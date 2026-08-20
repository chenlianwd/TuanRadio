using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public class NeteaseMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public string Name => "网易云音乐";

    public NeteaseMusicService(HttpClient httpClient, string baseUrl = "http://127.0.0.1:37250")
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/search?keywords={Uri.EscapeDataString(keyword)}&limit={limit}";
            var response = await _httpClient.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200)
                return new List<OnlineTrack>();

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs))
                return new List<OnlineTrack>();

            var tracks = new List<OnlineTrack>();

            foreach (var song in songs.EnumerateArray())
            {
                var artistName = "未知";
                if (song.TryGetProperty("artists", out var artists) &&
                    artists.GetArrayLength() > 0 &&
                    artists[0].TryGetProperty("name", out var nameEl))
                {
                    artistName = nameEl.GetString() ?? "未知";
                }

                var albumName = "";
                if (song.TryGetProperty("album", out var album) &&
                    album.TryGetProperty("name", out var albumEl))
                {
                    albumName = albumEl.GetString() ?? "";
                }

                tracks.Add(new OnlineTrack
                {
                    Id = "netease:" + (song.TryGetProperty("id", out var idEl) ? idEl.GetInt64().ToString() : "0"),
                    Title = song.TryGetProperty("name", out var titleEl) ? titleEl.GetString() ?? "" : "",
                    Artist = artistName,
                    Album = albumName,
                    DurationMs = song.TryGetProperty("duration", out var durEl) ? durEl.GetInt64() : 0,
                    Source = "网易"
                });
            }

            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_baseUrl}/song/url/v1?id={Uri.EscapeDataString(trackId)}&level=exhigh";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 播放地址是带签名的临时 CDN 链接，重试时不能复用本地 API 的 2 分钟缓存。
            request.Headers.TryAddWithoutValidation("x-apicache-bypass", "true");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200)
                return null;

            if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (IsTrialOrRestricted(first))
                {
                    Log.Information("Netease returned a trial-only stream for {Id}; trying another source", trackId);
                    return null;
                }

                if (first.TryGetProperty("url", out var urlEl))
                {
                    var playUrl = urlEl.GetString();
                    return string.IsNullOrEmpty(playUrl) ? null : playUrl;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease get play url failed for {Id}", trackId);
            return null;
        }
    }

    private static bool IsTrialOrRestricted(JsonElement item)
    {
        if (item.TryGetProperty("freeTrialInfo", out var trialInfo) &&
            trialInfo.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (!item.TryGetProperty("freeTrialPrivilege", out var privilege) ||
            privilege.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetPositiveInt32(privilege, "listenType") ||
               TryGetPositiveInt32(privilege, "cannotListenReason");
    }

    private static bool TryGetPositiveInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number) &&
           number > 0;
}
