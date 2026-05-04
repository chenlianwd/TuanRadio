using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 多平台聚合音乐搜索服务，集成酷我/酷狗/咪咕/网易云
/// </summary>
public class MultiSourceMusicService : IMusicSearchService
{
    private readonly HttpClient _httpClient;
    private readonly List<IMusicSearchService> _sources;

    public string Name => "多平台聚合";

    public MultiSourceMusicService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _sources = new List<IMusicSearchService>
        {
            new KuwoMusicService(httpClient),
            new KugouMusicService(httpClient),
            new MiguMusicService(httpClient),
            new NeteaseMusicService(httpClient)
        };
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        var tasks = _sources.Select(s => SearchWithFallback(s, keyword, limit));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).Take(limit * 2).ToList();
    }

    public async Task<string?> GetPlayUrlAsync(string trackId)
    {
        // trackId format: "source:id"
        var parts = trackId.Split(':', 2);
        if (parts.Length == 2)
        {
            var source = _sources.FirstOrDefault(s =>
                s.GetType().Name.Replace("MusicService", "").ToLower() == parts[0].ToLower());
            if (source != null)
                return await source.GetPlayUrlAsync(parts[1]);
        }

        // Try all sources
        foreach (var source in _sources)
        {
            try
            {
                var url = await source.GetPlayUrlAsync(trackId);
                if (url != null) return url;
            }
            catch { }
        }

        return null;
    }

    private async Task<List<OnlineTrack>> SearchWithFallback(IMusicSearchService source, string keyword, int limit)
    {
        try
        {
            return await source.SearchAsync(keyword, limit);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Source {Name} search failed", source.Name);
            return new List<OnlineTrack>();
        }
    }
}
