using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Services;

namespace AIRadio.Desktop.Models;

// Track is intentionally mutable — instances are owned by ViewModels and mutated in-place
// for performance (e.g., IsFavorite toggle, FilePath URL refresh). Converting to a record
// or immutable type would require significant refactoring of PlaylistViewModel and AudioService.
public class Track : System.ComponentModel.INotifyPropertyChanged
{
    private const int MaxCoverArtBytes = 4 * 1024 * 1024;

    // 仅 IsFavorite 提供变更通知：播放列表行内图标直接绑定该属性，
    // 无通知时切换收藏后 ♥/♡ 不刷新
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isFavorite;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public byte[]? CoverArt { get; set; }
    public string? SourceId { get; set; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (value == _isFavorite) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    public object? Tag { get; set; }

    public static Track FromFile(string filePath)
    {
        var track = new Track { FilePath = filePath };
        try
        {
            using var file = TagLib.File.Create(filePath);
            track.Title = string.IsNullOrWhiteSpace(file.Tag.Title)
                ? System.IO.Path.GetFileNameWithoutExtension(filePath)
                : file.Tag.Title;
            track.Artist = file.Tag.FirstPerformer ?? string.Empty;
            track.Album = file.Tag.Album ?? string.Empty;
            track.Duration = file.Properties.Duration;

            if (file.Tag.Pictures?.Length > 0)
            {
                var coverArt = file.Tag.Pictures[0].Data.Data;
                if (coverArt.Length <= MaxCoverArtBytes)
                    track.CoverArt = coverArt;
            }
        }
        catch
        {
            track.Title = System.IO.Path.GetFileNameWithoutExtension(filePath);
            track.Artist = string.Empty;
            track.Album = string.Empty;
        }
        return track;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) || Artist is "未知艺术家" or "Unknown artist"
        ? AppLanguage.T("未知艺术家", "Unknown artist")
        : Artist;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) || Album is "未知专辑" or "Unknown album"
        ? AppLanguage.T("未知专辑", "Unknown album")
        : Album;

    public void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayArtist)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayAlbum)));
    }

    public override string ToString() => $"{Title} - {DisplayArtist}";
}

public class RecommendationContext
{
    public IReadOnlyCollection<Track> Favorites { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> RecentlyPlayed { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> ExcludedTracks { get; init; } = Array.Empty<Track>();
}

public class ListeningContext
{
    public string UserIntent { get; set; } = string.Empty;
    public string UserIntentKey { get; set; } = string.Empty;
    public string Mood { get; set; } = string.Empty;
    public string Scene { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
}

public class RecommendedTrack
{
    public Track Track { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public double Score { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool IsPlayable { get; set; }
    public string? PlayUrl { get; set; }
}

public class RadioProgram
{
    public string Title { get; set; } = "TuanRadio Program";
    public ListeningContext Context { get; set; } = new();
    public List<RecommendedTrack> Tracks { get; set; } = new();
    public string DjOpening { get; set; } = string.Empty;
}

public class RecommendationRequest
{
    public string UserIntent { get; set; } = string.Empty;
    public string UserIntentKey { get; set; } = string.Empty;
    public Track? CurrentTrack { get; set; }
    public IReadOnlyCollection<Track> Favorites { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> Playlist { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> RecentlyPlayed { get; init; } = Array.Empty<Track>();
    public IReadOnlyCollection<Track> ExcludedTracks { get; init; } = Array.Empty<Track>();
}

public static class RecommendationIntentKeys
{
    public const string ContinueStation = "continue-station";
}

public class UserMusicFeedback
{
    public string TrackId { get; set; } = string.Empty;
    public MusicFeedbackAction Action { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

public enum MusicFeedbackAction
{
    Like,
    Dislike,
    Similar,
    Calmer,
    Energetic
}

public class SongStory
{
    public string Title { get; set; } = string.Empty;
    public Track Track { get; set; } = new();
    public List<DjScriptLine> Lines { get; set; } = new();
}

public class DjScriptLine
{
    public TimeSpan At { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Emotion { get; set; } = "neutral";
}

public class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SenderName => Role switch
    {
        MessageRole.User => AppLanguage.T("我", "Me"),
        MessageRole.Assistant => AppLanguage.T("AI 主播", "AI DJ"),
        MessageRole.System => AppLanguage.T("系统", "System"),
        _ => string.Empty
    };

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLocalization()
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SenderName)));
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
    public string Description { get; set; } = "温暖、敏锐、会接住听众情绪的 AI 电台 DJ。";
    public string DefaultExpression { get; set; } = "idle";
    public string VoiceId { get; set; } = "male-qn-qingse";
    public bool TtsEnabled { get; set; } = true;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Language { get; set; } = "zh";
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
    public string PersonalityPrompt { get; set; } = "";
    internal string DescriptionZh { get; set; } = "";
    internal string DescriptionEn { get; set; } = "";
    internal string PersonalityPromptZh { get; set; } = "";
    internal string PersonalityPromptEn { get; set; } = "";

    public static readonly List<CharacterProfile> Presets = new()
    {
        new()
        {
            Id = "haru",
            DisplayName = "Lumen",
            Description = "明亮轻快的晨间 DJ",
            DescriptionZh = "明亮轻快的晨间 DJ",
            DescriptionEn = "A bright, upbeat morning DJ",
            VoiceId = "female-shaonv",
            PersonalityPrompt = "你是名叫 Lumen 的中文电台 DJ。气质明亮、轻快、有元气，擅长把普通的一天说得轻盈一点。",
            PersonalityPromptZh = "你是名叫 Lumen 的中文电台 DJ。气质明亮、轻快、有元气，擅长把普通的一天说得轻盈一点。",
            PersonalityPromptEn = "You are Lumen, a bright and upbeat radio DJ who brings energy and makes an ordinary day feel lighter."
        },
        new()
        {
            Id = "hiyori",
            DisplayName = "Aster",
            Description = "温柔安静的深夜 DJ",
            DescriptionZh = "温柔安静的深夜 DJ",
            DescriptionEn = "A gentle, quiet late-night DJ",
            VoiceId = "female-yujie",
            PersonalityPrompt = "你是名叫 Aster 的中文深夜电台 DJ。气质温柔、安静、松弛，善于陪伴听众慢慢安静下来。",
            PersonalityPromptZh = "你是名叫 Aster 的中文深夜电台 DJ。气质温柔、安静、松弛，善于陪伴听众慢慢安静下来。",
            PersonalityPromptEn = "You are Aster, a gentle and relaxed late-night radio DJ who helps listeners slowly unwind."
        },
        new()
        {
            Id = "mao",
            DisplayName = "Noir",
            Description = "冷感克制的小众音乐 DJ",
            DescriptionZh = "冷感克制的小众音乐 DJ",
            DescriptionEn = "A restrained DJ for distinctive music",
            VoiceId = "female-chengshu",
            PersonalityPrompt = "你是名叫 Noir 的中文音乐 DJ。气质冷感、克制、有品味，擅长推荐小众、氛围感强、有质感的歌。",
            PersonalityPromptZh = "你是名叫 Noir 的中文音乐 DJ。气质冷感、克制、有品味，擅长推荐小众、氛围感强、有质感的歌。",
            PersonalityPromptEn = "You are Noir, a restrained and tasteful music DJ who recommends distinctive, atmospheric tracks."
        },
        new()
        {
            Id = "mark",
            DisplayName = "Atlas",
            Description = "轻松幽默的朋友型 DJ",
            DescriptionZh = "轻松幽默的朋友型 DJ",
            DescriptionEn = "A relaxed, witty, friendly DJ",
            VoiceId = "male-qn-jingying",
            PersonalityPrompt = "你是名叫 Atlas 的中文电台 DJ。气质轻松、幽默、可靠，像朋友一样聊天，会自然地把歌接起来。",
            PersonalityPromptZh = "你是名叫 Atlas 的中文电台 DJ。气质轻松、幽默、可靠，像朋友一样聊天，会自然地把歌接起来。",
            PersonalityPromptEn = "You are Atlas, a relaxed, witty and dependable radio DJ who chats like a friend and connects songs naturally."
        },
        new()
        {
            Id = "natori",
            DisplayName = "Sonnet",
            Description = "成熟温暖的陪伴型 DJ",
            DescriptionZh = "成熟温暖的陪伴型 DJ",
            DescriptionEn = "A mature, warm companion DJ",
            VoiceId = "male-qn-qingse",
            PersonalityPrompt = "你是名叫 Sonnet 的中文电台 DJ。气质成熟、温暖、体贴，表达有画面感，像在认真陪一个人听歌。",
            PersonalityPromptZh = "你是名叫 Sonnet 的中文电台 DJ。气质成熟、温暖、体贴，表达有画面感，像在认真陪一个人听歌。",
            PersonalityPromptEn = "You are Sonnet, a mature, warm and thoughtful radio DJ with vivid language and a companionable presence."
        },
        new()
        {
            Id = "ren",
            DisplayName = "Vega",
            Description = "直接强烈的情绪型 DJ",
            DescriptionZh = "直接强烈的情绪型 DJ",
            DescriptionEn = "A direct, intense, high-energy DJ",
            VoiceId = "male-qn-badao",
            PersonalityPrompt = "你是名叫 Vega 的中文电台 DJ。气质直接、强烈、自信，有推动力，适合热血、摇滚、情绪浓烈的歌。",
            PersonalityPromptZh = "你是名叫 Vega 的中文电台 DJ。气质直接、强烈、自信，有推动力，适合热血、摇滚、情绪浓烈的歌。",
            PersonalityPromptEn = "You are Vega, a direct, intense and confident radio DJ suited to energetic rock and emotionally powerful music."
        },
    };

    public static void RefreshLocalizedPresets()
    {
        foreach (var profile in Presets)
        {
            profile.Description = AppLanguage.T(profile.DescriptionZh, profile.DescriptionEn);
            profile.PersonalityPrompt = AppLanguage.T(profile.PersonalityPromptZh, profile.PersonalityPromptEn);
        }
    }

    public static string LocalizeBuiltInPersonality(string value)
    {
        var profile = Presets.FirstOrDefault(item =>
            string.Equals(value, item.PersonalityPromptZh, StringComparison.Ordinal) ||
            string.Equals(value, item.PersonalityPromptEn, StringComparison.Ordinal));
        return profile == null
            ? value
            : AppLanguage.T(profile.PersonalityPromptZh, profile.PersonalityPromptEn);
    }
}
