using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 酷狗音乐 API
/// </summary>
public class KugouMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    public string Name => "酷狗音乐";

    public KugouMusicService(HttpClient httpClient)
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
            var url = $"https://complexsearch.kugou.com/v2/search/song?callback=callback&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize={limit}&bitrate=0&isfuzzy=0&inputtype=0&platform=WebFilter&userid=0&clientver=20000&iscorrection=1&privilege_filter=0&token=";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.kugou.com/");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            // Kugou returns JSONP, strip callback wrapper
            var json = text;
            var start = text.IndexOf('(');
            var end = text.LastIndexOf(')');
            if (start > 0 && end > start)
                json = text.Substring(start + 1, end - start - 1);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tracks = new List<OnlineTrack>();
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("lists", out var lists))
            {
                foreach (var item in lists.EnumerateArray())
                {
                    if (!item.TryGetProperty("SongName", out var songNameEl) ||
                        !item.TryGetProperty("SingerName", out var singerNameEl) ||
                        !item.TryGetProperty("FileHash", out var fileIdEl))
                        continue;

                    var songName = songNameEl.GetString() ?? "";
                    var singerName = singerNameEl.GetString() ?? "";
                    var albumName = item.TryGetProperty("AlbumName", out var a) ? a.GetString() ?? "" : "";
                    var fileId = fileIdEl.GetString() ?? "";
                    var duration = item.TryGetProperty("Duration", out var d) ? d.GetInt32() : 0;

                    tracks.Add(new OnlineTrack
                    {
                        Id = "kugou:" + fileId,
                        Title = songName,
                        Artist = singerName,
                        Album = albumName,
                        DurationMs = duration * 1000L,
                        Source = "酷狗"
                    });
                }
            }

            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kugou search failed");
            return new List<OnlineTrack>();
        }
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        var hash = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
        try
        {
            // appid=1014, platid=4 are Kugou's public web client identifiers
            var url = $"https://wwwapi.kugou.com/yy/index.php?r=play/getdata&hash={hash}&appid=1014&mid=&platid=4&album_id=";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.kugou.com/");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("play_url", out var playUrl))
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
            Log.Warning(ex, "Kugou get play url failed for {Hash}", hash);
        }
        return null;
    }
}
