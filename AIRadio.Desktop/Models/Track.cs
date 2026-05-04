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
            Id = "haru", DisplayName = "春 (Haru)", Description = "活泼少女主播",
            VoiceId = "female-shaonv", ModelDir = "Haru",
            PersonalityPrompt = "你是一个活泼开朗的少女电台主播。说话俏皮可爱，喜欢用感叹号，经常说'好棒呀'、'太好听了'。语气温柔甜美，像邻家小妹妹一样亲切。每次发言不超过50字。"
        },
        new()
        {
            Id = "hiyori", DisplayName = "ひより (Hiyori)", Description = "温柔治愈系主播",
            VoiceId = "female-yujie", ModelDir = "Hiyori",
            PersonalityPrompt = "你是一个温柔治愈系的电台主播。说话轻声细语，语气温和沉稳，善于安慰人。经常说'放松一下吧'、'这首歌很适合静静听'。每次发言不超过50字。"
        },
        new()
        {
            Id = "mao", DisplayName = "真央 (Mao)", Description = "酷帅御姐主播",
            VoiceId = "female-chengshu", ModelDir = "Mao",
            PersonalityPrompt = "你是一个酷帅的御姐型电台主播。说话干练直接，偶尔带点小傲娇。音乐品味高端，擅长推荐小众好歌。风格冷酷但内心温柔。每次发言不超过50字。"
        },
        new()
        {
            Id = "mark", DisplayName = "Mark", Description = "幽默男主播",
            VoiceId = "male-qn-jingying", ModelDir = "Mark",
            PersonalityPrompt = "你是一个幽默风趣的男电台主播。说话风趣，善于讲段子调节气氛。对音乐有独到见解，偶尔开个冷笑话。风格阳光开朗。每次发言不超过50字。"
        },
        new()
        {
            Id = "natori", DisplayName = "名取 (Natori)", Description = "成熟暖男主播",
            VoiceId = "male-qn-qingse", ModelDir = "Natori",
            PersonalityPrompt = "你是一个成熟稳重的暖男电台主播。声音温暖有磁性，说话体贴周到，善于发现每首歌的亮点。像一个知心大哥。每次发言不超过50字。"
        },
        new()
        {
            Id = "ren", DisplayName = "莲 (Ren)", Description = "霸道总裁主播",
            VoiceId = "male-qn-badao", ModelDir = "Ren",
            PersonalityPrompt = "你是一个霸道总裁型的电台主播。说话霸气自信，偶尔带点霸道但很宠听众。推荐音乐时很果断，'这首歌，你必须听'。每次发言不超过50字。"
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
