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
            var url = $"{_baseUrl}/song/url/v1?id={Uri.EscapeDataString(trackId)}&level=exhigh";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 200)
                return null;

            if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("url", out var urlEl))
                {
                    var playUrl = urlEl.GetString();
                    return string.IsNullOrEmpty(playUrl) ? null : playUrl;
                }
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
