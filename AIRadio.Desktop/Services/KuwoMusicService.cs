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
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.Number ||
                codeElement.GetInt32() != 200)
                throw new MusicSourceBusinessException(AppLanguage.T(
                    $"酷我接口业务码异常({(codeElement.ValueKind == JsonValueKind.Number ? codeElement.GetInt32() : -1)})",
                    $"Kuwo returned an unexpected business code ({(codeElement.ValueKind == JsonValueKind.Number ? codeElement.GetInt32() : -1)})"),
                    MusicSourceFailureKind.ApiBroken);

            var tracks = new List<OnlineTrack>();
            // TryGetProperty 仅在 ValueKind==Object 时返回 bool，data:null 等形状会直接抛
            // InvalidOperationException，被聚合层误判为传输故障触发熔断（酷我受限曲常见此形状）
            if (!root.TryGetProperty("data", out var dataElement) ||
                dataElement.ValueKind != JsonValueKind.Object ||
                !dataElement.TryGetProperty("list", out var listElement) ||
                listElement.ValueKind != JsonValueKind.Array)
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
            throw;
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
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // code/data 同样做形状防御：受限曲返回 code:200 + data:null，按"无地址"处理
            if (root.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.Number && codeEl.GetInt32() == 200 &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("url", out var urlEl))
            {
                return urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kuwo get play url failed for {Id}", id);
            throw;
        }
        return null;
    }
}
