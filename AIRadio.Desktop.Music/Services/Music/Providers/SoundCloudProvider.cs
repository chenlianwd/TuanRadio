using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// SoundCloud 开放曲库（OAuth 2.0 Client Credentials）。
/// 严格遵守署名（歌手 + permalink_url 元数据）与可播放性限制：仅收录允许站外流播（streamable 且 access=playable）的曲目，拒绝将试听流当作完整播放。
/// </summary>
public sealed class SoundCloudProvider : IMusicProvider
{
    private const string CredentialClientIdService = "soundcloud-client-id-v1";
    private const string CredentialClientSecretService = "soundcloud-client-secret-v1";
    private static readonly Uri ApiRoot = new("https://api.soundcloud.com/");
    private static readonly Uri TokenEndpoint = new("https://secure.soundcloud.com/oauth/token");
    private static readonly Regex NumericIdRegex = new("^[0-9]+$", RegexOptions.Compiled);
    private static readonly (string Field, string Codec, int BitrateKbps)[] FullStreamPriority =
    {
        ("http_mp3_128_url", "MP3", 128),
        ("hls_aac_160_url", "AAC", 160),
        ("hls_aac_96_url", "AAC", 96),
        ("hls_mp3_128_url", "MP3", 128),
        ("hls_opus_64_url", "OPUS", 64),
    };

    private sealed record CachedToken(
        string Token,
        DateTimeOffset ExpiresAtUtc,
        string ClientId,
        string ClientSecret);

    private readonly HttpClient _http;
    private readonly ISecureStorage? _storage;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _clientId;
    private string? _clientSecret;
    private CachedToken? _cachedToken;

    public MusicProviderDescriptor Descriptor { get; } = new(
        "soundcloud", "SoundCloud", IsExperimental: true);

    public event EventHandler? ConfigurationChanged;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Volatile.Read(ref _clientId)) &&
        !string.IsNullOrWhiteSpace(Volatile.Read(ref _clientSecret));

    public bool IsConfigured => HasCredentials;

    public SoundCloudProvider(HttpClient? httpClient = null, ISecureStorage? storage = null)
    {
        _storage = storage;
        // JSON 接口与 OAuth 令牌请求禁止 HttpClient 自动跟随重定向。
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage == null) return;

        var rawId = await _storage.GetApiKeyAsync(CredentialClientIdService).ConfigureAwait(false);
        var rawSecret = await _storage.GetApiKeyAsync(CredentialClientSecretService).ConfigureAwait(false);
        var normalizedId = string.IsNullOrWhiteSpace(rawId) ? null : rawId.Trim();
        var normalizedSecret = string.IsNullOrWhiteSpace(rawSecret) ? null : rawSecret.Trim();

        var previousId = Interlocked.Exchange(ref _clientId, normalizedId);
        var previousSecret = Interlocked.Exchange(ref _clientSecret, normalizedSecret);
        if (!string.Equals(previousId, normalizedId, StringComparison.Ordinal) ||
            !string.Equals(previousSecret, normalizedSecret, StringComparison.Ordinal))
        {
            InvalidateCachedToken();
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SaveCredentialsAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage == null)
            throw new InvalidOperationException("SoundCloud secure storage is unavailable");

        var id = (clientId ?? string.Empty).Trim();
        var secret = (clientSecret ?? string.Empty).Trim();
        if (id.Length is < 1 or > 256 || id.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid SoundCloud Client ID", nameof(clientId));
        if (secret.Length is < 1 or > 256 || secret.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid SoundCloud Client Secret", nameof(clientSecret));

        await _storage.SaveApiKeyAsync(CredentialClientIdService, id).ConfigureAwait(false);
        await _storage.SaveApiKeyAsync(CredentialClientSecretService, secret).ConfigureAwait(false);
        Volatile.Write(ref _clientId, id);
        Volatile.Write(ref _clientSecret, secret);
        InvalidateCachedToken();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCredentials()
    {
        if (_storage == null)
            throw new InvalidOperationException("SoundCloud secure storage is unavailable");

        _storage.DeleteApiKey(CredentialClientIdService);
        _storage.DeleteApiKey(CredentialClientSecretService);
        Volatile.Write(ref _clientId, null);
        Volatile.Write(ref _clientSecret, null);
        InvalidateCachedToken();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(keyword) || limit <= 0)
            return new List<OnlineTrack>();

        var clampedLimit = Math.Min(limit, 50);
        var uri = new Uri(ApiRoot,
            "tracks?q=" + Uri.EscapeDataString(keyword.Trim()) +
            "&limit=" + clampedLimit.ToString(CultureInfo.InvariantCulture) +
            "&access=playable&linked_partitioning=1");

        using var response = await SendAuthorizedGetWithRetryAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MusicSourceBusinessException("SoundCloud authentication failed", MusicSourceFailureKind.AuthExpired);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("SoundCloud search redirected the request");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        JsonElement items;
        if (json.RootElement.ValueKind == JsonValueKind.Array)
            items = json.RootElement;
        else if (json.RootElement.ValueKind == JsonValueKind.Object &&
                 json.RootElement.TryGetProperty("collection", out var collection) &&
                 collection.ValueKind == JsonValueKind.Array)
            items = collection;
        else
            throw new JsonException("SoundCloud search response has no tracks array");

        var tracks = new List<OnlineTrack>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = ReadFlexibleId(item, "id");
            var title = ReadString(item, "title");
            var permalink = ReadString(item, "permalink_url");
            if (string.IsNullOrWhiteSpace(id) ||
                !NumericIdRegex.IsMatch(id) ||
                string.IsNullOrWhiteSpace(title) ||
                !TryParseHttpUri(permalink, out _) ||
                !IsPlayableOffPlatform(item, out _))
            {
                continue;
            }

            var artist = ReadArtistName(item);
            var durationMs = ReadDurationMs(item);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["permalink_url"] = permalink!
            };
            var license = ReadString(item, "license");
            if (!string.IsNullOrWhiteSpace(license))
                metadata["license"] = license;

            tracks.Add(new OnlineTrack
            {
                Id = "soundcloud:" + id,
                Title = title,
                Artist = artist,
                Album = string.Empty,
                DurationMs = durationMs,
                Source = Descriptor.DisplayName,
                ProviderMetadata = metadata
            });
            if (tracks.Count >= limit) break;
        }
        return tracks;
    }

    public async Task<MediaResolutionResult> ResolveAsync(
        ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(track.ProviderId, Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(track.TrackId) ||
            !NumericIdRegex.IsMatch(track.TrackId))
        {
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        }

        if (!IsConfigured)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.AuthRequired);

        // 1) 复核曲目详情与可播授权（拒绝试听/地区封锁/禁止外放）
        var trackUri = new Uri(ApiRoot, "tracks/" + Uri.EscapeDataString(track.TrackId));
        try
        {
            using var trackResponse = await SendAuthorizedGetWithRetryAsync(trackUri, cancellationToken).ConfigureAwait(false);
            if (TryMapFailureStatus(trackResponse.StatusCode, out var failure))
                return MediaResolutionResult.Failed(track.ProviderId, failure);
            trackResponse.EnsureSuccessStatusCode();

            await using var trackStream = await trackResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var trackJson = await JsonDocument.ParseAsync(trackStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (trackJson.RootElement.ValueKind != JsonValueKind.Object)
                return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
            if (!IsPlayableOffPlatform(trackJson.RootElement, out var accessFailure))
                return MediaResolutionResult.Failed(track.ProviderId, accessFailure);
        }
        catch (MusicSourceBusinessException ex)
        {
            return MediaResolutionResult.Failed(track.ProviderId, MapBusinessFailure(ex.Kind));
        }

        // 2) 拉取流地址映射，拒绝仅返回 preview 切片的曲目
        var streamsUri = new Uri(ApiRoot, "tracks/" + Uri.EscapeDataString(track.TrackId) + "/streams");
        try
        {
            using var streamsResponse = await SendAuthorizedGetWithRetryAsync(streamsUri, cancellationToken).ConfigureAwait(false);
            if (TryMapFailureStatus(streamsResponse.StatusCode, out var streamsFailure))
                return MediaResolutionResult.Failed(track.ProviderId, streamsFailure);
            streamsResponse.EnsureSuccessStatusCode();

            await using var stream = await streamsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var streamsJson = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (streamsJson.RootElement.ValueKind != JsonValueKind.Object)
                return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);

            foreach (var (field, codec, bitrate) in FullStreamPriority)
            {
                var url = ReadString(streamsJson.RootElement, field);
                if (TryParseHttpUri(url, out var mediaUri))
                {
                    return MediaResolutionResult.Playable(new ResolvedMedia(
                        track,
                        mediaUri,
                        Codec: codec,
                        BitrateKbps: bitrate));
                }
            }

            var previewUrl = ReadString(streamsJson.RootElement, "preview_mp3_128_url");
            if (TryParseHttpUri(previewUrl, out _))
                return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.PreviewOnly);

            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        }
        catch (MusicSourceBusinessException ex)
        {
            return MediaResolutionResult.Failed(track.ProviderId, MapBusinessFailure(ex.Kind));
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedGetWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(staleToken: null, cancellationToken).ConfigureAwait(false);
        var response = await SendGetAsync(uri, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        InvalidateCachedToken(token);
        var refreshedToken = await GetAccessTokenAsync(staleToken: token, cancellationToken).ConfigureAwait(false);
        return await SendGetAsync(uri, refreshedToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(string? staleToken, CancellationToken cancellationToken)
    {
        if (TryGetValidCachedToken(staleToken, out var cached))
            return cached;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetValidCachedToken(staleToken, out cached))
                return cached;

            var clientId = Volatile.Read(ref _clientId);
            var clientSecret = Volatile.Read(ref _clientSecret);
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new MusicSourceBusinessException("SoundCloud credentials not configured", MusicSourceFailureKind.NotSignedIn);

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                throw new MusicSourceBusinessException("SoundCloud OAuth token rejected", MusicSourceFailureKind.AuthExpired);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("SoundCloud OAuth endpoint redirected the request");
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var accessToken = ReadString(doc.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new JsonException("SoundCloud OAuth response missing access_token");

            var expiresInSeconds = 3600L;
            if (doc.RootElement.TryGetProperty("expires_in", out var expEl) &&
                expEl.ValueKind == JsonValueKind.Number &&
                expEl.TryGetInt64(out var parsedExp))
            {
                expiresInSeconds = Math.Clamp(parsedExp, 60, 86400);
            }

            // 凭据在 HTTP 往返期间若已被清除或替换，严禁把旧凭据签发的令牌写入缓存
            var currentId = Volatile.Read(ref _clientId);
            var currentSecret = Volatile.Read(ref _clientSecret);
            if (!string.Equals(currentId, clientId, StringComparison.Ordinal) ||
                !string.Equals(currentSecret, clientSecret, StringComparison.Ordinal))
            {
                throw new MusicSourceBusinessException(
                    "SoundCloud credentials changed during token acquisition",
                    string.IsNullOrWhiteSpace(currentId) || string.IsNullOrWhiteSpace(currentSecret)
                        ? MusicSourceFailureKind.NotSignedIn
                        : MusicSourceFailureKind.AuthExpired);
            }

            var entry = new CachedToken(
                accessToken,
                DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
                clientId,
                clientSecret);
            Volatile.Write(ref _cachedToken, entry);
            return accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private bool TryGetValidCachedToken(string? staleToken, out string token)
    {
        var cached = Volatile.Read(ref _cachedToken);
        var clientId = Volatile.Read(ref _clientId);
        var clientSecret = Volatile.Read(ref _clientSecret);
        if (cached != null &&
            !string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(clientSecret) &&
            string.Equals(cached.ClientId, clientId, StringComparison.Ordinal) &&
            string.Equals(cached.ClientSecret, clientSecret, StringComparison.Ordinal) &&
            !string.Equals(cached.Token, staleToken, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow.AddSeconds(60) < cached.ExpiresAtUtc)
        {
            token = cached.Token;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private void InvalidateCachedToken(string? expectedToken = null)
    {
        if (expectedToken == null)
        {
            Volatile.Write(ref _cachedToken, null);
            return;
        }

        var current = Volatile.Read(ref _cachedToken);
        if (current != null && string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            Interlocked.CompareExchange(ref _cachedToken, null, current);
    }

    private static PlaybackFailureKind MapBusinessFailure(MusicSourceFailureKind kind)
        => kind is MusicSourceFailureKind.AuthExpired or MusicSourceFailureKind.NotSignedIn
            ? PlaybackFailureKind.AuthRequired
            : PlaybackFailureKind.SourceUnavailable;

    private static bool TryMapFailureStatus(HttpStatusCode statusCode, out PlaybackFailureKind failure)
    {
        if (statusCode == HttpStatusCode.NotFound)
        {
            failure = PlaybackFailureKind.NotFound;
            return true;
        }
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            failure = PlaybackFailureKind.AuthRequired;
            return true;
        }
        if ((int)statusCode is >= 300 and < 400)
        {
            failure = PlaybackFailureKind.TransportRejected;
            return true;
        }
        failure = PlaybackFailureKind.None;
        return false;
    }

    private static bool IsPlayableOffPlatform(JsonElement item, out PlaybackFailureKind failure)
    {
        if (item.TryGetProperty("streamable", out var streamableEl) &&
            streamableEl.ValueKind == JsonValueKind.False)
        {
            failure = PlaybackFailureKind.SourceUnavailable;
            return false;
        }

        var access = ReadString(item, "access");
        if (!string.IsNullOrWhiteSpace(access))
        {
            if (string.Equals(access, "preview", StringComparison.OrdinalIgnoreCase))
            {
                failure = PlaybackFailureKind.PreviewOnly;
                return false;
            }
            if (string.Equals(access, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                failure = PlaybackFailureKind.RegionRestricted;
                return false;
            }
            if (!string.Equals(access, "playable", StringComparison.OrdinalIgnoreCase))
            {
                failure = PlaybackFailureKind.SourceUnavailable;
                return false;
            }
        }

        failure = PlaybackFailureKind.None;
        return true;
    }

    private static string ReadArtistName(JsonElement item)
    {
        if (!item.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            return string.Empty;
        return ReadString(user, "username") ?? ReadString(user, "full_name") ?? string.Empty;
    }

    private static long ReadDurationMs(JsonElement item)
    {
        if (!item.TryGetProperty("duration", out var dur)) return 0;
        if (dur.ValueKind == JsonValueKind.Number && dur.TryGetInt64(out var ms))
            return Math.Max(0, ms);
        if (dur.ValueKind == JsonValueKind.String &&
            long.TryParse(dur.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return Math.Max(0, parsed);
        return 0;
    }

    private static string? ReadFlexibleId(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
            return id.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static bool TryParseHttpUri(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            uri = parsed;
            return true;
        }
        uri = null;
        return false;
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
