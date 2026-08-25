using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 咪咕音乐 API
/// </summary>
public class MiguMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    public string Name => "咪咕音乐";

    public MiguMusicService(HttpClient httpClient)
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
            var url = $"https://m.music.migu.cn/migu/remoting/scr_search_tag?keyword={Uri.EscapeDataString(keyword)}&pgc=1&rows={limit}&type=2";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://m.music.migu.cn/");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json) || json[0] == '<')
                throw new MusicSourceBusinessException(AppLanguage.T(
                    "咪咕接口返回了非 JSON 响应（可能被门户页劫持）",
                    "Migu returned a non-JSON response, possibly redirected to a portal page"));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tracks = new List<OnlineTrack>();
            if (root.TryGetProperty("musics", out var musics))
            {
                foreach (var item in musics.EnumerateArray())
                {
                    try
                    {
                        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" :
                                 item.TryGetProperty("copyrightId", out var cId) ? cId.GetString() ?? "" : "";
                        var title = item.TryGetProperty("songName", out var t) ? t.GetString() ?? "" :
                                    item.TryGetProperty("title", out var t2) ? t2.GetString() ?? "" : "";
                        var artist = item.TryGetProperty("singerName", out var ar) ? ar.GetString() ?? "" :
                                     item.TryGetProperty("singer", out var ar2) ? ar2.GetString() ?? "" : "";
                        var album = item.TryGetProperty("albumName", out var al) ? al.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(id))
                        {
                            tracks.Add(new OnlineTrack
                            {
                                Id = "migu:" + id,
                                Title = title,
                                Artist = artist,
                                Album = album,
                                DurationMs = 0,
                                Source = "咪咕"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "Skipped malformed Migu search item");
                    }
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
            Log.Warning(ex, "Migu search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var id = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
        try
        {
            var url = $"https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/queryListenSongInfo.do?copyrightId={id}&toneType=SQ&netType=01";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Linux; Android 11) AppleWebKit/537.36");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("listenUrl", out var listenUrl))
            {
                // 空字符串视为无地址，继续尝试备用端点，避免"首端点空值即放弃"
                var primaryUrl = listenUrl.GetString();
                if (!string.IsNullOrWhiteSpace(primaryUrl))
                    return primaryUrl;
            }

            // Try alternative endpoint
            var url2 = $"https://music.migu.cn/v3/api/music/audioPlayer/getSongInfo?copyrightId={id}";
            using var request2 = new HttpRequestMessage(HttpMethod.Get, url2);
            request2.Headers.Add("Referer", "https://music.migu.cn/");

            using var response2 = await _httpClient.SendAsync(request2, cancellationToken);
            var json2 = await response2.Content.ReadAsStringAsync(cancellationToken);
            using var doc2 = JsonDocument.Parse(json2);

            if (doc2.RootElement.TryGetProperty("data", out var data2) &&
                data2.TryGetProperty("playUrl", out var playUrl))
            {
                return playUrl.GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Migu get play url failed for {Id}", id);
        }
        return null;
    }
}
