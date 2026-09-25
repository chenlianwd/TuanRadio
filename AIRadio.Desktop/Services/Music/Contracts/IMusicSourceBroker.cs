using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// Broker 对业务层暴露的聚合能力（docs/plans 2026-09-20 §3.3）。成员清单是显式的、
/// 必须逐项实现：GetPlayUrlAsync(OnlineTrack, ct) 与意图重载若漏实现会静默回落
/// IMusicSearchService 默认接口方法的弱路径（无跨源回退/无 ProviderMetadata/慢源误入
/// 自动链路），编译期不可见（评审第 2 轮 A2）。
/// </summary>
public interface IMusicSourceBroker : IMusicSearchService
{
    event EventHandler? ProviderConfigurationChanged;
    /// <summary>带逐源报告作用域的搜索：报告与结果同源，并发搜索互不串扰。</summary>
    Task<SearchOutcome> SearchWithReportAsync(string keyword, int limit, CancellationToken cancellationToken);

    /// <summary>仅搜索快速音源，供界面先返回结果；不会启动 yt-dlp 等慢源。</summary>
    Task<SearchOutcome> SearchFastWithReportAsync(string keyword, int limit, CancellationToken cancellationToken);

    /// <summary>仅搜索慢源，供界面在快速源无结果后按请求代次异步补充。</summary>
    Task<SearchOutcome> SearchSlowWithReportAsync(string keyword, int limit, CancellationToken cancellationToken);

    /// <summary>设置页逐源连接诊断（独立报告作用域，不污染搜索状态）。</summary>
    Task<IReadOnlyList<SourceSearchStatus>> DiagnoseAsync(CancellationToken cancellationToken);

    /// <summary>每个已注册音源的最近请求健康度快照；只包含本地分类与时间，不包含凭据或播放地址。</summary>
    IReadOnlyList<SourceHealthSnapshot> GetHealthSnapshots();

    /// <summary>注册音源和用户可调顺序；禁用的源仍列在注册列表中供设置页恢复。</summary>
    IReadOnlyList<MusicProviderDescriptor> GetProviderDescriptors();
    void ConfigureProviders(IReadOnlyList<string> orderedIds, IReadOnlyCollection<string> disabledIds);

    /// <summary>跨源回退解析：成功时回写 track 身份（Id/Source/ProviderMetadata）。</summary>
    Task<string?> GetAlternativePlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken);

    /// <summary>
    /// Broker 级曲目解析入口：结果显式携带（可能已回退的）实际生效身份与元数据；
    /// forceRefresh=true 绕过解析缓存并先逐出旧值——播放恢复/重试链路必须传 true
    /// （AudioService 的播放刷新委托只在刷新语境调用，读缓存会重试刚失败的 URL）。
    /// </summary>
    Task<ResolveTrackResult?> ResolveTrackAsync(OnlineTrack track, bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>供队列预检和界面使用的结构化解析状态。</summary>
    Task<PlaybackResolutionOutcome> ResolveTrackDetailedAsync(
        OnlineTrack track, bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>意图化搜索：Automatic 链路挡 YouTube 等慢源，防止顶穿续播预算。</summary>
    Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, MusicSearchIntent intent, CancellationToken cancellationToken);

    /// <summary>曲目级解析（显式覆写默认接口方法：带 ProviderMetadata 与跨源回退；
    /// new 隐藏是有意的——强制实现者显式实现，防静默回落 IMusicSearchService 的弱默认路径）。</summary>
    new Task<string?> GetPlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken);
}
