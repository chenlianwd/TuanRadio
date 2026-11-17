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
    [JsonPropertyName("serverTimeUtcMs")] public long ServerTimeUtcMs { get; init; }
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
