using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 音源聚合 Broker（docs/plans/2026-09-20-music-source-broker-phase1-design.md §3.3）：
/// 聚合/逐源报告/跨源回退/诊断逻辑自 MultiSourceMusicService 代码平移而来（只平移不重构），
/// 源列表从 IMusicSearchService 换为 IMusicProvider（经 MusicSearchServiceAdapter 包装）。
/// 源路由按 Descriptor.Id 匹配（原 FindSource 类型名反射的等价改写，A3 修复）；
/// 熔断键与逐源报告沿用 Descriptor.DisplayName（= 内层服务 Name，与旧实现完全一致）。
/// </summary>
public class MusicSourceBroker : IMusicSourceBroker
{
    private static readonly TimeSpan PrimarySourceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(5);
    // yt-dlp 子进程只用于显式搜索/播放，使用独立的有界预算。
    private static readonly TimeSpan SlowSourceTimeout = TimeSpan.FromSeconds(30);
    // 快速源共享 8s 前台预算；慢源在显式搜索阶段使用自己的预算。
    private static readonly TimeSpan SearchOverallDeadline = TimeSpan.FromSeconds(8);
    // 播放地址解析/跨源回退的整体预算，与 AudioService.UrlRefreshTimeout 对齐：
    // 内层每源 5s 不再无限串行叠加，由本 deadline 收口
    private static readonly TimeSpan ResolveOverallDeadline = TimeSpan.FromSeconds(8);
    // 剩余预算低于该值时不再启动下一个源（搜索+解析至少要给这么多时间才有意义）
    private static readonly TimeSpan MinSourceBudget = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<IMusicProvider> _providers;
    private readonly SourceHealthRegistry _healthRegistry = new();
    private readonly ResolvedMediaCache _mediaCache = new();
    private readonly object _reportGate = new();
    // 跨源回退对共享 track 实例的身份变更互斥（见 TryResolveFallbackCandidateAsync 内注释）
    private readonly object _fallbackMutationGate = new();
    private readonly List<SourceSearchStatus> _lastSearchReport = new();

    // 本服务是 DI 单例，用户搜索/电台推荐/DJ 点歌可能并发进入：
    // 逐源报告绑定到“发起搜索的异步上下文”，避免并发搜索互相覆盖状态。
    // 未设置时（直接调 SearchAsync 的旧路径）回落到共享的 _lastSearchReport。
    private static readonly AsyncLocal<List<SourceSearchStatus>?> CurrentSearchReport = new();

    public string Name => "多平台聚合";

    /// <summary>最近一次搜索的各源状态（供 UI 透传具体失败原因）。
    /// 注意并发搜索下这是“最后一次旧式调用”的快照；需要精确归属请用 <see cref="SearchWithReportAsync"/>。</summary>
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

    /// <summary>
    /// 在调用方上下文绑定独立报告作用域后执行搜索：报告对象随 ExecutionContext
    /// 流入 SearchAsync 的全部子调用（含 Task.WhenAll 的并发分身），读取时零竞态。
    /// </summary>
    public async Task<SearchOutcome> SearchWithReportAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        var report = new List<SourceSearchStatus>();
        CurrentSearchReport.Value = report;
        try
        {
            var tracks = await SearchAsync(
                keyword,
                limit,
                MusicSearchIntent.Explicit,
                cancellationToken);
            lock (_reportGate)
                return new SearchOutcome(tracks, report.ToArray());
        }
        finally
        {
            CurrentSearchReport.Value = null;
        }
    }

    /// <summary>
    /// 设置页逐源连接诊断：按源序独立探测（limit 1 的轻量搜索），复用逐源状态与
    /// 结构化失败分类（含熔断/超时/业务失败 Kind）。报告走独立作用域，
    /// 不污染用户上一次搜索的 LastSearchReport。
    /// </summary>
    public async Task<IReadOnlyList<SourceSearchStatus>> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var report = new List<SourceSearchStatus>();
        CurrentSearchReport.Value = report;
        try
        {
            foreach (var provider in _providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 慢源（yt-dlp 子进程）给独立短预算：诊断是手动触发，总时长需有界
                var budget = provider.Descriptor.IsSlowSource ? TimeSpan.FromSeconds(10) : SourceTimeout;
                await SearchWithFallback(provider, "周杰伦", 1, budget, cancellationToken);
            }
            lock (_reportGate)
                return report.ToArray();
        }
        finally
        {
            CurrentSearchReport.Value = null;
        }
    }

    /// <summary>按 Provider 列表组装（数组顺序即源优先级）。</summary>
    public MusicSourceBroker(params IMusicProvider[] providers)
        => _providers = providers.ToList();

    public MusicSourceBroker(HttpClient httpClient, params IMusicSearchService[] extraSources)
        : this(httpClient, accounts: null, extraSources)
    {
    }

    public MusicSourceBroker(HttpClient httpClient, MusicAccountStore? accounts, params IMusicSearchService[] extraSources)
        : this(httpClient, accounts, kugouVerification: null, extraSources)
    {
    }

    /// <summary>与原 MultiSourceMusicService 兼容的默认五源组装（自壳构造函数平移）。</summary>
    public MusicSourceBroker(HttpClient httpClient, MusicAccountStore? accounts,
        KugouVerificationService? kugouVerification, params IMusicSearchService[] extraSources)
    {
        var kugouSource = new KugouMusicService(httpClient, accounts, kugouVerification);
        var neteaseProvider = new MusicSearchServiceAdapter(new NeteaseMusicService(httpClient, accounts));
        var kugouProvider = new MusicSearchServiceAdapter(kugouSource);
        var providers = new List<IMusicProvider>
        {
            neteaseProvider,
            kugouProvider
        };
        if (accounts != null)
        {
            // 凭据变化：重置酷狗熔断（订阅归属随聚合体落在 Broker，docs/plans §3.5）+
            // 清空对应 Provider 的解析缓存（防旧凭据解析出的签名直链残留）
            accounts.KugouCredentialChanged += (_, _) =>
            {
                _healthRegistry.Reset(kugouSource.Name);
                _mediaCache.ClearProvider(kugouProvider.Descriptor.Id);
            };
            accounts.NeteaseCookieChanged += (_, _) =>
                _mediaCache.ClearProvider(neteaseProvider.Descriptor.Id);
        }
        // 酷我/咪咕当前依赖易失网页接口，默认不进入用户请求；仅供诊断和适配器开发显式开启。
        if (string.Equals(
                Environment.GetEnvironmentVariable("AIRADIO_ENABLE_LEGACY_WEB_SOURCES"),
                "1",
                StringComparison.Ordinal))
        {
            providers.Add(new MusicSearchServiceAdapter(new KuwoMusicService(httpClient), isExperimental: true));
            providers.Add(new MusicSearchServiceAdapter(new MiguMusicService(httpClient), isExperimental: true));
        }
        providers.AddRange(extraSources.Select(source => new MusicSearchServiceAdapter(source))); // YouTube 等额外源作为最低优先级
        _providers = providers;
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
        => SearchAsync(keyword, limit, MusicSearchIntent.Explicit, cancellationToken);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        MusicSearchIntent intent,
        CancellationToken cancellationToken)
    {
        lock (_reportGate)
        {
            if (CurrentSearchReport.Value is { } scoped)
                scoped.Clear();
            else
                _lastSearchReport.Clear();
        }

        cancellationToken.ThrowIfCancellationRequested();
        // 快速源优先使用 8s 整体 deadline；后续慢源只能使用其剩余部分
        var deadline = DateTimeOffset.UtcNow + SearchOverallDeadline;
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        searchCts.CancelAfter(SearchOverallDeadline);

        var merged = new List<OnlineTrack>();
        try
        {
            var primary = _providers.FirstOrDefault();
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
                        Log.Information("Music search '{Keyword}' returned {Count} result(s) from primary source {Source}", keyword, primaryResults.Count, primary.Descriptor.DisplayName);
                        return primaryResults.Take(limit * 2).ToList();
                    }

                    // 搜到结果但整组不可播（典型：版权受限只剩试听片段）时在报告注明，
                    // 否则 UI 只显示"成功N条"却没有任何结果，用户无法判断原因
                    AnnotateReport(primary.Descriptor.DisplayName, probe == PrimaryProbeResult.ProbeTimeout
                        ? AppLanguage.T("可播性检查超时，已跳过", "playability check timed out, skipped")
                        : AppLanguage.T("试听或失效片段，已过滤", "preview or stale clips only, filtered"));
                    Log.Warning("Primary source {Source} returned no playable result for '{Keyword}'; trying fallback sources", primary.Descriptor.DisplayName, keyword);
                }
            }

            var fallbackBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (fallbackBudget >= MinSourceBudget)
            {
                var tasks = _providers.Skip(1)
                    .Where(p => !p.Descriptor.IsSlowSource)
                    .Select(p => SearchWithFallback(
                        p,
                        keyword,
                        limit,
                        fallbackBudget,
                        searchCts.Token));
                var results = await Task.WhenAll(tasks);
                cancellationToken.ThrowIfCancellationRequested();

                // 跨源合并不在此去重：同名不同版本的优劣要等 CandidateRanker 评分排序后才可知，
                // 先按源优先级去重会把评分更高的副本提前丢掉；工作容量放宽到 3 倍，
                // 排序去重后再截回 limit*2，保持原有"至多 limit*2 条"的对外语义
                foreach (var track in results.SelectMany(r => r))
                {
                    if (merged.Count >= limit * 3)
                        break;
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

        // 慢源只允许显式用户操作进入。自动电台/DJ 推荐即使快速源为空也必须立即返回，
        // 否则 30s 搜索与 30s URL 解析会顶满下一首回调的 60s 上限。
        if (merged.Count == 0 && intent == MusicSearchIntent.Explicit)
        {
            var slowDeadline = DateTimeOffset.UtcNow + SlowSourceTimeout;
            foreach (var slowProvider in _providers.Where(p => p.Descriptor.IsSlowSource))
            {
                var slowBudget = CapBudget(SlowSourceTimeout, RemainingBudget(slowDeadline));
                if (slowBudget < MinSourceBudget)
                {
                    Log.Debug("Slow source budget exhausted for '{Keyword}'", keyword);
                    break;
                }

                var slowResults = await SearchWithFallback(
                    slowProvider,
                    keyword,
                    limit,
                    slowBudget,
                    cancellationToken);
                if (slowResults.Count > 0)
                {
                    merged.AddRange(slowResults.Take(limit * 2));
                    break;
                }
            }
        }

        // 基于 CandidateRanker 智能重排聚合搜索结果（低分与非预期版本后置），
        // 再做智能跨源去重（每组宽松同曲身份保留评分最高的副本）并截回上限
        merged = CandidateRanker.DeduplicateTracks(
                CandidateRanker.RankSearchResults(merged, keyword))
            .Take(limit * 2)
            .ToList();
        Log.Information("Music search '{Keyword}' returned {Count} fallback result(s)", keyword, merged.Count);
        return merged;
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        // trackId format: "source:id"：Descriptor.Id OrdinalIgnoreCase 匹配（自 FindSource 平移改写）
        var prefixedProvider = FindProvider(trackId);
        if (prefixedProvider != null)
        {
            var budget = prefixedProvider.Descriptor.IsSlowSource
                ? SlowSourceTimeout
                : CapBudget(SourceTimeout, RemainingBudget(deadline));
            return await ResolveWithTimeout(
                prefixedProvider, StripSourcePrefix(trackId), metadata: null, budget, cancellationToken);
        }

        // Try all sources：无前缀时才逐源尝试，且必须剥离可能存在的前缀，
        // 否则 "kugou:abc" 整串被喂给网易等源拼出无意义请求
        foreach (var provider in _providers)
        {
            var budget = CapBudget(SourceTimeout, RemainingBudget(deadline));
            if (budget < MinSourceBudget)
            {
                Log.Debug("Play URL attempts skipped for {Id}: overall deadline exhausted", trackId);
                break;
            }

            try
            {
                var url = await ResolveWithTimeout(provider, StripSourcePrefix(trackId), metadata: null, budget, cancellationToken);
                if (url != null) return url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) { Log.Warning(ex, "Source {Name} failed for {Id}", provider.Descriptor.DisplayName, trackId); }
        }

        return null;
    }

    /// <summary>
    /// 曲目级解析（缓存读路径）：点歌/推荐/歌单等对同一曲目的重复解析命中缓存；
    /// 播放恢复/重试链路必须走 <see cref="ResolveTrackAsync"/> 并传 forceRefresh=true
    /// （AudioService 的播放刷新委托只在刷新/恢复语境调用，拿旧缓存会重试刚失败的 URL）。
    /// </summary>
    public Task<string?> GetPlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken)
        => GetPlayUrlCoreAsync(track, forceRefresh: false, cancellationToken);

    /// <summary>
    /// Broker 级曲目解析入口（docs/plans §3.1/§3.5）：结果显式携带（可能已回退的）实际
    /// 生效身份与元数据；forceRefresh 绕过缓存并先逐出旧值（失败不回写），刷新结果写回缓存。
    /// </summary>
    public async Task<ResolveTrackResult?> ResolveTrackAsync(
        OnlineTrack track,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var beforeId = track.Id;
        var url = await GetPlayUrlCoreAsync(track, forceRefresh, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(url)
            ? null
            : BuildResolveResult(url, track, beforeId);
    }

    /// <summary>曲目解析缓存漏斗：命中直接返回；forceRefresh 读跳过且解析前逐出；
    /// 回退结果不入缓存（缓存命中不重放身份回写，防"Id 指向 A 源/URL 是 B 源直链"错位）。</summary>
    private async Task<string?> GetPlayUrlCoreAsync(
        OnlineTrack track,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var inputKey = ProviderTrackRef.FromSourceId(track.Id);
        if (inputKey != null)
        {
            if (!forceRefresh && _mediaCache.TryGet(inputKey, out var cached) && cached != null)
                return cached.Url;
            if (forceRefresh)
                _mediaCache.Evict(inputKey);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        var preferred = FindProvider(track.Id);
        string? url = null;
        if (preferred != null)
        {
            var preferredId = StripSourcePrefix(track.Id);
            var preferredBudget = preferred.Descriptor.IsSlowSource
                ? SlowSourceTimeout
                : CapBudget(SourceTimeout, RemainingBudget(deadline));
            url = await ResolveWithTimeout(
                preferred,
                preferredId,
                track.ProviderMetadata,
                preferredBudget,
                cancellationToken);
        }

        var beforeId = track.Id;
        if (string.IsNullOrWhiteSpace(url))
            url = await GetFallbackPlayUrlAsync(track, preferred, deadline, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // 回退（身份被改写）的结果不入缓存；首选源命中（身份未变）的结果入缓存
        if (inputKey != null && string.Equals(beforeId, track.Id, StringComparison.Ordinal))
            _mediaCache.Set(inputKey, BuildResolveResult(url, track, beforeId), expiresAt: null);

        return url;
    }

    private static ResolveTrackResult BuildResolveResult(string url, OnlineTrack track, string beforeId)
        => new(
            url,
            ProviderTrackRef.FromSourceId(track.Id) ?? new ProviderTrackRef(string.Empty, track.Id),
            track.Source,
            new Dictionary<string, string>(track.ProviderMetadata, StringComparer.OrdinalIgnoreCase),
            FellBack: !string.Equals(beforeId, track.Id, StringComparison.Ordinal));

    /// <summary>
    /// 当前音源已经提前结束时，跳过该音源并按歌曲元数据寻找替代播放地址。
    /// 成功时同步更新 track.Id，确保后续刷新继续使用实际生效的音源。
    /// </summary>
    public Task<string?> GetAlternativePlayUrlAsync(
        OnlineTrack track,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ResolveOverallDeadline;
        return GetFallbackPlayUrlAsync(track, FindProvider(track.Id), deadline, cancellationToken);
    }

    private async Task<string?> GetFallbackPlayUrlAsync(
        OnlineTrack track,
        IMusicProvider? excludedProvider,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var query = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title
            : $"{track.Title} {track.Artist}";

        // 快速源共享同一段搜索预算并发查询；仍按 _providers 的既定优先级选择候选。
        // 这样单个坏源不会在串行链路里吃光总预算，同时不改变正常情况下的选源顺序。
        var fastSources = _providers
            .Where(provider => !ReferenceEquals(provider, excludedProvider) && !provider.Descriptor.IsSlowSource)
            .ToArray();
        var fastSearchBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
        if (fastSources.Length > 0 && fastSearchBudget >= MinSourceBudget)
        {
            using var fastSearchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var searches = fastSources
                .Select(async provider => (
                    Provider: provider,
                    Candidates: await SearchForPlaybackFallbackAsync(
                        provider,
                        query,
                        fastSearchBudget,
                        fastSearchCts.Token)))
                .ToArray();

            // 任务已全部启动，但按优先级逐个等待：高优先级源一旦命中即可立即解析 URL，
            // 不必等一个低优先级挂起源跑满超时。
            string? resolvedUrl = null;
            try
            {
                foreach (var search in searches)
                {
                    var (provider, candidates) = await search;
                    resolvedUrl = await TryResolveFallbackCandidateAsync(
                        track,
                        provider,
                        candidates,
                        excludedProvider,
                        deadline,
                        cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resolvedUrl))
                        break;
                }
            }
            finally
            {
                // 命中、异常或调用方取消都要终止并观察其余包装任务，避免旧播放请求继续占用资源。
                fastSearchCts.Cancel();
                try
                {
                    await Task.WhenAll(searches);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 本批次因已命中而主动取消。
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedUrl))
                return resolvedUrl;
        }
        else if (fastSources.Length > 0)
        {
            Log.Debug("Fast playback fallback skipped: overall deadline exhausted");
        }

        // YouTube/yt-dlp 不参与自动回退：30s 子进程不能安全塞进 8s 播放恢复预算。
        return null;
    }

    private async Task<string?> TryResolveFallbackCandidateAsync(
        OnlineTrack track,
        IMusicProvider provider,
        IReadOnlyList<OnlineTrack> candidates,
        IMusicProvider? excludedProvider,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        // 两遍按原宽松口径：先精确匹配，落空再做”剥离标题修饰”的第二遍（YouTube 等源
        // 的结果标题几乎必带 “(Live)”/”(Official Music Video)” 等修饰）。每遍内按时长
        // 接近度择优（docs/plans/2026-09-15 §2），源间优先级仍由外层”命中即停”控制。
        var candidate = FindBestFallbackCandidate(candidates, track, stripDecorations: false)
            ?? FindBestFallbackCandidate(candidates, track, stripDecorations: true);
        if (candidate == null)
            return null;

        var urlBudget = CapBudget(SourceTimeout, RemainingBudget(deadline));
        if (urlBudget < MinSourceBudget)
        {
            Log.Debug("Playback fallback URL fetch skipped {Source}: overall deadline exhausted", provider.Descriptor.DisplayName);
            return null;
        }

        var url = await ResolveWithTimeout(
            provider,
            StripSourcePrefix(candidate.Id),
            candidate.ProviderMetadata,
            urlBudget,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var previousSource = excludedProvider?.Descriptor.DisplayName ?? "unknown";
        // 共享 track 实例可能同时经历 URL 刷新与本回退：变更串行化，
        // 避免后写者覆盖前写者（Id 指向 A 源而实际播放 B 源直链）
        lock (_fallbackMutationGate)
        {
            track.Id = candidate.Id;
            track.Source = candidate.Source;
            track.ProviderMetadata = new Dictionary<string, string>(
                candidate.ProviderMetadata,
                StringComparer.OrdinalIgnoreCase);
            if (candidate.DurationMs > 0)
                track.DurationMs = candidate.DurationMs;
        }

        Log.Information(
            "Playback URL fallback switched {Track} from {Preferred} to {Source}",
            track.Title,
            previousSource,
            provider.Descriptor.DisplayName);
        return url;
    }

    /// <summary>
    /// 单遍回退候选择优：通过该遍身份匹配的候选中按时长接近度取最高分；
    /// 严格大于才替换（并列取源内首个，保留各源自身相关性排序为 tie-breaker）。
    /// </summary>
    private static OnlineTrack? FindBestFallbackCandidate(
        IReadOnlyList<OnlineTrack> candidates,
        OnlineTrack target,
        bool stripDecorations)
    {
        var targetTitle = stripDecorations
            ? MusicIdentity.StripTitleDecorations(target.Title)
            : target.Title;

        OnlineTrack? best = null;
        var bestScore = double.MinValue;
        foreach (var candidate in candidates)
        {
            var candidateTitle = stripDecorations
                ? MusicIdentity.StripTitleDecorations(candidate.Title)
                : candidate.Title;
            if (!MusicIdentity.IsSameSongLoose(candidateTitle, candidate.Artist, targetTitle, target.Artist))
                continue;

            var score = CandidateRanker.ScoreFallbackCandidate(candidate, target);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private async Task<List<OnlineTrack>> SearchWithFallback(
        IMusicProvider provider,
        string keyword,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_healthRegistry.CanRequest(provider.Descriptor.DisplayName, out var remaining))
        {
            var note = AppLanguage.T(
                $"连续接口故障，暂停请求 {Math.Ceiling(remaining.TotalSeconds):0} 秒",
                $"circuit open for {Math.Ceiling(remaining.TotalSeconds):0}s after repeated transport failures");
            AddSearchReport(new SourceSearchStatus(provider.Descriptor.DisplayName, "disabled", 0, note));
            Log.Debug("Source {Name} skipped while circuit is open for {Seconds}s", provider.Descriptor.DisplayName, remaining.TotalSeconds);
            return new List<OnlineTrack>();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var list = await provider.SearchAsync(keyword, limit, timeoutCts.Token)
                .WaitAsync(timeout, cancellationToken);
            _healthRegistry.RecordSuccess(provider.Descriptor.DisplayName);
            AddSearchReport(new SourceSearchStatus(provider.Descriptor.DisplayName, "ok", list.Count, null));
            return list;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            AddSearchReport(new SourceSearchStatus(provider.Descriptor.DisplayName, "timeout", 0, AppLanguage.T($"超时({timeout.TotalSeconds}s)", $"timed out ({timeout.TotalSeconds}s)")));
            Log.Warning("Source {Name} search timed out after {Seconds}s", provider.Descriptor.DisplayName, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            AddSearchReport(new SourceSearchStatus(provider.Descriptor.DisplayName, "timeout", 0, AppLanguage.T($"超时({timeout.TotalSeconds}s)", $"timed out ({timeout.TotalSeconds}s)")));
            Log.Warning("Source {Name} search timed out after {Seconds}s", provider.Descriptor.DisplayName, timeout.TotalSeconds);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordSourceOutcome(provider.Descriptor.DisplayName, ex);
            AddSearchReport(new SourceSearchStatus(
                provider.Descriptor.DisplayName, "failed", 0, ex.Message, FailureKind: ClassifyFailure(ex)));
            Log.Warning(ex, "Source {Name} search failed", provider.Descriptor.DisplayName);
            return new List<OnlineTrack>();
        }
    }

    private async Task<PrimaryProbeResult> ProbePrimaryPlayabilityAsync(
        IMusicProvider provider,
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
                var sourceId = StripSourcePrefix(track.Id);
                var url = await ResolveWithTimeout(
                    provider,
                    sourceId,
                    track.ProviderMetadata,
                    budget,
                    playabilityCts.Token);
                if (!string.IsNullOrWhiteSpace(url))
                    return PrimaryProbeResult.Playable;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("Primary source {Name} playability check timed out", provider.Descriptor.DisplayName);
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
        IMusicProvider provider,
        string query,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (!_healthRegistry.CanRequest(provider.Descriptor.DisplayName, out var remaining))
        {
            Log.Debug(
                "Playback fallback search skipped {Source}: circuit open for {Seconds}s",
                provider.Descriptor.DisplayName,
                remaining.TotalSeconds);
            return new List<OnlineTrack>();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(budget);
        try
        {
            var results = await provider.SearchAsync(query, 5, timeoutCts.Token)
                .WaitAsync(budget, cancellationToken);
            _healthRegistry.RecordSuccess(provider.Descriptor.DisplayName);
            return results;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            Log.Debug("Playback fallback search timed out for source {Source}", provider.Descriptor.DisplayName);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            Log.Debug("Playback fallback search timed out for source {Source}", provider.Descriptor.DisplayName);
            return new List<OnlineTrack>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordSourceOutcome(provider.Descriptor.DisplayName, ex);
            Log.Debug(ex, "Playback fallback search failed for source {Source}", provider.Descriptor.DisplayName);
            return new List<OnlineTrack>();
        }
        finally
        {
            // WaitAsync 先观察到批次取消时，显式取消内层令牌，确保取消继续传到底层音源实现。
            timeoutCts.Cancel();
        }
    }

    /// <summary>单源解析带硬超时与熔断（自 GetPlayUrlWithTimeout 两个重载合并平移：元数据可空即覆盖原字符串路径）。</summary>
    private async Task<string?> ResolveWithTimeout(
        IMusicProvider provider,
        string bareTrackId,
        IReadOnlyDictionary<string, string>? metadata,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (!_healthRegistry.CanRequest(provider.Descriptor.DisplayName, out var remaining))
        {
            Log.Debug("Source {Name} play URL skipped while circuit is open for {Seconds}s", provider.Descriptor.DisplayName, remaining.TotalSeconds);
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(budget);
        try
        {
            var media = await provider.ResolveAsync(
                    new ProviderTrackRef(provider.Descriptor.Id, bareTrackId),
                    metadata,
                    timeoutCts.Token)
                .WaitAsync(budget, cancellationToken);
            _healthRegistry.RecordSuccess(provider.Descriptor.DisplayName);
            if (media == null)
                return null;

            // 播放 URL 进 LibVLC 前统一过 MediaUriPolicy（docs/plans §3.4）：
            // 本处是全部解析路径的收口点（id 级/曲目级/跨源回退/可播性探针都经此）。
            // 拒绝不记熔断（策略事件不是音源健康事件），按"无可播地址"处理让回退链继续。
            // 策略内的 DNS 复查同样受预算约束：解析器无内在超时，病态域名不能击穿整体 deadline。
            bool allowed;
            try
            {
                allowed = await MediaUriPolicy.ValidateAsync(
                        provider.Descriptor.NetworkScope,
                        media.Uri,
                        timeoutCts.Token)
                    .WaitAsync(budget, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeoutCts.Cancel();
                Log.Warning(
                    "Source {Name} play URL policy check timed out after {Seconds}s for {Id}",
                    provider.Descriptor.DisplayName, budget.TotalSeconds, bareTrackId);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 常态路径：timeoutCts（方法入口武装）先于 WaitAsync 自己的计时器触发，
                // DNS 尊重 token 时抛出的是 OCE 而非 TimeoutException。
                // 策略校验超时不是音源健康事件，同样不记熔断。
                Log.Warning(
                    "Source {Name} play URL policy check timed out after {Seconds}s for {Id}",
                    provider.Descriptor.DisplayName, budget.TotalSeconds, bareTrackId);
                return null;
            }

            if (!allowed)
            {
                Log.Warning(
                    "Provider {Name} play URL rejected by MediaUriPolicy for {Id}: {Uri}",
                    provider.Descriptor.DisplayName, bareTrackId, media.Uri);
                return null;
            }

            return media.RawUrl;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            Log.Warning("Source {Name} play URL timed out after {Seconds}s for {Id}", provider.Descriptor.DisplayName, budget.TotalSeconds, bareTrackId);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _healthRegistry.RecordTransportFailure(provider.Descriptor.DisplayName);
            Log.Warning("Source {Name} play URL timed out after {Seconds}s for {Id}", provider.Descriptor.DisplayName, budget.TotalSeconds, bareTrackId);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordSourceOutcome(provider.Descriptor.DisplayName, ex);
            Log.Debug(ex, "Source {Name} play URL failed for {Id}", provider.Descriptor.DisplayName, bareTrackId);
            return null;
        }
    }

    private void RecordSourceOutcome(string sourceName, Exception exception)
    {
        // 业务拒绝仍证明请求/响应链路健康，应打断此前的连续传输失败计数。
        if (exception is MusicSourceBusinessException)
            _healthRegistry.RecordSuccess(sourceName);
        else
            _healthRegistry.RecordTransportFailure(sourceName);
    }

    private static MusicSourceFailureKind ClassifyFailure(Exception exception)
        => exception is MusicSourceBusinessException business ? business.Kind : MusicSourceFailureKind.None;

    private static TimeSpan RemainingBudget(DateTimeOffset deadline)
        => deadline - DateTimeOffset.UtcNow;

    private static TimeSpan CapBudget(TimeSpan budget, TimeSpan remaining)
        => remaining < budget ? remaining : budget;

    /// <summary>按持久化 SourceId 的 "source:" 前缀路由到对应 Provider（Descriptor.Id OrdinalIgnoreCase）。</summary>
    private IMusicProvider? FindProvider(string trackId)
    {
        var parts = trackId.Split(':', 2);
        if (parts.Length != 2)
            return null;

        return _providers.FirstOrDefault(p =>
            p.Descriptor.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
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
            SensitiveDataSanitizer.Sanitize(status.Note),
            status.FailureKind);
        lock (_reportGate)
            (CurrentSearchReport.Value ?? _lastSearchReport).Add(sanitized);
    }

    private void AnnotateReport(string sourceName, string note)
    {
        lock (_reportGate)
        {
            var target = CurrentSearchReport.Value ?? _lastSearchReport;
            var index = target.FindIndex(s => s.Name == sourceName);
            if (index >= 0)
                target[index] = target[index] with { Note = note };
        }
    }
}
