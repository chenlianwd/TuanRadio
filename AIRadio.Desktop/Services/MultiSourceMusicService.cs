using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 多平台聚合音乐搜索服务，集成酷我/酷狗/咪咕/网易云
/// </summary>
public class MultiSourceMusicService : IMusicSearchService
{
    private static readonly TimeSpan PrimarySourceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);
    // yt-dlp 子进程允许使用较长的单源上限，但仍必须被整个搜索操作的剩余预算截断
    private static readonly TimeSpan SlowSourceTimeout = TimeSpan.FromSeconds(30);
    // 0.4 统一 deadline：快速源与后续慢源共享 8s 整体预算（对应量化门禁"普通在线搜索硬截止 8s"），
    // 每个单源预算从剩余时间中扣减，调用下一源前先检查剩余预算
    private static readonly TimeSpan SearchOverallDeadline = TimeSpan.FromSeconds(8);
    // 播放地址解析/跨源回退的整体预算，与 AudioService.UrlRefreshTimeout 对齐：
    // 内层每源 5s 不再无限串行叠加，由本 deadline 收口
    private static readonly TimeSpan ResolveOverallDeadline = TimeSpan.FromSeconds(8);
    // 剩余预算低于该值时不再启动下一个源（搜索+解析至少要给这么多时间才有意义）
    private static readonly TimeSpan MinSourceBudget = TimeSpan.FromSeconds(1);
    private readonly HttpClient _httpClient;
    private readonly List<IMusicSearchService> _sources;
    private readonly object _reportGate = new();
    private readonly List<SourceSearchStatus> _lastSearchReport = new();

    public string Name => "多平台聚合";

    /// <summary>最近一次搜索的各源状态（供 UI 透传具体失败原因，子项目 5）。</summary>
    public IReadOnlyList<SourceSearchStatus> LastSearchReport
    {
        get
        {
            lock (_reportGate)
            {
                return _lastSearchReport.ToArray();
            }
        }
    }

    public MultiSourceMusicService(HttpClient httpClient, params IMusicSearchService[] extraSources)
        : this(httpClient, accounts: null, extraSources)
    {
    }

    public MultiSourceMusicService(HttpClient httpClient, MusicAccountStore? accounts, params IMusicSearchService[] extraSources)
    {
        _httpClient = httpClient;
        _sources = new List<IMusicSearchService>
        {
            new NeteaseMusicService(httpClient, accounts),
            new KuwoMusicService(httpClient),
            new KugouMusicService(httpClient, accounts),
            new MiguMusicService(httpClient)
        };
        _sources.AddRange(extraSources); // YouTube 等额外源作为最低优先级
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        lock (_reportGate)
            _lastSearchReport.Clear();

        cancellationToken.ThrowIfCancellationRequested();
        // 快速源优先使用 8s 整体 deadline；后续慢源只能使用其剩余部分
        var deadline = DateTimeOffset.UtcNow + SearchOverallDeadline;
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        searchCts.CancelAfter(SearchOverallDeadline);

        var merged = new List<OnlineTrack>();
        try
        {
            var primary = _sources.FirstOrDefault();
            if (primary != null)
            {
                var primaryResults = await SearchWithFallback(
                    primary,
                    keyword,
                    limit,
                    CapBudget(PrimarySourceTimeout, RemainingBudget(deadline)),
                    searchCts.Token);
                if (primaryResults.Count > 0)
                {
                    var probe = await ProbePrimaryPlayabilityAsync(
                        primary,
                        primaryResults,
                        CapBudget(SourceTimeout, RemainingBudget(deadline)),
                        searchCts.Token);
                    if (probe == PrimaryProbeResult.Playable)
                    {
                        Log.Information("Music search '{Keyword}' returned {Count} result(s) from primary source {Source}", keyword, primaryResults.Count, primary.Name);
                        return primaryResults.Take(limit * 2).ToList();
                    }

                    // 搜到结果但整组不可播（典型：版权受限只剩试听片段）时在报告注明，
                    // 否则 UI 只显示"成功N条"却没有任何结果，用户无法判断原因
                    AnnotateReport(primary.Name, probe == PrimaryProbeResult.ProbeTimeout
                        ? AppLanguage.T("可播性检查超时，已跳过", "playability check timed out, skipped")
                        : AppLanguage.T("试听或失效片段，已过滤", "preview or stale clips only, filtered"));
                    Log.Warning("Primary source {Source} returned no playable result for '{Keyword}'; trying fallback sources", primary.Name, keyword);
                }
            }

            var fallbackBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (fallbackBudget >= MinSourceBudget)
            {
                var tasks = _sources.Skip(1)
                    .Where(s => !s.IsSlowSource)
                    .Select(s => SearchWithFallback(
                        s,
                        keyword,
                        limit,
                        fallbackBudget,
                        searchCts.Token));
                var results = await Task.WhenAll(tasks);
                cancellationToken.ThrowIfCancellationRequested();

                // 跨源去重：同一首歌多源命中只保留首个（顺序即源优先级）
                foreach (var track in results.SelectMany(r => r))
                {
                    if (merged.Count >= limit * 2)
                        break;
                    if (merged.Any(m => MusicIdentity.IsSameSongLoose(m.Title, m.Artist, track.Title, track.Artist)))
                        continue;
                    merged.Add(track);
                }
            }
            else
            {
                // 主源已耗尽整体预算：明确记录跳过，而不是让内层源各自再拿满 5s
                Log.Debug("Fast fallback sources skipped for '{Keyword}': search deadline exhausted", keyword);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 整体 deadline 到点收口：按"无结果"处理并继续 YouTube 兜底判定，
            // 不得把 deadline 取消误当作用户取消抛给调用方
            Log.Debug("Fast search path cut off by overall deadline for '{Keyword}'", keyword);
        }

        // YouTube 等慢源是最低优先级兜底：只使用整个搜索操作的剩余预算，
        // 不得在 8s 总 deadline 之后再叠加自己的 30s 单源预算。
        if (merged.Count == 0)
        {
            foreach (var slowSource in _sources.Where(s => s.IsSlowSource))
            {
                var slowBudget = CapBudget(SlowSourceTimeout, RemainingBudget(deadline));
                if (slowBudget < MinSourceBudget || searchCts.IsCancellationRequested)
                {
                    Log.Debug("Slow source {Source} skipped for '{Keyword}': overall search deadline exhausted", slowSource.Name, keyword);
                    break;
                }

                List<OnlineTrack> slowResults;
                try
                {
                    slowResults = await SearchWithFallback(
                        slowSource,
                        keyword,
                        limit,
                        slowBudget,
                        searchCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.Debug("Slow source {Source} cut off by overall deadline for '{Keyword}'", slowSource.Name, keyword);
                    break;
                }
                if (slowResults.Count > 0)
                {
                    merged.AddRange(slowResults.Take(limit * 2));
                    break;
                }
            }
        }

        Log.Information("Music search '{Keyword}' returned {Count} fallback result(s)", keyword, merged.Count);
        return merged;
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        // trackId format: "source:id"
        var parts = trackId.Split(':', 2);
        if (parts.Length == 2)
        {
            var source = _sources.FirstOrDefault(s =>
                s.GetType().Name.Replace("MusicService", "").ToLower() == parts[0].ToLower());
            if (source != null)
                return await GetPlayUrlWithTimeout(
                    source, parts[1], CapBudget(SourceTimeout, RemainingBudget(deadline)), cancellationToken);
        }

        // Try all sources
        foreach (var source in _sources)
        {
            var budget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (budget < MinSourceBudget)
            {
                Log.Debug("Play URL attempts skipped for {Id}: overall deadline exhausted", trackId);
                break;
            }

            try
            {
                var url = await GetPlayUrlWithTimeout(source, trackId, budget, cancellationToken);
                if (url != null) return url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) { Log.Warning(ex, "Source {Name} failed for {Id}", source.Name, trackId); }
        }

        return null;
    }

    /// <summary>
    /// 当首选音源只返回了搜索结果但播放地址失效时，按歌曲元数据到其他音源重新搜索，
    /// 避免把一个源的 ID 错误地拿去请求另一个源。
    /// </summary>
    public async Task<string?> GetPlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        var preferred = FindSource(track.Id);
        if (preferred != null)
        {
            var preferredId = StripSourcePrefix(track.Id);
            var preferredUrl = await GetPlayUrlWithTimeout(
                preferred, preferredId, CapBudget(SourceTimeout, RemainingBudget(deadline)), cancellationToken);
            if (!string.IsNullOrWhiteSpace(preferredUrl))
                return preferredUrl;
        }

        return await GetFallbackPlayUrlAsync(track, preferred, deadline, cancellationToken);
    }

    /// <summary>
    /// 当前音源已经提前结束时，跳过该音源并按歌曲元数据寻找替代播放地址。
    /// 成功时同步更新 track.Id，确保后续刷新继续使用实际生效的音源。
    /// </summary>
    public Task<string?> GetAlternativePlayUrlAsync(
        OnlineTrack track,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        return GetFallbackPlayUrlAsync(track, FindSource(track.Id), deadline, cancellationToken);
    }

    private async Task<string?> GetFallbackPlayUrlAsync(
        OnlineTrack track,
        IMusicSearchService? excludedSource,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var query = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title
            : $"{track.Title} {track.Artist}";

        foreach (var source in _sources)
        {
            if (ReferenceEquals(source, excludedSource))
                continue;

            // 调用下一源前检查剩余预算：预算不足时明确记录并停止，
            // 不再依赖外层 WaitAsync 超时把整个恢复流程硬掰断
            var searchBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (searchBudget < MinSourceBudget)
            {
                Log.Debug("Playback fallback skipped {Source}: overall deadline exhausted", source.Name);
                break;
            }

            var candidates = await SearchForPlaybackFallbackAsync(source, query, searchBudget, cancellationToken);
            var candidate = candidates.FirstOrDefault(candidate => MusicIdentity.IsSameSongLoose(
                candidate.Title, candidate.Artist, track.Title, track.Artist));
            if (candidate == null)
                continue;

            var urlBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (urlBudget < MinSourceBudget)
            {
                Log.Debug("Playback fallback URL fetch skipped {Source}: overall deadline exhausted", source.Name);
                break;
            }

            var url = await GetPlayUrlWithTimeout(
                source,
                StripSourcePrefix(candidate.Id),
                urlBudget,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var previousSource = excludedSource?.Name ?? "unknown";
                track.Id = candidate.Id;
                track.Source = candidate.Source;
                if (candidate.DurationMs > 0)
                    track.DurationMs = candidate.DurationMs;

                Log.Information(
                    "Playback URL fallback switched {Track} from {Preferred} to {Source}",
                    track.Title,
                    previousSource,
                    source.Name);
                return url;
            }
        }

        return null;
    }

    private async Task<List<OnlineTrack>> SearchWithFallback(
        IMusicSearchService source,
        string keyword,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var list = await source.SearchAsync(keyword, limit, timeoutCts.Token)
                .WaitAsync(timeout, cancellationToken);
            AddSearchReport(new SourceSearchStatus(source.Name, "ok", list.Count, null));
            return list;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            AddSearchReport(new SourceSearchStatus(source.Name, "timeout", 0, AppLanguage.T($"超时({timeout.TotalSeconds}s)", $"timed out ({timeout.TotalSeconds}s)")));
            Log.Warning("Source {Name} search timed out after {Seconds}s", source.Name, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AddSearchReport(new SourceSearchStatus(source.Name, "timeout", 0, AppLanguage.T($"超时({timeout.TotalSeconds}s)", $"timed out ({timeout.TotalSeconds}s)")));
            Log.Warning("Source {Name} search timed out after {Seconds}s", source.Name, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddSearchReport(new SourceSearchStatus(source.Name, "failed", 0, ex.Message));
            Log.Warning(ex, "Source {Name} search failed", source.Name);
            return new List<OnlineTrack>();
        }
    }

    private async Task<PrimaryProbeResult> ProbePrimaryPlayabilityAsync(
        IMusicSearchService source,
        IReadOnlyList<OnlineTrack> tracks,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var playabilityCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        playabilityCts.CancelAfter(budget);
        try
        {
            foreach (var track in tracks.Take(3))
            {
                playabilityCts.Token.ThrowIfCancellationRequested();
                var parts = track.Id.Split(':', 2);
                var sourceId = parts.Length == 2 ? parts[1] : track.Id;
                var url = await GetPlayUrlWithTimeout(source, sourceId, budget, playabilityCts.Token);
                if (!string.IsNullOrWhiteSpace(url))
                    return PrimaryProbeResult.Playable;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Primary source {Name} playability check timed out", source.Name);
            return PrimaryProbeResult.ProbeTimeout;
        }

        return PrimaryProbeResult.NoPlayableTrack;
    }

    private enum PrimaryProbeResult
    {
        Playable,
        NoPlayableTrack,
        ProbeTimeout
    }

    private async Task<List<OnlineTrack>> SearchForPlaybackFallbackAsync(
        IMusicSearchService source,
        string query,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(budget);
        try
        {
            return await source.SearchAsync(query, 5, timeoutCts.Token)
                .WaitAsync(budget, cancellationToken);
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            Log.Debug("Playback fallback search timed out for source {Source}", source.Name);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Playback fallback search failed for source {Source}", source.Name);
            return new List<OnlineTrack>();
        }
    }

    private async Task<string?> GetPlayUrlWithTimeout(
        IMusicSearchService source,
        string trackId,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(budget);
        try
        {
            return await source.GetPlayUrlAsync(trackId, timeoutCts.Token)
                .WaitAsync(budget, cancellationToken);
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            Log.Warning("Source {Name} play URL timed out after {Seconds}s for {Id}", source.Name, budget.TotalSeconds, trackId);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Source {Name} play URL timed out after {Seconds}s for {Id}", source.Name, budget.TotalSeconds, trackId);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Source {Name} play URL failed for {Id}", source.Name, trackId);
            return null;
        }
    }

    private static TimeSpan RemainingBudget(DateTimeOffset deadline)
        => deadline - DateTimeOffset.UtcNow;

    private static TimeSpan CapBudget(TimeSpan budget, TimeSpan remaining)
        => remaining < budget ? remaining : budget;

    private IMusicSearchService? FindSource(string trackId)
    {
        var parts = trackId.Split(':', 2);
        if (parts.Length != 2)
            return null;

        return _sources.FirstOrDefault(s =>
            s.GetType().Name.Replace("MusicService", "", StringComparison.OrdinalIgnoreCase)
                .Equals(parts[0], StringComparison.OrdinalIgnoreCase));
    }

    private static string StripSourcePrefix(string trackId)
    {
        var parts = trackId.Split(':', 2);
        return parts.Length == 2 ? parts[1] : trackId;
    }

    private void AddSearchReport(SourceSearchStatus status)
    {
        // 逐源报告会透传到 UI 与日志：异常文本可能带上游 URL/凭据参数，入库前统一脱敏
        var sanitized = new SourceSearchStatus(
            status.Name,
            status.Status,
            status.Count,
            SensitiveDataSanitizer.Sanitize(status.Error),
            SensitiveDataSanitizer.Sanitize(status.Note));
        lock (_reportGate)
            _lastSearchReport.Add(sanitized);
    }

    private void AnnotateReport(string sourceName, string note)
    {
        lock (_reportGate)
        {
            var index = _lastSearchReport.FindIndex(s => s.Name == sourceName);
            if (index >= 0)
                _lastSearchReport[index] = _lastSearchReport[index] with { Note = note };
        }
    }
}

/// <summary>单个音源搜索状态（成功/超时/失败 + 原因；Note 附加说明如"已过滤"）。</summary>
public record SourceSearchStatus(string Name, string Status, int Count, string? Error, string? Note = null);
