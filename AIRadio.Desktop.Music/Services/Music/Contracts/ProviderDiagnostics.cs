using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

/// <summary>带逐源报告的搜索结果：报告与结果同源，杜绝读到别的并发请求的状态。</summary>
public sealed record SearchOutcome(List<OnlineTrack> Tracks, IReadOnlyList<SourceSearchStatus> Report);

/// <summary>单个音源搜索状态（成功/超时/失败 + 原因；Note 附加说明如"已过滤"；
/// FailureKind 为业务失败的结构化分类，供 UI 渲染用户可读文案，默认 None=非业务失败）。
/// 命名空间保持 AIRadio.Desktop.Services：PlaylistViewModel 等消费方以 Services. 前缀引用，
/// 迁入 Music/Contracts 只挪文件不换命名空间（docs/plans 2026-09-20 §3.6）。</summary>
public record SourceSearchStatus(
    string Name,
    string Status,
    int Count,
    string? Error,
    string? Note = null,
    MusicSourceFailureKind FailureKind = MusicSourceFailureKind.None);
