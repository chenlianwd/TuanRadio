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
    private static readonly TimeSpan PrimarySourceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _httpClient;
    private readonly List<IMusicSearchService> _sources;

    public string Name => "多平台聚合";

    public MultiSourceMusicService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _sources = new List<IMusicSearchService>
        {
            new NeteaseMusicService(httpClient),
            new KuwoMusicService(httpClient),
            new KugouMusicService(httpClient),
            new MiguMusicService(httpClient)
        };
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        var primary = _sources.FirstOrDefault();
        if (primary != null)
        {
            var primaryResults = await SearchWithFallback(primary, keyword, limit, PrimarySourceTimeout);
            if (primaryResults.Count > 0)
            {
                Log.Information("Music search '{Keyword}' returned {Count} result(s) from primary source {Source}", keyword, primaryResults.Count, primary.Name);
                return primaryResults.Take(limit * 2).ToList();
            }
        }

        var tasks = _sources.Skip(1).Select(s => SearchWithFallback(s, keyword, limit, SourceTimeout));
        var results = await Task.WhenAll(tasks);
        var merged = results.SelectMany(r => r).Take(limit * 2).ToList();
        Log.Information("Music search '{Keyword}' returned {Count} fallback result(s)", keyword, merged.Count);
        return merged;
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
                return await GetPlayUrlWithTimeout(source, parts[1]);
        }

        // Try all sources
        foreach (var source in _sources)
        {
            try
            {
                var url = await GetPlayUrlWithTimeout(source, trackId);
                if (url != null) return url;
            }
            catch { }
        }

        return null;
    }

    private async Task<List<OnlineTrack>> SearchWithFallback(IMusicSearchService source, string keyword, int limit, TimeSpan timeout)
    {
        try
        {
            return await source.SearchAsync(keyword, limit).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Log.Warning("Source {Name} search timed out after {Seconds}s", source.Name, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Source {Name} search failed", source.Name);
            return new List<OnlineTrack>();
        }
    }

    private static async Task<string?> GetPlayUrlWithTimeout(IMusicSearchService source, string trackId)
    {
        try
        {
            return await source.GetPlayUrlAsync(trackId).WaitAsync(SourceTimeout);
        }
        catch (TimeoutException)
        {
            Log.Warning("Source {Name} play URL timed out after {Seconds}s for {Id}", source.Name, SourceTimeout.TotalSeconds, trackId);
            return null;
        }
    }
}
