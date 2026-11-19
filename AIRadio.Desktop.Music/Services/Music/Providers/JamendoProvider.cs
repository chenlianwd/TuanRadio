using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>Jamendo 开放曲库（CC 许可独立/氛围/纯音乐）。需用户配置 Jamendo Developer Client ID。</summary>
public sealed class JamendoProvider : IMusicProvider
{
    private const string CredentialService = "jamendo-client-id-v1";
    private static readonly Uri ApiRoot = new("https://api.jamendo.com/v3.0/");
    private static readonly Regex NumericIdRegex = new("^[0-9]+$", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly ISecureStorage? _storage;
    private string? _clientId;

    public MusicProviderDescriptor Descriptor { get; } = new(
        "jamendo", "Jamendo", IsExperimental: true);

    public event EventHandler? ConfigurationChanged;
    public bool HasClientId => !string.IsNullOrWhiteSpace(Volatile.Read(ref _clientId));
    public bool IsConfigured => HasClientId;

    public JamendoProvider(HttpClient? httpClient = null, ISecureStorage? storage = null)
    {
        _storage = storage;
        // 搜索与详情只读取 JSON；重定向不交给 HttpClient 自动跟随。
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage == null) return;
        var key = await _storage.GetApiKeyAsync(CredentialService).ConfigureAwait(false);
        var normalized = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var previous = Interlocked.Exchange(ref _clientId, normalized);
        if (!string.Equals(previous, normalized, StringComparison.Ordinal))
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage == null) throw new InvalidOperationException("Jamendo secure storage is unavailable");
        var key = (clientId ?? string.Empty).Trim();
        if (key.Length is < 1 or > 128 || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid Jamendo Client ID", nameof(clientId));
        await _storage.SaveApiKeyAsync(CredentialService, key).ConfigureAwait(false);
        Volatile.Write(ref _clientId, key);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearClientId()
    {
        if (_storage == null) throw new InvalidOperationException("Jamendo secure storage is unavailable");
        _storage.DeleteApiKey(CredentialService);
        Volatile.Write(ref _clientId, null);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        var clientId = Volatile.Read(ref _clientId);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(keyword) || limit <= 0)
            return new List<OnlineTrack>();

        var clampedLimit = Math.Min(limit, 100);
        var uri = new Uri(ApiRoot,
            "tracks/?client_id=" + Uri.EscapeDataString(clientId) +
            "&format=json&audioformat=mp32&limit=" + clampedLimit.ToString(CultureInfo.InvariantCulture) +
            "&search=" + Uri.EscapeDataString(keyword.Trim()));

        using var json = await GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        EnsureHeadersOk(json.RootElement);

        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Jamendo search response has no results array");

        var tracks = new List<OnlineTrack>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            var title = ReadString(item, "name");
            var audio = ReadString(item, "audio");
            if (string.IsNullOrWhiteSpace(id) ||
                !NumericIdRegex.IsMatch(id) ||
                string.IsNullOrWhiteSpace(title) ||
                !TryParseHttpUri(audio, out _))
            {
                continue;
            }

            var artist = ReadString(item, "artist_name");
            var album = ReadString(item, "album_name");
            var durationMs = ReadDurationMs(item);
            tracks.Add(new OnlineTrack
            {
                Id = "jamendo:" + id,
                Title = title,
                Artist = artist ?? string.Empty,
                Album = album ?? string.Empty,
                DurationMs = durationMs,
                Source = Descriptor.DisplayName
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

        var clientId = Volatile.Read(ref _clientId);
        if (string.IsNullOrWhiteSpace(clientId))
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.AuthRequired);

        var uri = new Uri(ApiRoot,
            "tracks/?client_id=" + Uri.EscapeDataString(clientId) +
            "&format=json&audioformat=mp32&id=" + Uri.EscapeDataString(track.TrackId));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        if ((int)response.StatusCode is >= 300 and < 400)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.TransportRejected);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var code = ReadHeaderCode(json.RootElement);
        if (code != 0)
        {
            return code is 4 or 5
                ? MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.AuthRequired)
                : MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.SourceUnavailable, retryable: true);
        }

        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Jamendo track response has no results array");

        JsonElement? matched = null;
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                string.Equals(ReadString(item, "id"), track.TrackId, StringComparison.Ordinal))
            {
                matched = item;
                break;
            }
        }
        if (matched is not { } trackJson)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);

        var audioUrl = ReadString(trackJson, "audio");
        if (!TryParseHttpUri(audioUrl, out var audioUri))
        {
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        }

        return MediaResolutionResult.Playable(new ResolvedMedia(
            track,
            audioUri,
            Codec: "MP3",
            BitrateKbps: 192));
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Jamendo search redirected the request");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureHeadersOk(JsonElement root)
    {
        var code = ReadHeaderCode(root);
        if (code == 0) return;
        throw new MusicSourceBusinessException(
            $"Jamendo API error {code}",
            code is 4 or 5 ? MusicSourceFailureKind.AuthExpired : MusicSourceFailureKind.Unknown);
    }

    private static int ReadHeaderCode(JsonElement root)
    {
        if (!root.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return 0;
        if (headers.TryGetProperty("code", out var codeEl))
        {
            if (codeEl.ValueKind == JsonValueKind.Number && codeEl.TryGetInt32(out var code))
                return code;
            if (codeEl.ValueKind == JsonValueKind.String &&
                int.TryParse(codeEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        if (string.Equals(ReadString(headers, "status"), "failed", StringComparison.OrdinalIgnoreCase))
            return -1;
        return 0;
    }

    private static long ReadDurationMs(JsonElement item)
    {
        if (!item.TryGetProperty("duration", out var dur)) return 0;
        if (dur.ValueKind == JsonValueKind.Number && dur.TryGetInt64(out var seconds))
            return Math.Clamp(seconds, 0, long.MaxValue / 1000) * 1000;
        if (dur.ValueKind == JsonValueKind.String &&
            long.TryParse(dur.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return Math.Clamp(parsed, 0, long.MaxValue / 1000) * 1000;
        return 0;
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
