using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 音源搜索与跨源回退候选智能排重与评分器。
/// 综合评估：标题规范化匹配（35%）、歌手精确匹配（30%）、时长接近度（20%）、音源优先级（15%），
/// 并对伴奏/翻唱/DJ版/Live等非预期版本施加负向约束惩罚（-0.3 ~ -0.7）。
/// </summary>
public static class CandidateRanker
{
    private static readonly Regex InstrumentalRegex = new(
        @"(?i)(?:伴奏|纯音乐|卡拉\s*ok|karaoke|instrumental|\binst\b|\boff\s*vocal\b|\bminus\s*one\b)",
        RegexOptions.Compiled);

    private static readonly Regex CoverRegex = new(
        @"(?i)(?:翻唱版?|cover\s*by\b|[(\[【]\s*(?:cover|翻唱)[^)\]】]*[)\]】]|[\s\-_/]+(?:cover|翻唱)$)",
        RegexOptions.Compiled);

    private static readonly Regex RemixRegex = new(
        @"(?i)(?:dj[^\s\)\]】]*版|[(\[【]\s*dj(?:\s*remix|\s*mix|\s*edit|\s*ver|\s*version|\s*版|\s*慢摇|\s*电音)?\s*[)\]】]|(?:^|[\s\-_])dj(?:\s*remix|\s*mix|\s*edit|\s*ver|\s*version|\s*版|\s*慢摇|\s*电音)|[\s\-_]dj$|remix|加速版|慢速版|降调版|升调版|电音版|慢摇)",
        RegexOptions.Compiled);

    private static readonly Regex LiveRegex = new(
        @"(?i)(?:现场版?|演唱会版?|音乐节(?:现场|版)|[(\[【]\s*live[^)\]】]*[)\]】]|[\s\-_/]+live$)",
        RegexOptions.Compiled);

    private static readonly char[] ArtistSeparators = { '/', ',', '&', '、', '，', ';', '|' };

    public static bool HasInstrumentalTag(string? text)
        => !string.IsNullOrWhiteSpace(text) && InstrumentalRegex.IsMatch(text);

    public static bool HasCoverTag(string? text)
        => !string.IsNullOrWhiteSpace(text) && CoverRegex.IsMatch(text);

    public static bool HasRemixTag(string? text)
        => !string.IsNullOrWhiteSpace(text) && RemixRegex.IsMatch(text);

    public static bool HasLiveTag(string? text)
        => !string.IsNullOrWhiteSpace(text) && LiveRegex.IsMatch(text);

    /// <summary>
    /// 判断候选歌曲是否属于非预期的特殊版本（伴奏/翻唱/电音DJ/加速等）。
    /// 若 referenceText（目标曲目名或搜索词）未主动包含对应标签，则视为非预期版本。
    /// </summary>
    public static bool IsUnwantedVersion(OnlineTrack candidate, string? referenceText = null)
    {
        var target = referenceText ?? string.Empty;
        var candText = $"{candidate.Title} {candidate.Artist}";

        if (!HasInstrumentalTag(target) && HasInstrumentalTag(candText))
            return true;
        if (!HasCoverTag(target) && HasCoverTag(candText))
            return true;
        if (!HasRemixTag(target) && HasRemixTag(candText))
            return true;

        return false;
    }

    /// <summary>
    /// 对跨源播放回退候选进行综合评分（分数区间理论为 [-1.0, 1.0]）。
    /// </summary>
    public static double ScoreFallbackCandidate(OnlineTrack candidate, OnlineTrack target)
    {
        // 1. 标题匹配度（35%）
        var titleScore = ScoreTitle(candidate.Title, target.Title);

        // 2. 歌手匹配度（30%）
        var artistScore = ScoreArtist(candidate.Artist, target.Artist);

        // 3. 时长接近度（20%）
        var durationScore = ScoreDuration(candidate.DurationMs, target.DurationMs);

        // 4. 音源优先级与健康度（15%）
        var sourceScore = ScoreSource(candidate.Source);

        var baseScore = (0.35 * titleScore) + (0.30 * artistScore) + (0.20 * durationScore) + (0.15 * sourceScore);

        // 5. 版本冲突负向约束惩罚
        var targetFull = $"{target.Title} {target.Artist}";
        var candFull = $"{candidate.Title} {candidate.Artist}";
        var penalty = 0.0;

        if (!HasInstrumentalTag(targetFull) && HasInstrumentalTag(candFull))
            penalty += 0.70;

        if (!HasCoverTag(targetFull) && HasCoverTag(candFull))
            penalty += 0.60;

        if (!HasRemixTag(targetFull) && HasRemixTag(candFull))
            penalty += 0.50;

        if (!HasLiveTag(targetFull) && HasLiveTag(candFull))
            penalty += 0.30;

        return baseScore - penalty;
    }

    /// <summary>
    /// 对搜索结果单项进行评分。
    /// </summary>
    public static double ScoreSearchResult(OnlineTrack candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 0.5;

        var candTitle = candidate.Title ?? string.Empty;
        var candArtist = candidate.Artist ?? string.Empty;
        var candFull = $"{candTitle} {candArtist}";

        // 标题与查询词重合度
        var titleSim = ScoreTitle(candTitle, query);

        // 歌手是否在查询词中
        var artistSim = 0.5;
        if (!string.IsNullOrWhiteSpace(candArtist))
        {
            var artists = SplitArtists(candArtist);
            if (artists.Any(a => query.Contains(a, StringComparison.OrdinalIgnoreCase)))
                artistSim = 1.0;
            else
                artistSim = CalculateStringSimilarity(candArtist, query);
        }

        var sourceScore = ScoreSource(candidate.Source);
        var baseScore = (0.50 * titleSim) + (0.35 * artistSim) + (0.15 * sourceScore);

        var penalty = 0.0;
        if (!HasInstrumentalTag(query) && HasInstrumentalTag(candFull))
            penalty += 0.70;
        if (!HasCoverTag(query) && HasCoverTag(candFull))
            penalty += 0.60;
        if (!HasRemixTag(query) && HasRemixTag(candFull))
            penalty += 0.50;
        if (!HasLiveTag(query) && HasLiveTag(candFull))
            penalty += 0.30;

        return baseScore - penalty;
    }

    /// <summary>
    /// 对回退候选列表排序（由高到低）。
    /// </summary>
    public static IReadOnlyList<OnlineTrack> RankFallbackCandidates(
        IEnumerable<OnlineTrack> candidates,
        OnlineTrack target)
    {
        return candidates
            .Select(c => (Track: c, Score: ScoreFallbackCandidate(c, target)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Track)
            .ToList();
    }

    /// <summary>
    /// 对搜索结果列表按评分由高到低重排。
    /// </summary>
    public static IReadOnlyList<OnlineTrack> RankSearchResults(
        IEnumerable<OnlineTrack> candidates,
        string query)
    {
        return candidates
            .Select(c => (Track: c, Score: ScoreSearchResult(c, query)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Track)
            .ToList();
    }

    /// <summary>
    /// 基于宽松同曲语义对在线音源列表进行智能去重（剥离修饰后比对，保留高分或先出现的版本）。
    /// </summary>
    public static List<OnlineTrack> DeduplicateTracks(IEnumerable<OnlineTrack> tracks)
    {
        var result = new List<OnlineTrack>();
        foreach (var track in tracks)
        {
            var titleClean = MusicIdentity.StripTitleDecorations(track.Title ?? string.Empty);
            if (result.Any(existing => MusicIdentity.IsSameSongLoose(
                MusicIdentity.StripTitleDecorations(existing.Title ?? string.Empty),
                existing.Artist,
                titleClean,
                track.Artist)))
            {
                continue;
            }
            result.Add(track);
        }
        return result;
    }

    /// <summary>
    /// 拆分多歌手名称（支持斜杠、逗号、顿号、feat 等）。
    /// </summary>
    public static string[] SplitArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Array.Empty<string>();

        var normalized = Regex.Replace(artist, @"(?i)\s+(?:feat\.?|ft\.?)\s+", " / ");
        return normalized.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static double ScoreTitle(string candidateTitle, string targetTitle)
    {
        var targetClean = MusicIdentity.StripTitleDecorations(targetTitle ?? string.Empty);
        var candClean = MusicIdentity.StripTitleDecorations(candidateTitle ?? string.Empty);

        var targetNorm = MusicIdentity.NormalizeLoose(targetClean);
        var candNorm = MusicIdentity.NormalizeLoose(candClean);

        if (targetNorm.Length == 0 && candNorm.Length == 0)
            return 1.0;
        if (targetNorm.Length == 0 || candNorm.Length == 0)
            return 0.0;

        if (string.Equals(targetNorm, candNorm, StringComparison.Ordinal))
            return 1.0;

        if (targetNorm.Contains(candNorm, StringComparison.Ordinal) || candNorm.Contains(targetNorm, StringComparison.Ordinal))
        {
            // 包含关系说明候选标题可信，但恒定地板会把标题维度压成常数："歌手+歌名"查询
            // （如"周杰伦晴天"）下所有正常候选都是子串，"Love" 与 "Love Story" 将完全并列。
            // 仿射映射 ratio → [0.55, 1.0]：包含仍稳定优于 Dice 相似度（典型 0.2~0.4），
            // 同时对解释比例（短串/长串长度）单调，恢复候选间的区分度。
            var ratio = (double)Math.Min(targetNorm.Length, candNorm.Length) / Math.Max(targetNorm.Length, candNorm.Length);
            return 0.55 + 0.45 * ratio;
        }

        return CalculateStringSimilarity(targetNorm, candNorm);
    }

    private static double ScoreArtist(string candidateArtist, string targetArtist)
    {
        var targetClean = (targetArtist ?? string.Empty).Trim();
        var candClean = (candidateArtist ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(targetClean))
            return 0.5; // 目标无歌手，不作严厉扣分
        if (string.IsNullOrWhiteSpace(candClean))
            return 0.2;

        var targetArtists = SplitArtists(targetClean);
        var candArtists = SplitArtists(candClean);

        foreach (var t in targetArtists)
        {
            var tNorm = MusicIdentity.NormalizeLoose(t);
            if (tNorm.Length == 0) continue;

            foreach (var c in candArtists)
            {
                var cNorm = MusicIdentity.NormalizeLoose(c);
                if (cNorm.Length == 0) continue;

                if (string.Equals(tNorm, cNorm, StringComparison.Ordinal))
                    return 1.0;

                if (tNorm.Contains(cNorm, StringComparison.Ordinal) || cNorm.Contains(tNorm, StringComparison.Ordinal))
                    return 0.85;
            }
        }

        var directSim = CalculateStringSimilarity(
            MusicIdentity.NormalizeLoose(targetClean),
            MusicIdentity.NormalizeLoose(candClean));
        return directSim;
    }

    private static double ScoreDuration(long candidateDurationMs, long targetDurationMs)
    {
        if (targetDurationMs <= 0)
            return 0.5;

        if (candidateDurationMs <= 0)
            return -0.1; // 候选缺少时长元数据，劣于有时长的候选

        var diffSeconds = Math.Abs((candidateDurationMs - targetDurationMs) / 1000.0);

        if (diffSeconds <= 3.0)
            return 1.0;
        if (diffSeconds <= 8.0)
            return 0.90;
        if (diffSeconds <= 15.0)
            return 0.70;
        if (diffSeconds <= 30.0)
            return 0.40;
        if (diffSeconds <= 60.0)
            return 0.10;

        return 0.0; // 偏差大于 1 分钟（试听切片/错曲/完整演出长音频）直接给 0 分
    }

    private static double ScoreSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 0.80;

        return source.Trim().ToLowerInvariant() switch
        {
            "netease" or "网易" => 1.0,
            "kugou" or "酷狗" => 0.95,
            "kuwo" or "酷我" => 0.90,
            "migu" or "咪咕" => 0.85,
            "youtube" => 0.75,
            _ => 0.80
        };
    }

    /// <summary>
    /// 基于字符 Bigram 的 Dice 相似度计算（0.0 ~ 1.0），对中英文拼写容错性良好。
    /// </summary>
    private static double CalculateStringSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0.0;

        if (s1 == s2)
            return 1.0;

        if (s1.Length < 2 || s2.Length < 2)
            return s1.Contains(s2, StringComparison.OrdinalIgnoreCase) || s2.Contains(s1, StringComparison.OrdinalIgnoreCase) ? 0.7 : 0.0;

        var bigrams1 = GetBigrams(s1);
        var bigrams2 = GetBigrams(s2);

        var intersection = 0;
        foreach (var b in bigrams1)
        {
            if (bigrams2.Remove(b))
                intersection++;
        }

        // 上面的 Remove 已把 bigrams2 缩减为 |B|-|交集|，补回交集数恰好还原 Dice 分母 |A|+|B|
        var total = bigrams1.Count + (bigrams2.Count + intersection);
        return total > 0 ? (2.0 * intersection) / total : 0.0;
    }

    private static List<string> GetBigrams(string s)
    {
        var bigrams = new List<string>(Math.Max(0, s.Length - 1));
        for (var i = 0; i < s.Length - 1; i++)
        {
            bigrams.Add(s.Substring(i, 2));
        }
        return bigrams;
    }
}
