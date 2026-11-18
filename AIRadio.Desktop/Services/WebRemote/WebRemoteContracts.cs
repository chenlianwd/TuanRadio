using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIRadio.Desktop.Services.WebRemote;

public sealed record WebRemoteLyricLine(
    [property: JsonPropertyName("timeMs")] long TimeMs,
    [property: JsonPropertyName("text")] string Text);

public sealed record WebRemoteChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("timestamp")] string Timestamp);

public sealed record WebRemoteSnapshot
{
    [JsonPropertyName("language")] public string Language { get; init; } = "zh";
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("artist")] public string Artist { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("quality")] public string Quality { get; init; } = string.Empty;
    [JsonPropertyName("isPreview")] public bool IsPreview { get; init; }
    [JsonPropertyName("hasTrack")] public bool HasTrack { get; init; }
    [JsonPropertyName("isPlaying")] public bool IsPlaying { get; init; }
    [JsonPropertyName("isLiveStream")] public bool IsLiveStream { get; init; }
    [JsonPropertyName("isPodcast")] public bool IsPodcast { get; init; }
    [JsonPropertyName("isFavorite")] public bool IsFavorite { get; init; }
    [JsonPropertyName("positionSeconds")] public double PositionSeconds { get; init; }
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("canSeek")] public bool CanSeek { get; init; }
    [JsonPropertyName("volume")] public float Volume { get; init; } = 0.8f;
    [JsonPropertyName("isShuffled")] public bool IsShuffled { get; init; }
    [JsonPropertyName("repeatMode")] public string RepeatMode { get; init; } = "radio";
    [JsonPropertyName("repeatLabel")] public string RepeatLabel { get; init; } = "DJ";
    [JsonPropertyName("radioState")] public string RadioState { get; init; } = "Idle";
    [JsonPropertyName("sceneMode")] public string SceneMode { get; init; } = "Default";
    [JsonPropertyName("isFocusMode")] public bool IsFocusMode { get; init; }
    [JsonPropertyName("isSleepTimerActive")] public bool IsSleepTimerActive { get; init; }
    [JsonPropertyName("sleepTimerText")] public string SleepTimerText { get; init; } = string.Empty;
    [JsonPropertyName("canStopAfterCurrentTrack")] public bool CanStopAfterCurrentTrack { get; init; }
    [JsonPropertyName("sleepTimerMinutes")] public int SleepTimerMinutes { get; init; }
    [JsonPropertyName("djName")] public string DjName { get; init; } = string.Empty;
    [JsonPropertyName("djAccentHex")] public string DjAccentHex { get; init; } = "#56F5C4";
    [JsonPropertyName("isDjSpeaking")] public bool IsDjSpeaking { get; init; }
    [JsonPropertyName("isDjProcessing")] public bool IsDjProcessing { get; init; }
    [JsonPropertyName("hasLyrics")] public bool HasLyrics { get; init; }
    [JsonPropertyName("lyricStatusText")] public string LyricStatusText { get; init; } = string.Empty;
    [JsonPropertyName("currentLyricIndex")] public int CurrentLyricIndex { get; init; } = -1;
    [JsonPropertyName("lyricLines")] public IReadOnlyList<WebRemoteLyricLine> LyricLines { get; init; } = Array.Empty<WebRemoteLyricLine>();
    [JsonPropertyName("messages")] public IReadOnlyList<WebRemoteChatMessage> Messages { get; init; } = Array.Empty<WebRemoteChatMessage>();
    [JsonPropertyName("isProgramLoading")] public bool IsProgramLoading { get; init; }
    [JsonPropertyName("digestingEpisodeKey")] public string DigestingEpisodeKey { get; init; } = string.Empty;
    [JsonPropertyName("serverTimeUtcMs")] public long ServerTimeUtcMs { get; init; }
}

public sealed record WebRemoteBrowseRequest
{
    public string Section { get; init; } = "program";
    public string? Category { get; init; }
    public string? Feed { get; init; }
    public string? Query { get; init; }
    public bool Force { get; init; }
}

public sealed record WebRemoteProgramTrackItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("playability")] string Playability,
    [property: JsonPropertyName("isPlayable")] bool IsPlayable,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);

public sealed record WebRemoteTrackItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("isFavorite")] bool IsFavorite,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent,
    [property: JsonPropertyName("isLiveStream")] bool IsLiveStream = false,
    [property: JsonPropertyName("stationUuid")] string? StationUuid = null);

public sealed record WebRemoteRadioCategoryItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name);

public sealed record WebRemoteRadioStationItem(
    [property: JsonPropertyName("stationUuid")] string StationUuid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tags")] string Tags,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("bitrate")] int Bitrate,
    [property: JsonPropertyName("quality")] string Quality,
    [property: JsonPropertyName("isCustom")] bool IsCustom,
    [property: JsonPropertyName("isFavorite")] bool IsFavorite,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);

public sealed record WebRemotePodcastFeedItem(
    [property: JsonPropertyName("feedId")] string FeedId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subTitle")] string SubTitle,
    [property: JsonPropertyName("rssUrl")] string RssUrl);

public sealed record WebRemotePodcastEpisodeItem(
    [property: JsonPropertyName("episodeKey")] string EpisodeKey,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("meta")] string Meta,
    [property: JsonPropertyName("shortSummary")] string ShortSummary,
    [property: JsonPropertyName("resumePositionMs")] long ResumePositionMs,
    [property: JsonPropertyName("isCompleted")] bool IsCompleted,
    [property: JsonPropertyName("progressBadge")] string ProgressBadge,
    [property: JsonPropertyName("hasSummary")] bool HasSummary,
    [property: JsonPropertyName("isDigesting")] bool IsDigesting,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);

public sealed record WebRemoteBrowseResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("section")] public string Section { get; init; } = "program";
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("statusText")] public string StatusText { get; init; } = string.Empty;
    [JsonPropertyName("isLoading")] public bool IsLoading { get; init; }
    [JsonPropertyName("programTitle")] public string ProgramTitle { get; init; } = string.Empty;
    [JsonPropertyName("programOpening")] public string ProgramOpening { get; init; } = string.Empty;
    [JsonPropertyName("programTracks")] public IReadOnlyList<WebRemoteProgramTrackItem> ProgramTracks { get; init; } = Array.Empty<WebRemoteProgramTrackItem>();
    [JsonPropertyName("libraryTracks")] public IReadOnlyList<WebRemoteTrackItem> LibraryTracks { get; init; } = Array.Empty<WebRemoteTrackItem>();
    [JsonPropertyName("songs")] public IReadOnlyList<WebRemoteTrackItem> FavoriteSongs { get; init; } = Array.Empty<WebRemoteTrackItem>();
    [JsonPropertyName("stations")] public IReadOnlyList<WebRemoteTrackItem> FavoriteStations { get; init; } = Array.Empty<WebRemoteTrackItem>();
    [JsonPropertyName("radioCategories")] public IReadOnlyList<WebRemoteRadioCategoryItem> RadioCategories { get; init; } = Array.Empty<WebRemoteRadioCategoryItem>();
    [JsonPropertyName("selectedRadioCategory")] public string SelectedRadioCategory { get; init; } = string.Empty;
    [JsonPropertyName("radioStations")] public IReadOnlyList<WebRemoteRadioStationItem> RadioStations { get; init; } = Array.Empty<WebRemoteRadioStationItem>();
    [JsonPropertyName("podcastFeeds")] public IReadOnlyList<WebRemotePodcastFeedItem> PodcastFeeds { get; init; } = Array.Empty<WebRemotePodcastFeedItem>();
    [JsonPropertyName("selectedPodcastFeedUrl")] public string SelectedPodcastFeedUrl { get; init; } = string.Empty;
    [JsonPropertyName("podcastEpisodes")] public IReadOnlyList<WebRemotePodcastEpisodeItem> PodcastEpisodes { get; init; } = Array.Empty<WebRemotePodcastEpisodeItem>();
}

public sealed record WebRemoteCommand
{
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("value")] public double? Value { get; init; }
    [JsonPropertyName("intValue")] public int? IntValue { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
}

public sealed record WebRemoteCommandResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string? Message = null)
{
    public static WebRemoteCommandResult Success(string? message = null) => new(true, message);
    public static WebRemoteCommandResult Fail(string message) => new(false, message);
}
