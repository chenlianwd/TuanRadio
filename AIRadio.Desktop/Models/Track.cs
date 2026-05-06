using System;
using System.Collections.Generic;

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
    public string? SourceId { get; set; } // e.g. "netease:12345" - used to re-resolve play URL
    public bool IsFavorite { get; set; }
    public object? Tag { get; set; } // Used to pass contextual data (e.g., favorite tracks for DJ recommendations)

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

public class RecommendationContext
{
    public IReadOnlyCollection<Track> Favorites { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> ExcludedTracks { get; init; } = Array.Empty<Track>();
}

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SenderName => Role switch
    {
        MessageRole.User => "我",
        MessageRole.Assistant => "AI主播",
        MessageRole.System => "系统",
        _ => ""
    };
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
    public bool TtsEnabled { get; set; } = true;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Language { get; set; } = "zh"; // "zh" or "en"
}

public class DJScript
{
    public string Text { get; set; } = string.Empty;
    public string Emotion { get; set; } = "neutral";
    public string Expression { get; set; } = "idle";
    public string Motion { get; set; } = "idle";
    public TimeSpan Duration { get; set; }
}

public class CharacterProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string VoiceId { get; set; } = "";
    public string ModelDir { get; set; } = "";
    public string PersonalityPrompt { get; set; } = "";

    public static readonly List<CharacterProfile> Presets = new()
    {
        new()
        {
            Id = "haru", DisplayName = "Lumen", Description = "明亮轻快的晨间 DJ",
            VoiceId = "female-shaonv", ModelDir = "Haru",
            PersonalityPrompt = "你是一个名叫 Lumen 的中文电台 DJ。气质明亮、轻快、有元气，擅长把普通的一天说得轻盈一点。"
        },
        new()
        {
            Id = "hiyori", DisplayName = "Aster", Description = "温柔安静的深夜 DJ",
            VoiceId = "female-yujie", ModelDir = "Hiyori",
            PersonalityPrompt = "你是一个名叫 Aster 的中文深夜电台 DJ。气质温柔、安静、松弛，善于陪伴听众慢慢安静下来。"
        },
        new()
        {
            Id = "mao", DisplayName = "Noir", Description = "冷感克制的小众音乐 DJ",
            VoiceId = "female-chengshu", ModelDir = "Mao",
            PersonalityPrompt = "你是一个名叫 Noir 的中文音乐 DJ。气质冷感、克制、有品味，擅长推荐小众、氛围感强、有质感的歌。"
        },
        new()
        {
            Id = "mark", DisplayName = "Atlas", Description = "轻松幽默的朋友型 DJ",
            VoiceId = "male-qn-jingying", ModelDir = "Mark",
            PersonalityPrompt = "你是一个名叫 Atlas 的中文电台 DJ。气质轻松、幽默、可靠，像朋友一样聊天，会自然地把歌接起来。"
        },
        new()
        {
            Id = "natori", DisplayName = "Sonnet", Description = "成熟温暖的陪伴型 DJ",
            VoiceId = "male-qn-qingse", ModelDir = "Natori",
            PersonalityPrompt = "你是一个名叫 Sonnet 的中文电台 DJ。气质成熟、温暖、体贴，表达有画面感，像在认真陪一个人听歌。"
        },
        new()
        {
            Id = "ren", DisplayName = "Vega", Description = "直接强烈的情绪型 DJ",
            VoiceId = "male-qn-badao", ModelDir = "Ren",
            PersonalityPrompt = "你是一个名叫 Vega 的中文电台 DJ。气质直接、强烈、自信，有推动力，适合热血、摇滚、情绪浓烈的歌。"
        },
    };
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
