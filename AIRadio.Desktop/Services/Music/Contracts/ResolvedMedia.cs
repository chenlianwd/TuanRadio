using System;
using System.Collections.Generic;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>有限传输头白名单：只存在于内存，不参与歌单序列化、普通日志或诊断报告。</summary>
public sealed record PlaybackHeaders(
    string? UserAgent = null,
    Uri? Referer = null,
    string? Cookie = null);

/// <summary>
/// 解析出的可播放媒体（docs/plans 2026-09-20 §3.1）：Uri 交给播放器前必须经 MediaUriPolicy 校验。
/// 交给播放器时用 <see cref="RawUrl"/>（OriginalString 逐字保留解析所得字符串），
/// 不得用 Uri.ToString()——后者会改变转义形态。
/// </summary>
public sealed record ResolvedMedia(
    ProviderTrackRef Track,
    Uri Uri,
    DateTimeOffset? ExpiresAt = null,
    PlaybackHeaders? Headers = null,
    bool IsPreview = false,
    string? Codec = null,
    string? Container = null,
    int? BitrateKbps = null)
{
    /// <summary>解析得到的原始 URL 字符串，与音源返回逐字一致。</summary>
    public string RawUrl => Uri.OriginalString;
}

/// <summary>
/// Broker 级曲目解析结果（docs/plans §3.1 两级解析入口的 Broker 层）：
/// 跨源回退成功时携带实际生效的身份与元数据。现状跨源回退由聚合器直接回写
/// 共享 track 实例（调用方依赖该行为），Broker 保持回写的同时把结果显式返回。
/// </summary>
public sealed record ResolveTrackResult(
    string Url,
    ProviderTrackRef Track,
    string SourceName,
    IReadOnlyDictionary<string, string> ProviderMetadata,
    bool FellBack = false);

/// <summary>Broker 聚合解析的最终状态；预检只消费分类，不保存播放 URL。</summary>
public sealed record PlaybackResolutionOutcome(
    ResolveTrackResult? Track,
    PlaybackFailureKind Failure);
