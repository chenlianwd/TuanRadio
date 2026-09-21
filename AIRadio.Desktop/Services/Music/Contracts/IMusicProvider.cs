using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>Provider 网络作用域：MediaUriPolicy 按此决定地址类校验口径。</summary>
public enum ProviderNetworkScope
{
    /// <summary>仅本地文件（阶段 2 本地曲库 Provider）。</summary>
    LocalFileOnly,
    /// <summary>公网音源：播放地址禁止 loopback/私网/链路本地/组播/云元数据地址。</summary>
    PublicInternet,
    /// <summary>用户显式配置的私有服务器（阶段 2 OpenSubsonic）。</summary>
    UserConfiguredPrivateNetwork
}

/// <summary>
/// Provider 描述符。由宿主注册时给定，不从 Provider 自身响应提权：
/// Id 即持久化 SourceId 的 "source:" 前缀（路由键）；DisplayName 沿用
/// IMusicSearchService.Name——逐源报告、熔断键与既有测试断言依赖该显示名。
/// </summary>
public sealed record MusicProviderDescriptor(
    string Id,
    string DisplayName,
    ProviderNetworkScope NetworkScope = ProviderNetworkScope.PublicInternet,
    bool IsSlowSource = false,
    bool IsExperimental = false,
    TimeSpan? SearchBudget = null,
    TimeSpan? ResolveBudget = null);

/// <summary>
/// 音源 Provider 契约（docs/plans/2026-09-20-music-source-broker-phase1-design.md §3.1）：
/// 只负责本源搜索与解析；跨源回退、逐源报告、熔断与预算编排都在 Broker 聚合层。
/// ResolveAsync 返回 null = 无可播地址（与既有 GetPlayUrlAsync 返回 null 语义一致）；
/// 业务失败抛 MusicSourceBusinessException，传输异常照抛。
/// </summary>
public interface IMusicProvider
{
    MusicProviderDescriptor Descriptor { get; }

    Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken);

    Task<ResolvedMedia?> ResolveAsync(
        ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata,
        CancellationToken cancellationToken);
}
