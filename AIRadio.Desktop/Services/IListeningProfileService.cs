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
    /// force=true（设置页手动重新归纳）跳过触发条件与失败退避，仍保留冷启动门槛、
    /// 在途互斥（撞上直接返回）与代次校验。
    /// </summary>
    Task RefreshDigestAsync(CancellationToken cancellationToken, bool force = false);

    /// <summary>
    /// 记录一条听众聊天消息进性格印象的归纳素材窗口（≤20 条滚动覆盖，每条截断 200 字，
    /// 入库前消毒控制标记）。入口过与事件采集相同的采集守卫：学习开关关闭即不落盘
    /// （聊天原文比收听事件敏感，「关闭 = 停止采集」契约同样适用）。
    /// </summary>
    void RecordListenerChat(string userMessage);

    /// <summary>
    /// 后台刷新 LLM 性格印象（从聊天素材窗口归纳）。内部自带水位（≥16 条）/语言触发、
    /// 并发互斥与失败退避，全异常内吞不抛出；聊天喂料路径可安全 fire-and-forget。
    /// 归纳成功后素材窗口清到本次归纳时的水位（在途新消息保留待下轮）。
    /// force=true（设置页手动重新归纳）跳过触发条件与失败退避，仍保留素材要求、
    /// 在途互斥（撞上直接返回）与代次校验。
    /// </summary>
    Task RefreshImpressionAsync(CancellationToken cancellationToken, bool force = false);

    /// <summary>
    /// 黑名单单条移除（设置页「不爱听的歌」）：删除该曲目（归一化 Title|Artist 键）的全部
    /// Dislike 事件，统计随事件派生自然重算；其它类型事件不动、水位不回退、digest 不触发
    /// 再生（旧归纳文本可能残留，由手动重新归纳兜底）。返回是否删到事件；空歌手键
    /// 永不匹配（黑名单构建本就不收空歌手）。
    /// </summary>
    bool RemoveDislikeEvents(string? title, string? artist);

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

    /// <summary>听歌行为指标（快照现算）：完成率 = Completed/(Completed+Skipped)，样本 ≥10 条才有值。</summary>
    public double CompletionRate { get; init; }
    public bool HasCompletionSample { get; init; }

    /// <summary>新歌手占比：最近 30 天窗口内出现过的歌手中「全部可见事件最早记录也落在窗口内」的比例。
    /// 样本（窗口歌手 ≥5）不足时无值。已知偏差：事件上限 1000 条裁旧后史前歌手会被误判为新歌手，
    /// 该指标只增不减——注入文案以相对倾向解读，不当精确统计播报。</summary>
    public double NewArtistRatio { get; init; }
    public bool HasExplorationSample { get; init; }

    /// <summary>主要收听时段（night/morning/afternoon/evening）；播放样本 &lt;10 时为 null。</summary>
    public string? DominantTimeBand { get; init; }

    /// <summary>听歌习惯人格特质 key（ListeningPersona 派生），文案由消费点按语言域映射。</summary>
    public IReadOnlyList<string> PersonaTraits { get; init; } = Array.Empty<string>();

    public ListenerImpressionData? Impression { get; init; }

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

/// <summary>LLM 归纳的口味摘要（digest）。EventWatermark 对齐单调计数 TotalEventsIngested。
/// Text 恒为解析后的总评纯文本（绝不存 LLM 原始 JSON 串——注入链与测试的既有契约）；
/// StyleTags 为提炼的风格标签（流派/年代/语言/氛围，旧数据/纯文本回退时为空）。</summary>
public class TasteDigestData
{
    public string Text { get; set; } = string.Empty;
    public List<string> StyleTags { get; set; } = new();
    public string Language { get; set; } = "zh";
    public DateTime GeneratedAt { get; set; }
    public long EventWatermark { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>LLM 从聊天内容归纳的听众性格印象。归纳素材（PendingChats）成功消费后清到水位，
/// 聊天原文不在画像文件中长期保留。Text/Traits 语义与 TasteDigestData 同构但水位机制独立。</summary>
public class ListenerImpressionData
{
    public string Text { get; set; } = string.Empty;
    public List<string> Traits { get; set; } = new();
    public string Language { get; set; } = "zh";
    public DateTime GeneratedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
}
