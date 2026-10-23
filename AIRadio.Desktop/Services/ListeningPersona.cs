using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 听歌习惯人格特质：从快照行为指标派生稳定 key（completionist/skipper/explorer/loyal/
/// night_owl/early_bird），文案经 TraitText 双语映射。key 层稳定（快照/测试锚定），
/// 文案由消费点按各自语言域现取：展示卡跟界面语言、DJ 注入跟人设语言——两语言域
/// 并存是项目既有分工（现场段/天气段同款）。
/// </summary>
internal static class ListeningPersona
{
    public const string Completionist = "completionist";
    public const string Skipper = "skipper";
    public const string Explorer = "explorer";
    public const string Loyal = "loyal";
    public const string NightOwl = "night_owl";
    public const string EarlyBird = "early_bird";

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
