using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 酷我音乐 API
/// </summary>
public class KuwoMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    public string Name => "酷我音乐";

    public KuwoMusicService(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
            var url = $"https://www.kuwo.cn/api/www/search/searchMusicByhttp?key={Uri.EscapeDataString(keyword)}&pn=1&rn={limit}&httpsStatus=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.kuwo.cn/");
            // Kuwo requires these headers; the token value "0" works as a placeholder
            request.Headers.Add("csrf", "0");
            request.Headers.Add("Cookie", "kw_token=0");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                codeElement.GetInt32() != 200)
                throw new MusicSourceBusinessException($"酷我接口业务码异常({(codeElement.ValueKind == JsonValueKind.Number ? codeElement.GetInt32() : -1)})");

            var tracks = new List<OnlineTrack>();
            if (!root.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("list", out var listElement))
                return tracks;

            foreach (var item in listElement.EnumerateArray())
            {
                try
                {
                    tracks.Add(new OnlineTrack
                    {
                        Id = "kuwo:" + (item.TryGetProperty("rid", out var rid) ? rid.GetInt64().ToString() : "0"),
                        Title = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        Artist = item.TryGetProperty("artist", out var artist) ? artist.GetString() ?? "" : "",
                        Album = item.TryGetProperty("album", out var album) ? album.GetString() ?? "" : "",
                        DurationMs = item.TryGetProperty("duration", out var dur) ? dur.GetInt32() * 1000L : 0,
                        Source = "酷我"
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Skipped malformed Kuwo search item");
                }
            }

            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not MusicSourceBusinessException)
        {
            Log.Warning(ex, "Kuwo search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        // Strip source prefix if present
        var id = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
        try
        {
            var url = $"https://www.kuwo.cn/api/v1/www/music/playUrl?mid={id}&type=music&httpsStatus=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.kuwo.cn/");
            // Kuwo requires these headers; the token value "0" works as a placeholder
            request.Headers.Add("csrf", "0");
            request.Headers.Add("Cookie", "kw_token=0");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() == 200 &&
                root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("url", out var urlEl))
            {
                return urlEl.GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kuwo get play url failed for {Id}", id);
        }
        return null;
    }
}
