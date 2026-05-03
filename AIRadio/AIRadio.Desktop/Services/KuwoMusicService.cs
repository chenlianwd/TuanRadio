using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        var url = $"http://www.kuwo.cn/api/www/search/searchMusicByhttp?key={Uri.EscapeDataString(keyword)}&pn=1&rn={limit}&httpsStatus=1";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Referer", "http://www.kuwo.cn/");
        request.Headers.Add("csrf", "0");
        request.Headers.Add("Cookie", "kw_token=0");

        var response = await _httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.GetProperty("code").GetInt32() != 200)
            return new List<OnlineTrack>();

        var tracks = new List<OnlineTrack>();
        var data = root.GetProperty("data").GetProperty("list");

        foreach (var item in data.EnumerateArray())
        {
            tracks.Add(new OnlineTrack
            {
                Id = "kuwo:" + item.GetProperty("rid").GetInt64().ToString(),
                Title = item.GetProperty("name").GetString() ?? "",
                Artist = item.GetProperty("artist").GetString() ?? "",
                Album = item.GetProperty("album").GetString() ?? "",
                DurationMs = item.GetProperty("duration").GetInt32() * 1000L,
                Source = "酷我"
            });
        }

        return tracks;
    }

    public async Task<string?> GetPlayUrlAsync(string trackId)
    {
        // Strip source prefix if present
        var id = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
        try
        {
            var url = $"http://www.kuwo.cn/api/v1/www/music/playUrl?mid={id}&type=music&httpsStatus=1";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "http://www.kuwo.cn/");
            request.Headers.Add("csrf", "0");
            request.Headers.Add("Cookie", "kw_token=0");

            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("code").GetInt32() == 200)
            {
                return root.GetProperty("data").GetProperty("url").GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kuwo get play url failed for {Id}", id);
        }
        return null;
    }
}
