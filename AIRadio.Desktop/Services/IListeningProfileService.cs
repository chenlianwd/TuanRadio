using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 长期收听画像（docs/plans/2026-09-14-long-term-listener-profile-design.md）。
/// 持久化本地收听事件并派生歌手亲和度/黑名单/氛围历史/LLM 口味摘要，供推荐链路跨会话使用。
/// </summary>
public interface IListeningProfileService
{
    /// <summary>设置页「学习我的口味」开关。关闭 = 停止采集且 GetSnapshot 返回空快照；数据保留，重开即恢复。</summary>
    bool Enabled { get; set; }

    /// <summary>
    /// 当前画像只读快照；未加载/已禁用/无数据时返回空快照（推荐侧按现状运行，无需判 null）。
    /// </summary>
    ListenerProfileSnapshot GetSnapshot();

    /// <summary>记录单条收听事件（按钮反馈/聊天 MoodSet 等非播放态信号）。</summary>
    void RecordEvent(ListeningEventData eventData);

    /// <summary>曲目进入 Playing 态：记录 Played 事件（同曲目 10 分钟窗口去重）并更新跳过判定状态。</summary>
    void NotifyPlaybackStarted(Track track);

    /// <summary>曲目自然播完（TrackEnded）：记录 Completed 事件并清除跳过判定状态。</summary>
    void NotifyPlaybackEndedNaturally(Track track);

    /// <summary>进度采样（500ms 粒度）：样本与曲目身份绑定，供手动切歌时判定跳过。</summary>
    void NotifyPositionSampled(Track track, TimeSpan position);

    /// <summary>
    /// 手动切歌判定（TrackChanged）：前曲处于播放态、新曲身份不同、样本满足
    /// 进度 &lt;30% 且已播 ≥10s 且时长 ≥60s 时记 Skipped 事件。重放同曲（URL 恢复）不记。
    /// </summary>
    void NotifyTrackSwitched(Track? next);

    /// <summary>暂停时清除跳过待判状态：暂停后再切歌不算跳过（前曲非播放态）。</summary>
    void NotifyPlaybackPaused();

    /// <summary>清除全部画像数据（内存 + 磁盘文件 + 作废在途归纳）。</summary>
    void Reset();

    /// <summary>从磁盘加载画像；文件缺失/损坏按空画像运行，不抛异常。</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 后台刷新 LLM 口味摘要。内部自带水位/老化/语言触发条件、并发互斥与失败退避，
    /// 全异常内吞不抛出；节目单生成路径可安全 fire-and-forget。
    /// </summary>
    Task RefreshDigestAsync(CancellationToken cancellationToken);

    /// <summary>尽力立即落盘（退出路径有界等待用）；防抖窗口内由内部定时器兜底。</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>画像只读快照：统计与摘要均由事件派生，快照不可变。</summary>
public sealed class ListenerProfileSnapshot
{
    public static readonly ListenerProfileSnapshot Empty = new();

    public long TotalEventsIngested { get; init; }
    public IReadOnlyList<ArtistAffinity> TopArtists { get; init; } = Array.Empty<ArtistAffinity>();
    public IReadOnlyList<ArtistAffinity> AvoidArtists { get; init; } = Array.Empty<ArtistAffinity>();
    public IReadOnlyList<ProfileMusicRef> DislikeBlacklist { get; init; } = Array.Empty<ProfileMusicRef>();
    public IReadOnlyDictionary<string, int> MoodUsage { get; init; } = new Dictionary<string, int>();
    public TasteDigestData? Digest { get; init; }

    /// <summary>推荐注入门槛（冷启动防护）：累计 ≥30 事件且 ≥3 个歌手。</summary>
    public bool MeetsInjectionThreshold
        => TotalEventsIngested >= ListeningProfileService.InjectionMinEvents &&
           TopArtists.Count >= ListeningProfileService.InjectionMinArtists;
}

/// <summary>歌手亲和度：衰减加权累计分，见设计文档 §4.2。</summary>
public sealed class ArtistAffinity
{
    public string Artist { get; init; } = string.Empty;
    public double Score { get; init; }
    public int PlayCount { get; init; }
    public DateTime LastPlayedAt { get; init; }
}

/// <summary>黑名单曲目引用：匹配走 MusicIdentity.IsSameMusicIdentity（保留空歌手通配语义）。</summary>
public sealed record ProfileMusicRef(string Title, string Artist);

/// <summary>持久化事件（listener-profile.json）。Type=MoodSet 时 Detail 存归一化氛围值。</summary>
public class ListeningEventData
{
    public ListeningEventType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public string? Detail { get; set; }
    public DateTime Time { get; set; }
}

public enum ListeningEventType
{
    Played,
    Completed,
    Skipped,
    Like,
    Dislike,
    Similar,
    Calmer,
    Energetic,
    MoodSet,
}

/// <summary>LLM 归纳的口味摘要（digest）。EventWatermark 对齐单调计数 TotalEventsIngested。</summary>
public class TasteDigestData
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = "zh";
    public DateTime GeneratedAt { get; set; }
    public long EventWatermark { get; set; }
    public int ConsecutiveFailures { get; set; }
}
