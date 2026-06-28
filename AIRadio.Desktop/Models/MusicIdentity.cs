using System;
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
}
