using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
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

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        try
        {
            var url = $"{_baseUrl}/search?keywords={Uri.EscapeDataString(keyword)}&limit={limit}";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.GetProperty("code").GetInt32() != 200)
                return new List<OnlineTrack>();

            var songs = root.GetProperty("result").GetProperty("songs");
            var tracks = new List<OnlineTrack>();

            foreach (var song in songs.EnumerateArray())
            {
                var artists = song.GetProperty("artists");
                var artistName = artists.GetArrayLength() > 0
                    ? artists[0].GetProperty("name").GetString() ?? "未知"
                    : "未知";

                tracks.Add(new OnlineTrack
                {
                    Id = "netease:" + song.GetProperty("id").GetInt64().ToString(),
                    Title = song.GetProperty("name").GetString() ?? "",
                    Artist = artistName,
                    Album = song.GetProperty("album").GetProperty("name").GetString() ?? "",
                    DurationMs = song.GetProperty("duration").GetInt64(),
                    Source = "netease"
                });
            }

            return tracks;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease search failed");
            return new List<OnlineTrack>();
        }
    }

    public async Task<string?> GetPlayUrlAsync(string trackId)
    {
        try
        {
            var url = $"{_baseUrl}/song/url/v1?id={trackId}&level=exhigh";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.GetProperty("code").GetInt32() != 200)
                return null;

            var data = root.GetProperty("data");
            if (data.GetArrayLength() > 0)
            {
                var playUrl = data[0].GetProperty("url").GetString();
                return string.IsNullOrEmpty(playUrl) ? null : playUrl;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Netease get play url failed for {Id}", trackId);
            return null;
        }
    }
}
