using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AIRadio.Desktop.Services;

namespace AIRadio.Desktop.Models;

/// <summary>
/// 播客单集实体，支持断点续播进度展示与转换为通用 Track 模型播放。
/// </summary>
public sealed class PodcastEpisode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private long _resumePositionMs;
    private bool _isCompleted;
    private bool _isDigesting;

    public string EpisodeKey { get; init; } = string.Empty;
    public string FeedUrl { get; init; } = string.Empty;
    public string FeedTitle { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string AudioUrl { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Codec { get; init; } = string.Empty;
    public int BitrateKbps { get; init; }

    public long ResumePositionMs
    {
        get => _resumePositionMs;
        set
        {
            var clamped = Math.Max(0, value);
            if (_resumePositionMs == clamped)
                return;
            _resumePositionMs = clamped;
            NotifyProgressChanged();
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value)
                return;
            _isCompleted = value;
            NotifyProgressChanged();
        }
    }

    public bool IsDigesting
    {
        get => _isDigesting;
        set
        {
            if (_isDigesting == value)
                return;
            _isDigesting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDigesting)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DigestButtonText)));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasResumePosition => !IsCompleted && ResumePositionMs > 3000;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasProgressBadge => IsCompleted || HasResumePosition;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DigestButtonText => IsDigesting
        ? AppLanguage.Get("S_PodcastDigesting")
        : AppLanguage.Get("S_PodcastAiDigest");

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayProgressBadge
    {
        get
        {
            if (IsCompleted)
                return AppLanguage.T("已播完", "Played");
            if (HasResumePosition)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    AppLanguage.T("续播 {0}", "Resume {0}"),
                    FormatDuration(TimeSpan.FromMilliseconds(ResumePositionMs)));
            return string.Empty;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ShortSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Summary))
                return string.Empty;
            return Summary.Length <= 140 ? Summary : Summary[..140].TrimEnd() + "...";
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayMeta
    {
        get
        {
            var parts = new List<string>(3);
            if (PublishedAt.HasValue)
                parts.Add(PublishedAt.Value.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (Duration > TimeSpan.Zero)
                parts.Add(FormatDuration(Duration));
            if (!string.IsNullOrWhiteSpace(Codec))
                parts.Add(Codec.ToUpperInvariant());
            return string.Join(" · ", parts);
        }
    }

    public void RefreshLocalization()
    {
        NotifyProgressChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DigestButtonText)));
    }

    private void NotifyProgressChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResumePositionMs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResumePosition)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProgressBadge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayProgressBadge)));
    }

    public Track ToTrack(bool startFromBeginning = false)
    {
        var sourceId = PodcastFeed.SourcePrefix + EpisodeKey;
        var effectiveResumeMs = !startFromBeginning && HasResumePosition ? ResumePositionMs : 0L;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_podcast"] = "true",
            ["episode_key"] = EpisodeKey,
            ["feed_url"] = FeedUrl,
            ["feed_title"] = FeedTitle,
            ["audio_url"] = AudioUrl,
            ["summary"] = Summary,
            ["resume_ms"] = effectiveResumeMs.ToString(CultureInfo.InvariantCulture)
        };

        return new Track
        {
            Id = sourceId,
            SourceId = sourceId,
            Title = Title,
            Artist = !string.IsNullOrWhiteSpace(FeedTitle) ? FeedTitle : Author,
            Album = AppLanguage.T("播客节目", "Podcast"),
            Duration = Duration,
            FilePath = AudioUrl,
            DeclaredCodec = string.IsNullOrWhiteSpace(Codec) ? null : Codec.ToUpperInvariant(),
            DeclaredBitrateKbps = BitrateKbps > 0 ? BitrateKbps : null,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
            ProviderMetadata = metadata
        };
    }

    public static string BuildEpisodeKey(string feedUrl, string? guid, string audioUrl)
    {
        var identity = string.IsNullOrWhiteSpace(guid) ? audioUrl.Trim() : guid.Trim();
        var raw = $"{feedUrl.Trim().ToLowerInvariant()}|{identity}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "00:00";
        return duration.TotalHours >= 1.0
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
