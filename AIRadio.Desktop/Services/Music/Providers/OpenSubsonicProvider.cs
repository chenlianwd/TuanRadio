using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>OpenSubsonic 兼容服务器。账号配置整体保存于系统凭据库。</summary>
public sealed class OpenSubsonicProvider : IMusicProvider, IPrivateMediaOriginPolicy
{
    private const string CredentialService = "opensubsonic-config-v1";
    private readonly HttpClient _http;
    private readonly ISecureStorage _storage;
    private ServerConfig? _config;

    public MusicProviderDescriptor Descriptor { get; } = new(
        "opensubsonic", "OpenSubsonic", ProviderNetworkScope.UserConfiguredPrivateNetwork);
    public event EventHandler? ConfigurationChanged;
    public string ServerUrl => Volatile.Read(ref _config)?.ServerUrl ?? string.Empty;
    public string Username => Volatile.Read(ref _config)?.Username ?? string.Empty;
    public bool IsConfigured => Volatile.Read(ref _config) != null;

    public OpenSubsonicProvider(ISecureStorage storage, HttpClient? httpClient = null)
    {
        _storage = storage;
        // 私有服务器 API 不跟随重定向。跨主机跳转须由用户修改配置后重新测试。
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await _storage.GetApiKeyAsync(CredentialService).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<ServerConfig>(json);
            if (loaded != null && TryNormalizeServer(loaded.ServerUrl, out var uri) &&
                !string.IsNullOrWhiteSpace(loaded.Username) && !string.IsNullOrEmpty(loaded.Password))
            {
                Volatile.Write(ref _config, loaded with { ServerUrl = uri!.AbsoluteUri.TrimEnd('/') });
                ConfigurationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (JsonException)
        {
            // 凭据格式损坏时只禁用此源，不阻断其他音源。
        }
    }

    public async Task ConnectAsync(string serverUrl, string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeServer(serverUrl, out var server) || string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Invalid OpenSubsonic server or username");
        var previous = Volatile.Read(ref _config);
        if (string.IsNullOrEmpty(password) && previous != null &&
            string.Equals(previous.ServerUrl, server!.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(previous.Username, username.Trim(), StringComparison.Ordinal))
            password = previous.Password;
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("OpenSubsonic password is required");
        var candidate = new ServerConfig(server!.AbsoluteUri.TrimEnd('/'), username.Trim(), password);
        using var response = await GetJsonAsync(candidate, "ping", null, cancellationToken).ConfigureAwait(false);
        await _storage.SaveApiKeyAsync(CredentialService, JsonSerializer.Serialize(candidate)).ConfigureAwait(false);
        Volatile.Write(ref _config, candidate);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        _storage.DeleteApiKey(CredentialService);
        Volatile.Write(ref _config, null);
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        var config = Volatile.Read(ref _config);
        if (config == null || limit <= 0) return new List<OnlineTrack>();
        using var response = await GetJsonAsync(config, "search3", new Dictionary<string, string>
        {
            ["query"] = keyword,
            ["artistCount"] = "0",
            ["albumCount"] = "0",
            ["songCount"] = Math.Min(limit, 100).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement.GetProperty("subsonic-response");
        if (!root.TryGetProperty("searchResult3", out var result) ||
            !result.TryGetProperty("song", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return new List<OnlineTrack>();
        var tracks = new List<OnlineTrack>();
        foreach (var song in songs.EnumerateArray())
        {
            var id = GetString(song, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            tracks.Add(new OnlineTrack
            {
                Id = $"opensubsonic:{id}",
                Title = GetString(song, "title") ?? string.Empty,
                Artist = GetString(song, "artist") ?? string.Empty,
                Album = GetString(song, "album") ?? string.Empty,
                DurationMs = song.TryGetProperty("duration", out var duration) && duration.TryGetInt64(out var seconds)
                    ? Math.Max(0, seconds) * 1000 : 0,
                Source = Descriptor.DisplayName,
                ProviderMetadata = BuildMediaMetadata(song)
            });
        }
        return tracks;
    }

    public Task<MediaResolutionResult> ResolveAsync(ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = Volatile.Read(ref _config);
        if (config == null)
            return Task.FromResult(MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.AuthRequired));
        if (!string.Equals(track.ProviderId, Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(track.TrackId))
            return Task.FromResult(MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound));
        // stream 是二进制端点；LibVLC 需要可直接拉取的 URL，故只放一次性盐值及哈希令牌。
        var uri = BuildRequestUri(config, "stream", new Dictionary<string, string> { ["id"] = track.TrackId });
        var suffix = ReadMetadata(providerMetadata, "transcodedSuffix") ?? ReadMetadata(providerMetadata, "suffix");
        var bitrate = int.TryParse(ReadMetadata(providerMetadata, "transcodedBitRate"), out var kbps)
            ? kbps : (int?)null;
        return Task.FromResult(MediaResolutionResult.Playable(new ResolvedMedia(track, uri,
            Codec: suffix, Container: suffix, BitrateKbps: bitrate)));
    }

    public bool IsAllowedMediaUri(Uri uri)
    {
        var config = Volatile.Read(ref _config);
        if (config == null || !TryNormalizeServer(config.ServerUrl, out var server)) return false;
        var expected = new Uri(server!.AbsoluteUri.TrimEnd('/') + "/rest/stream.view");
        return uri.IsAbsoluteUri && uri.Scheme == expected.Scheme &&
               string.Equals(uri.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == expected.Port && uri.AbsolutePath == expected.AbsolutePath &&
               string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    }

    private async Task<JsonDocument> GetJsonAsync(ServerConfig config, string method,
        IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
    {
        var uri = BuildRequestUri(config, method, parameters);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("OpenSubsonic server redirected the request");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            var root = json.RootElement.GetProperty("subsonic-response");
            if (GetString(root, "status") != "ok")
            {
                var code = root.TryGetProperty("error", out var error) &&
                           error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                    ? value : 0;
                throw new MusicSourceBusinessException("OpenSubsonic API error " + code,
                    code is 40 or 41 or 42 or 43 or 44 ? MusicSourceFailureKind.AuthExpired : MusicSourceFailureKind.Unknown);
            }
            return json;
        }
        catch
        {
            json.Dispose();
            throw;
        }
    }

    private static Uri BuildRequestUri(ServerConfig config, string method,
        IReadOnlyDictionary<string, string>? parameters)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var token = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(config.Password + salt)))
            .ToLowerInvariant();
        var args = new Dictionary<string, string>
        {
            ["u"] = config.Username, ["t"] = token, ["s"] = salt,
            ["v"] = "1.16.1", ["c"] = "TuanRadio", ["f"] = "json"
        };
        if (parameters != null)
            foreach (var item in parameters) args[item.Key] = item.Value;
        var query = string.Join("&", args.Select(item =>
            Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));
        return new Uri(config.ServerUrl + "/rest/" + method + ".view?" + query);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static Dictionary<string, string> BuildMediaMetadata(JsonElement song)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "suffix", "contentType", "transcodedSuffix", "transcodedContentType" })
        {
            var value = GetString(song, name);
            if (!string.IsNullOrWhiteSpace(value)) metadata[name] = value;
        }
        if (song.TryGetProperty("transcodedBitRate", out var bitrate) && bitrate.TryGetInt32(out var kbps))
            metadata["transcodedBitRate"] = kbps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return metadata;
    }

    private static string? ReadMetadata(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata != null && metadata.TryGetValue(key, out var value) ? value : null;

    private static bool TryNormalizeServer(string? input, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(input?.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        uri = parsed;
        return true;
    }

    public sealed record ServerConfig(string ServerUrl, string Username, string Password);
}

/// <summary>私有网络源必须把播放地址收紧到用户配置的服务端。</summary>
public interface IPrivateMediaOriginPolicy
{
    bool IsAllowedMediaUri(Uri uri);
}
