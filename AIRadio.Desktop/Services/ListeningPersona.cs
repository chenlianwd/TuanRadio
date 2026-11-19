using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 听歌习惯人格特质、时段分桶与曲目语种辅助：从快照行为指标派生稳定 key（completionist/skipper/explorer/loyal/
/// night_owl/early_bird），文案经双语映射按各自语言域现取（展示卡跟界面语言、DJ 注入跟人设语言）。
/// </summary>
internal static class ListeningPersona
{
    public const string Completionist = "completionist";
    public const string Skipper = "skipper";
    public const string Explorer = "explorer";
    public const string Loyal = "loyal";
    public const string NightOwl = "night_owl";
    public const string EarlyBird = "early_bird";

    public static readonly string[] TimeBandOrder = ["morning", "afternoon", "evening", "night"];

    /// <summary>时段分桶索引：0=night(22~5 点)，1=morning(5~11)，2=afternoon(11~17)，3=evening(17~22)。</summary>
    internal static int TimeBandIndex(DateTime time) => time.Hour switch
    {
        >= 22 or < 5 => 0,
        < 11 => 1,
        < 17 => 2,
        _ => 3,
    };

    /// <summary>根据时间解析统一时段 key（night/morning/afternoon/evening）。</summary>
    internal static string ResolveTimeBand(DateTime time) => TimeBandIndex(time) switch
    {
        0 => "night",
        1 => "morning",
        2 => "afternoon",
        _ => "evening",
    };

    /// <summary>时段 key → 双语文案。</summary>
    internal static string TimeBandLabel(string band, bool isEn) => (band, isEn) switch
    {
        ("night", false) => "深夜",
        ("night", true) => "late night",
        ("morning", false) => "上午",
        ("morning", true) => "morning",
        ("afternoon", false) => "午后",
        ("afternoon", true) => "afternoon",
        ("evening", false) => "晚间",
        ("evening", true) => "evening",
        _ => band,
    };

    /// <summary>
    /// 基于标题与歌手文本脚本特征推断语种（"ja"/"ko"/"zh"/"en"/null）。
    /// 优先级：日文假名 → 韩文音节/字母 → CJK 汉字 → 拉丁字母。
    /// 注：纯汉字写的日语歌名且无假名时按中文处理（纯本地零词典推理边界）。
    /// </summary>
    internal static string? DetectTrackLanguage(string? title, string? artist)
    {
        var hasKana = false;
        var hasHangul = false;
        var hasCjk = false;
        var hasLatin = false;

        Scan(title);
        Scan(artist);

        if (hasKana) return "ja";
        if (hasHangul) return "ko";
        if (hasCjk) return "zh";
        if (hasLatin) return "en";
        return null;

        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var c in text)
            {
                if (c is (>= '\u3040' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF'))
                    hasKana = true;
                else if (c is (>= '\uAC00' and <= '\uD7AF') or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F'))
                    hasHangul = true;
                else if (c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF'))
                    hasCjk = true;
                else if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
                    hasLatin = true;
            }
        }
    }

    internal static string LanguageLabel(string code, bool isEn) => (code, isEn) switch
    {
        ("zh", false) => "华语",
        ("zh", true) => "Chinese",
        ("en", false) => "欧美",
        ("en", true) => "English",
        ("ja", false) => "日语",
        ("ja", true) => "Japanese",
        ("ko", false) => "韩语",
        ("ko", true) => "Korean",
        _ => code,
    };

    internal static string FormatLanguageMix(
        IReadOnlyList<LanguageShare> items,
        bool isEn,
        string separator,
        int maxItems = 4)
    {
        if (items.Count == 0)
            return string.Empty;
        return string.Join(separator, items.Take(maxItems).Select(item =>
            $"{LanguageLabel(item.LanguageCode, isEn)} {Math.Round(item.Share * 100):0}%"));
    }

    internal static string FormatTimeBandSummary(TimeBandPreference pref, bool isEn)
    {
        var separator = isEn ? ", " : "、";
        var parts = new List<string>();
        if (pref.TopArtists.Count > 0)
            parts.Add(string.Join(separator, pref.TopArtists.Take(2)));
        if (pref.TopMoods.Count > 0)
            parts.Add(string.Join(separator, pref.TopMoods.Take(2)));
        if (parts.Count == 0 && !string.IsNullOrEmpty(pref.DominantLanguage))
            parts.Add(LanguageLabel(pref.DominantLanguage, isEn));
        return string.Join(isEn ? " · " : " · ", parts);
    }

    /// <summary>
    /// 行为特质派生。样本不足的维度直接跳过；完成率与探索率都有「中间带」
    /// （0.4~0.7 / 0.15~0.4）不标注——中间带是常态，贴标签反而是噪音。
    /// </summary>
    internal static List<string> DerivePersonaTraits(
        bool hasCompletionSample,
        double completionRate,
        bool hasExplorationSample,
        double newArtistRatio,
        string? dominantTimeBand)
    {
        var traits = new List<string>();
        if (hasCompletionSample && completionRate >= 0.7)
            traits.Add(Completionist);
        else if (hasCompletionSample && completionRate <= 0.4)
            traits.Add(Skipper);

        if (hasExplorationSample && newArtistRatio >= 0.4)
            traits.Add(Explorer);
        else if (hasExplorationSample && newArtistRatio <= 0.15)
            traits.Add(Loyal);

        if (dominantTimeBand == "night")
            traits.Add(NightOwl);
        else if (dominantTimeBand == "morning")
            traits.Add(EarlyBird);

        return traits;
    }

    /// <summary>特质 key → 双语文案。isEn 由调用方按语言域传入（界面语言或 DJ 人设语言）。</summary>
    internal static string TraitText(string trait, bool isEn) => (trait, isEn) switch
    {
        (Completionist, false) => "爱完整听完",
        (Completionist, true) => "Plays songs through",
        (Skipper, false) => "快切型听众",
        (Skipper, true) => "Quick skipper",
        (Explorer, false) => "爱探索新歌手",
        (Explorer, true) => "Explores new artists",
        (Loyal, false) => "口味专一",
        (Loyal, true) => "Loyal to favorites",
        (NightOwl, false) => "深夜听歌",
        (NightOwl, true) => "Late-night listener",
        (EarlyBird, false) => "清晨听歌",
        (EarlyBird, true) => "Early-morning listener",
        _ => trait,
    };
}
