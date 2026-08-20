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
}
