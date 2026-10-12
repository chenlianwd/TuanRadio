using System;
using System.Collections.Generic;

namespace AIRadio.Desktop.Services.Music;

/// <summary>全局源策略（演进计划阶段 4 UI 项）：影响跨源回退尝试顺序与搜索重排的源权重。</summary>
public enum MusicSourcePolicy
{
    /// <summary>覆盖优先（默认=现状）：回退按用户排序命中即停，源权重沿用既有静态表。</summary>
    Coverage,
    /// <summary>稳定优先：回退先尝试稳定源（自建/公开 API 在前，风控史源在后）。</summary>
    Stability,
    /// <summary>音质优先：回退先尝试已知高音质能力的源（OpenSubsonic 原始 > 网易/酷狗 > 其余）。</summary>
    Quality
}

/// <summary>
/// 源策略分级表：按 provider id 给稳定/音质两个维度的等级（0 最好）。
/// 稳定类依据自建性与风控史（酷狗 20028/网易登录态），音质类依据已知能力
/// （OpenSubsonic 原始可无损 > 网易 exhigh/酷狗 > Audius/酷我/咪咕；本地文件可无损
/// 但无法按文件静态判定，取中性）。仅影响回退顺序与搜索 15% 源权重，
/// 首选源仍由用户手动排序决定。
/// </summary>
public static class SourcePolicyClasses
{
    public const int DefaultStabilityClass = 2;
    public const int DefaultQualityClass = 2;

    private static readonly IReadOnlyDictionary<string, (int Stability, int Quality)> Classes =
        new Dictionary<string, (int Stability, int Quality)>(StringComparer.OrdinalIgnoreCase)
        {
            ["local"] = (0, 2),
            ["opensubsonic"] = (0, 0),
            ["audius"] = (1, 2),
            ["netease"] = (2, 1),
            ["kugou"] = (3, 1),
            ["kuwo"] = (3, 2),
            ["migu"] = (3, 2),
            ["youtube"] = (4, 4),
        };

    /// <summary>
    /// 取源在策略维度的等级。输入兼容 Descriptor.Id 与 CandidateRanker 历史口径的
    /// 展示别名（"网易"/"本地曲库"等）；未知源走中性默认等级。
    /// </summary>
    public static int GetClass(string? sourceToken, MusicSourcePolicy policy)
    {
        if (policy == MusicSourcePolicy.Coverage)
            return 0;
        var id = NormalizeSourceToken(sourceToken);
        (int Stability, int Quality) classes = id != null && Classes.TryGetValue(id, out var known)
            ? known
            : (DefaultStabilityClass, DefaultQualityClass);
        return policy == MusicSourcePolicy.Quality ? classes.Quality : classes.Stability;
    }

    private static string? NormalizeSourceToken(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        return source.Trim().ToLowerInvariant() switch
        {
            "local" or "local library" or "本地曲库" => "local",
            "opensubsonic" => "opensubsonic",
            "netease" or "网易" or "网易云音乐" => "netease",
            "kugou" or "酷狗" => "kugou",
            "kuwo" or "酷我" => "kuwo",
            "migu" or "咪咕" => "migu",
            "youtube" => "youtube",
            "audius" => "audius",
            _ => null
        };
    }
}
