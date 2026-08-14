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

    /// <summary>最近一次搜索的各源状态（供 UI 透传具体失败原因，子项目 5）。</summary>
    public List<SourceSearchStatus> LastSearchReport { get; } = new();

    public MultiSourceMusicService(HttpClient httpClient, params IMusicSearchService[] extraSources)
    {
        _httpClient = httpClient;
        _sources = new List<IMusicSearchService>
        {
            new NeteaseMusicService(httpClient),
            new KuwoMusicService(httpClient),
            new KugouMusicService(httpClient),
            new MiguMusicService(httpClient)
        };
        _sources.AddRange(extraSources); // YouTube 等额外源作为最低优先级
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        LastSearchReport.Clear();
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
            catch (Exception ex) { Log.Warning(ex, "Source {Name} failed for {Id}", source.Name, trackId); }
        }

        return null;
    }

    private async Task<List<OnlineTrack>> SearchWithFallback(IMusicSearchService source, string keyword, int limit, TimeSpan timeout)
    {
        try
        {
            var list = await source.SearchAsync(keyword, limit).WaitAsync(timeout);
            LastSearchReport.Add(new SourceSearchStatus(source.Name, "ok", list.Count, null));
            return list;
        }
        catch (TimeoutException)
        {
            LastSearchReport.Add(new SourceSearchStatus(source.Name, "timeout", 0, $"超时({timeout.TotalSeconds}s)"));
            Log.Warning("Source {Name} search timed out after {Seconds}s", source.Name, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (Exception ex)
        {
            LastSearchReport.Add(new SourceSearchStatus(source.Name, "failed", 0, ex.Message));
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

/// <summary>单个音源搜索状态（成功/超时/失败 + 原因）。</summary>
public record SourceSearchStatus(string Name, string Status, int Count, string? Error);
