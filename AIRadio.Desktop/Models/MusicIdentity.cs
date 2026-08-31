using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Models;

/// <summary>
/// Shared music identity comparison logic used by DJService and RecommendationService.
/// </summary>
public static class MusicIdentity
{
    public static bool IsSameSource(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool IsSameMusicIdentity(string titleA, string artistA, string titleB, string artistB)
    {
        var titleLeft = NormalizeMusicText(titleA);
        var titleRight = NormalizeMusicText(titleB);
        if (string.IsNullOrWhiteSpace(titleLeft) || titleLeft != titleRight)
            return false;

        var artistLeft = NormalizeMusicText(artistA);
        var artistRight = NormalizeMusicText(artistB);
        return string.IsNullOrWhiteSpace(artistLeft) ||
               string.IsNullOrWhiteSpace(artistRight) ||
               artistLeft == artistRight;
    }

    public static string NormalizeMusicText(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"[\s""'“”‘’《》<>。.!！?？,，;；:\-_/\\]+", "");

    /// <summary>
    /// 宽松同曲判定（跨源播放回退、搜索合并去重用）：标题全等 + 歌手双向包含。
    /// 与 <see cref="IsSameMusicIdentity"/> 的精确判定是有意并存的两档语义，勿互相替换。
    /// </summary>
    public static bool IsSameSongLoose(string titleA, string artistA, string titleB, string artistB)
    {
        var titleLeft = NormalizeLoose(titleA);
        var titleRight = NormalizeLoose(titleB);
        if (titleLeft.Length == 0 || titleRight.Length == 0 || titleLeft != titleRight)
            return false;

        var artistLeft = NormalizeLoose(artistA);
        var artistRight = NormalizeLoose(artistB);
        return artistLeft.Length == 0 || artistRight.Length == 0 ||
               artistLeft.Contains(artistRight, StringComparison.Ordinal) ||
               artistRight.Contains(artistLeft, StringComparison.Ordinal);
    }

    /// <summary>宽松归一化：仅保留字母数字并小写（括号、Live 标记等修饰全部剥离）。</summary>
    public static string NormalizeLoose(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // 括号类标题修饰：版本/发行类标记（live、official、官方MV、高清版、MV、4k 等）与 feat 署名。
    // 刻意不含 remix/cover/伴奏/纯音乐——那些是不同版本的歌曲，误配会播错曲。
    private static readonly Regex BracketedDecoration = new(
        @"(?i)[(\[【]\s*(?:live|official[^\])】]*|官方[^\])】]*|高清[^\])】]*|mv|m/v|video|audio|hq|hd|4k|explicit|remaster(?:ed)?|feat\.?[^\])】]*|ft\.[^\])】]*|现场版?)\s*[)\]】]",
        RegexOptions.Compiled);

    // 裸后缀修饰：标题结尾不带括号的 " MV" / " Official Video" / " Live" 等
    private static readonly Regex TrailingDecoration = new(
        @"(?i)\s+(?:official\s+(?:music\s+)?video|music\s+video|lyrics?\s+video|official\s+audio|video|audio|mv|m/v|live)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 剥离标题中的发行/版本修饰（"(Live)"、"【官方MV】"、结尾的 " Official Video" 等），
    /// 仅用于跨源回退候选的二次匹配，不参与精确身份与收藏去重。
    /// </summary>
    public static string StripTitleDecorations(string title)
        => TrailingDecoration.Replace(
            BracketedDecoration.Replace(title, " "),
            string.Empty).Trim();
}
