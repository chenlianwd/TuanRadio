using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>Audius 公开只读曲库。只使用无需账号的搜索与完整曲目播放接口。</summary>
public sealed class AudiusProvider : IMusicProvider
{
    private static readonly Uri ApiRoot = new("https://api.audius.co/v1/");
    private readonly HttpClient _http;

    public MusicProviderDescriptor Descriptor { get; } = new(
        "audius", "Audius", IsExperimental: true);

    public AudiusProvider(HttpClient? httpClient = null)
    {
        // 搜索只读取 JSON；重定向不交给 HttpClient 自动跟随。
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return new List<OnlineTrack>();
        var uri = new Uri(ApiRoot, "tracks/search?query=" + Uri.EscapeDataString(keyword.Trim()) +
            "&limit=" + Math.Min(limit, 100).ToString(CultureInfo.InvariantCulture) + "&app_name=TuanRadio");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("Audius search redirected the request");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Audius search response has no data array");
        var tracks = new List<OnlineTrack>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) ||
                !IsTrue(item, "is_streamable")) continue;
            // 未授权的门控曲目及明确不可流播曲目不可进入自动节目单。
            if (IsTrue(item, "is_stream_gated") ||
                IsFalse(item, "is_available") ||
                (item.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.Object &&
                 IsFalse(access, "stream"))) continue;
            var artist = item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? ReadString(user, "name") : null;
            var album = item.TryGetProperty("album_backlink", out var backlink) &&
                        backlink.ValueKind == JsonValueKind.Object
                ? ReadString(backlink, "playlist_name") : null;
            var duration = item.TryGetProperty("duration", out var seconds) && seconds.TryGetInt64(out var value)
                ? Math.Clamp(value, 0, long.MaxValue / 1000) * 1000 : 0;
            tracks.Add(new OnlineTrack
            {
                Id = "audius:" + id,
                Title = title,
                Artist = artist ?? string.Empty,
                Album = album ?? string.Empty,
                DurationMs = duration,
                Source = Descriptor.DisplayName
            });
            if (tracks.Count >= limit) break;
        }
        return tracks;
    }

    public async Task<MediaResolutionResult> ResolveAsync(ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(track.ProviderId, Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(track.TrackId) ||
            !System.Text.RegularExpressions.Regex.IsMatch(track.TrackId, "^[A-Za-z0-9]+$"))
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);

        // 收藏/历史曲目可能在搜索后变为门控；缓存未命中时重新核验当前可播状态。
        var detailUri = new Uri(ApiRoot, "tracks/" + track.TrackId + "?app_name=TuanRadio");
        using var response = await _http.GetAsync(detailUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        if ((int)response.StatusCode is >= 300 and < 400)
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.TransportRejected);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("data", out var item) || item.ValueKind != JsonValueKind.Object)
            throw new JsonException("Audius track response has no data object");
        if (!string.Equals(ReadString(item, "id"), track.TrackId, StringComparison.Ordinal))
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound);
        if (IsTrue(item, "is_stream_gated") ||
            (item.TryGetProperty("access", out var access) && access.ValueKind == JsonValueKind.Object &&
             IsFalse(access, "stream")))
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.AuthRequired);
        if (!IsTrue(item, "is_streamable") || IsFalse(item, "is_available"))
            return MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.SourceUnavailable);
        // Audius 返回公开 API 地址，实际媒体会重定向到内容节点。
        var uri = new Uri(ApiRoot, "tracks/" + track.TrackId + "/stream?app_name=TuanRadio");
        return MediaResolutionResult.Playable(new ResolvedMedia(track, uri));
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool IsTrue(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
}
