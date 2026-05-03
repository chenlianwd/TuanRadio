using System;

namespace AIRadio.Desktop.Models;

public class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public byte[]? CoverArt { get; set; }

    public static Track FromFile(string filePath)
    {
        var track = new Track { FilePath = filePath };
        try
        {
            var file = TagLib.File.Create(filePath);
            track.Title = string.IsNullOrWhiteSpace(file.Tag.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(filePath)
                : file.Tag.Title;
            track.Artist = file.Tag.FirstPerformer ?? "未知艺术家";
            track.Album = file.Tag.Album ?? "未知专辑";
            track.Duration = file.Properties.Duration;

            if (file.Tag.Pictures?.Length > 0)
            {
                track.CoverArt = file.Tag.Pictures[0].Data.Data;
            }
        }
        catch
        {
            track.Title = System.IO.Path.GetFileNameWithoutExtension(filePath);
        }
        return track;
    }

    public override string ToString() => $"{Title} - {Artist}";
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public enum MessageRole
{
    System,
    User,
    Assistant
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    Ended
}

public class DJProfile
{
    public string Name { get; set; } = "小音";
    public string Description { get; set; } = "活泼开朗的电台主播，熟悉各类音乐风格";
    public string AvatarModelPath { get; set; } = "assets/models/Hiyori";
    public string DefaultExpression { get; set; } = "idle";
    public string VoiceId { get; set; } = "male-qn-qingse";
    public string SystemPrompt { get; set; } = string.Empty;
}

public class DJScript
{
    public string Text { get; set; } = string.Empty;
    public string Emotion { get; set; } = "neutral";
    public string Expression { get; set; } = "idle";
    public string Motion { get; set; } = "idle";
    public TimeSpan Duration { get; set; }
}

public class RadioSettings
{
    public string MinimaxApiKey { get; set; } = string.Empty;
    public DJProfile DjProfile { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public UISettings UI { get; set; } = new();
}

public class PlaybackSettings
{
    public double CrossfadeDuration { get; set; } = 2.0;
    public bool AutoTransition { get; set; } = true;
    public string TransitionMode { get; set; } = "track_start";
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string RepeatMode { get; set; } = "list";
}

public class AudioSettings
{
    public bool SpectrumEnabled { get; set; } = true;
    public int SpectrumUpdateRate { get; set; } = 30;
}

public class UISettings
{
    public string Theme { get; set; } = "dark";
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 700;
    public double SplitterRatio { get; set; } = 0.65;
}
