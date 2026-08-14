using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class DJService : IDJService
{
    private readonly ILLMService _llm;
    private readonly ITtsService? _tts;
    private readonly IMusicSearchService? _musicSearch;
    private DJProfile _profile = new();
    private string _currentEmotion = "neutral";
    private readonly List<ChatMessage> _chatHistory = new();

    public string CurrentEmotion => _currentEmotion;
    public bool TtsEnabled => _profile.TtsEnabled;
    public ApiFailureInfo? LastFailure { get; private set; }

    public DJService(ILLMService llm, ITtsService? tts = null, IMusicSearchService? musicSearch = null)
    {
        _llm = llm;
        _tts = tts;
        _musicSearch = musicSearch;
    }

    public void Initialize(DJProfile profile)
    {
        _profile = profile;

        var commandRules = """

At the END of every response, append exactly ONE emotion tag in square brackets from:
[happy] [sad] [calm] [neutral] [angry] [surprised]

Command rules:
When the user asks to control music, append one JSON control block after the emotion tag:
<cmd>{"action":"play","query":"song name"}</cmd>
<cmd>{"action":"next"}</cmd>
<cmd>{"action":"pause"}</cmd>
<cmd>{"action":"resume"}</cmd>
<cmd>{"action":"recommend_more"}</cmd>
<cmd>{"action":"change_mood","mood":"calmer"}</cmd>
The spoken text must not mention the control block.
""";

        string systemPrompt;
        if (profile.Language == "en")
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + commandRules + "\nAlways respond in English only."
                : $@"You are an AI radio DJ named ""{profile.Name}"".
{profile.Description}

Response rules:
1. Always respond in English only.
2. Speak naturally like a real late-night radio DJ.
3. End with one emotion tag: [happy] [sad] [calm] [neutral] [angry] [surprised].
4. Use a JSON control block only when music playback should change.";
        }
        else
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + commandRules + "\n必须只用中文回复。"
                : $@"你是名叫 ""{profile.Name}"" 的 AI 电台 DJ。
{profile.Description}

回复规则：
1. 必须只用中文回复。
2. 像真实深夜电台 DJ 一样自然说话，可以有铺垫、画面感和情绪。
3. 末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。
4. 只有需要控制音乐时，才在情绪标签后追加 JSON 控制块。";
        }

        _chatHistory.Clear();
        _chatHistory.Add(new ChatMessage { Role = MessageRole.System, Content = systemPrompt });
    }

    public async Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next)
    {
        try
        {
            var text = await _llm.GenerateTrackIntroductionAsync(current, next);
            var emotion = DetectEmotion(text);

            return new DJScript
            {
                Text = StripControlTags(text),
                Emotion = emotion,
                Expression = MapExpression(emotion),
                Motion = MapMotion(emotion)
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate track introduction");
            return new DJScript
            {
                Text = $"接下来为你带来《{next.Title}》 - {next.Artist}，一起听听这段情绪。",
                Emotion = "happy",
                Expression = "smile",
                Motion = "wave"
            };
        }
    }

    public async Task<SongStory> GenerateSongStoryAsync(Track track)
    {
        try
        {
            var prompt = $"歌曲《{track.Title}》- {track.Artist}。用 3-5 句话给电台听众讲讲这首歌的背景、风格或趣闻，亲切口语化，每句独占一行。";
            var text = await _llm.ChatAsync(prompt, new List<ChatMessage>());
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => t.Trim())
                             .Where(t => t.Length > 0)
                             .Take(5)
                             .Select(t => new DjScriptLine { Text = t, Emotion = DetectEmotion(t) })
                             .ToList();
            return new SongStory { Title = track.Title, Track = track, Lines = lines };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to generate song story for {Track}", track.Title);
            return new SongStory { Title = track.Title, Track = track, Lines = new() };
        }
    }

    public async Task<string> GenerateChatResponseAsync(string userMessage)
    {
        LastFailure = null;
        try
        {
            var response = await _llm.ChatAsync(userMessage, _chatHistory);
            _chatHistory.Add(new ChatMessage { Role = MessageRole.User, Content = userMessage });
            _chatHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });

            // Trim history to avoid unbounded growth (keep system prompt + last N messages)
            const int maxHistoryMessages = 20;
            while (_chatHistory.Count > maxHistoryMessages + 1)
                _chatHistory.RemoveAt(1);
            _currentEmotion = DetectEmotion(response);
            return response;
        }
        catch (Exception ex)
        {
            LastFailure = ApiFailureInfo.FromException(ex);
            Log.Error(ex, "Failed to generate chat response");
            return _profile.Language == "en"
                ? "Sorry, the signal drifted for a moment. Say that again? [calm]"
                : "不好意思，刚才信号飘了一下，可以再说一遍吗？[calm]";
        }
    }

    public async Task<byte[]?> GenerateSpeechAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        LastFailure = null;
        try
        {
            if (_tts == null) return Array.Empty<byte>();
            return await _tts.SynthesizeAsync(StripControlTags(text), _profile.VoiceId, _currentEmotion);
        }
        catch (Exception ex)
        {
            LastFailure = ApiFailureInfo.FromException(ex);
            Log.Warning(ex, "TTS failed");
            return null;
        }
    }

    public async Task<Track?> RecommendNextTrackAsync(Track? current)
    {
        if (_musicSearch == null) return null;

        try
        {
            var excludedTracks = GetExcludedTracks(current).ToList();
            if (current != null && !excludedTracks.Any(t => IsSameTrack(t, current)))
                excludedTracks.Add(current);

            var prompt = BuildRecommendationPrompt(current);
            var response = await _llm.ChatAsync(prompt, new List<ChatMessage>());
            var cleaned = CleanRecommendationText(response);
            var (title, artist) = ParseRecommendedSong(cleaned);
            if (string.IsNullOrWhiteSpace(title)) return null;

            return await SearchRecommendedTrack(title, artist, excludedTracks);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DJ recommendation failed");
            return null;
        }
    }

    private string BuildRecommendationPrompt(Track? current)
    {
        var favorites = GetFavorites(current);
        var favoriteContext = "";
        if (favorites.Count > 0)
        {
            var favList = favorites.Take(10).Select(t => $"{t.Title} by {t.Artist}").ToList();
            favoriteContext = $" The user likes these songs: {string.Join(", ", favList)}. ";
        }

        return current != null
            ? $"Based on the song '{current.Title}' by '{current.Artist}', {favoriteContext}recommend ONE NEW similar or related song that is NOT already in the user's playlist. Do not recommend '{current.Title}'. Reply with ONLY the song name and artist if known."
            : "Recommend one NEW popular song for an AI radio station. Reply with ONLY the song name and artist if known.";
    }

    private async Task<Track?> SearchRecommendedTrack(string title, string? artist, List<Track> excludedTracks)
    {
        var searchQuery = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var results = await _musicSearch!.SearchAsync(searchQuery, 10);
        foreach (var item in results)
        {
            if (IsExcluded(item, excludedTracks))
            {
                Log.Debug("Skipped already-known DJ recommendation: {Title} by {Artist}", item.Title, item.Artist);
                continue;
            }

            var url = await _musicSearch.GetPlayUrlAsync(item.Id);
            if (!string.IsNullOrEmpty(url))
            {
                Log.Information("DJ recommended: {Title} by {Artist}", item.Title, item.Artist);
                return item.ToTrack(url);
            }
        }

        Log.Warning("DJ could not find track for recommendation: {Title}", title);
        return null;
    }

    private static readonly string[] ValidEmotions = ["happy", "sad", "calm", "neutral", "angry", "surprised"];
    private static readonly string EmotionPattern = $@"\[({string.Join("|", ValidEmotions)})\]";

    private static string DetectEmotion(string text)
    {
        var match = Regex.Match(text, EmotionPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var emotion = match.Groups[1].Value.ToLowerInvariant();
            if (Array.IndexOf(ValidEmotions, emotion) >= 0)
                return emotion;
        }
        return "neutral";
    }

    private static string StripControlTags(string text)
    {
        var cleaned = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<cmd>\s*\{.*?\}\s*</cmd>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"【\s*(?:play:.+?|next|pause|resume)\s*】", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static string CleanRecommendationText(string response)
    {
        var cleaned = response.Trim();
        cleaned = Regex.Replace(cleaned, @"https?://\S+", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\[think\]:.*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"^[\[\(【].*?[\]\)】]:", "", RegexOptions.IgnoreCase);
        return StripControlTags(cleaned).Trim();
    }

    private static (string? Title, string? Artist) ParseRecommendedSong(string cleaned)
    {
        if (cleaned.Contains(" - ", StringComparison.Ordinal))
        {
            var parts = cleaned.Split(new[] { " - " }, 2, StringSplitOptions.None);
            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null);
        }

        if (cleaned.Contains(" by ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = Regex.Split(cleaned, @"\s+by\s+", RegexOptions.IgnoreCase);
            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null);
        }

        return cleaned.Length is >= 2 and <= 100 ? (cleaned, null) : (null, null);
    }

    private static string MapExpression(string emotion) => emotion switch
    {
        "happy" => "smile",
        "surprised" => "smile",
        "sad" => "droopy",
        "angry" => "droopy",
        _ => "idle"
    };

    private static string MapMotion(string emotion) => emotion switch
    {
        "happy" => "wave",
        "surprised" => "wave",
        "sad" => "nod",
        "angry" => "nod",
        _ => "idle"
    };

    private static IReadOnlyCollection<Track> GetFavorites(Track? current)
    {
        if (current?.Tag is RecommendationContext context)
            return context.Favorites;
        if (current?.Tag is IEnumerable<Track> favorites)
            return favorites.ToList();
        return Array.Empty<Track>();
    }

    private static IEnumerable<Track> GetExcludedTracks(Track? current)
    {
        if (current?.Tag is RecommendationContext context)
            return context.ExcludedTracks;
        return Array.Empty<Track>();
    }

    private static bool IsExcluded(OnlineTrack candidate, IEnumerable<Track> excludedTracks)
    {
        return excludedTracks.Any(track =>
            IsSameSource(track.SourceId, candidate.Id) ||
            IsSameMusicIdentity(track.Title, track.Artist, candidate.Title, candidate.Artist));
    }

    private static bool IsSameTrack(Track left, Track right)
    {
        return IsSameSource(left.SourceId, right.SourceId) ||
               IsSameMusicIdentity(left.Title, left.Artist, right.Title, right.Artist) ||
               (!string.IsNullOrWhiteSpace(left.FilePath) && left.FilePath == right.FilePath);
    }

    private static bool IsSameSource(string? left, string? right)
        => MusicIdentity.IsSameSource(left, right);

    private static bool IsSameMusicIdentity(string titleA, string artistA, string titleB, string artistB)
        => MusicIdentity.IsSameMusicIdentity(titleA, artistA, titleB, artistB);

}
