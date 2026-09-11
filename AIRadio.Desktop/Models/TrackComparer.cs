using System;

namespace AIRadio.Desktop.Models;

/// <summary>
/// Utility methods for comparing tracks by identity.
/// </summary>
public static class TrackComparer
{
    /// <summary>
    /// Checks if two tracks refer to the same content.
    /// Asymmetric: if one side has a SourceId/FilePath/Id and the other doesn't, returns false.
    /// Used for "is this the currently playing track" comparisons.
    /// </summary>
    public static bool IsSameTrack(Track? left, Track? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        // SourceId/FilePath 忽略大小写，与 MusicIdentity.IsSameSource 口径一致
        if (!string.IsNullOrWhiteSpace(left.SourceId) &&
            string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(left.FilePath) &&
            string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(left.Id) && left.Id == right.Id;
    }

    /// <summary>
    /// Checks if two tracks share the same stable identity.
    /// Both sides must have the identifier for a match.
    /// Used for playlist deduplication where both tracks are stored references.
    /// </summary>
    public static bool IsSameTrackIdentity(Track? left, Track? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;

        if (!string.IsNullOrWhiteSpace(left.SourceId) && !string.IsNullOrWhiteSpace(right.SourceId) &&
            string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(left.FilePath) && !string.IsNullOrWhiteSpace(right.FilePath) &&
            string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id) &&
            left.Id == right.Id)
            return true;
        return false;
    }
}
