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
    private readonly IMinimaxService _minimax;
    private readonly IMusicSearchService? _musicSearch;
    private DJProfile _profile = new();
    private string _currentEmotion = "neutral";
    private readonly List<ChatMessage> _chatHistory = new();

    public string CurrentEmotion => _currentEmotion;
    public bool TtsEnabled => _profile.TtsEnabled;
    public ApiFailureInfo? LastFailure { get; private set; }

    public DJService(IMinimaxService minimax, IMusicSearchService? musicSearch = null)
    {
        _minimax = minimax;
        _musicSearch = musicSearch;
    }

    public void Initialize(DJProfile profile)
    {
        _profile = profile;

        var commandRules = @"

At the END of every response, append exactly ONE emotion tag in square brackets from:
[happy] [sad] [calm] [neutral] [angry] [surprised]

Command rules:
When the user asks to control music, append ONE command after the emotion tag:
- Play song: 【play:song name】
- Next: 【next】
- Pause: 【pause】
- Resume: 【resume】";

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
4. For music control append: 【play:song name】 【next】 【pause】 【resume】";
        }
        else
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + commandRules + "\n必须只用中文回复。"
                : $@"你是一个名叫 ""{profile.Name}"" 的 AI 电台主播。
{profile.Description}

回复规则：
1. 必须只用中文回复。
2. 像真实深夜电台 DJ 一样自然说话，可以有铺垫、画面感和情绪。
3. 末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。
4. 当用户要求播放音乐时，在情绪标签后附加：【play:歌名】【next】【pause】【resume】。";
        }

        _chatHistory.Clear();
        _chatHistory.Add(new ChatMessage { Role = MessageRole.System, Content = systemPrompt });
    }

    public async Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next)
    {
        try
        {
            var text = await _minimax.GenerateTrackIntroductionAsync(current, next);
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
                Text = $"接下来为你带来 {next.Title} - {next.Artist}，一起听听这段情绪。",
                Emotion = "happy",
                Expression = "smile",
                Motion = "wave"
            };
        }
    }

    public async Task<string> GenerateChatResponseAsync(string userMessage)
    {
        LastFailure = null;
        try
        {
            var response = await _minimax.ChatAsync(userMessage, _chatHistory);
            _chatHistory.Add(new ChatMessage { Role = MessageRole.User, Content = userMessage });
            _chatHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
            _currentEmotion = DetectEmotion(response);
            return response;
        }
        catch (Exception ex)
        {
            LastFailure = ApiFailureInfo.FromException(ex);
            Log.Error(ex, "Failed to generate chat response");
            return _profile.Language == "en"
                ? "Sorry, the signal drifted. Say that again? [calm]"
                : "不好意思，刚才信号飘了一下，可以再说一遍吗？[calm]";
        }
    }

    public async Task<byte[]?> GenerateSpeechAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        LastFailure = null;
        try
        {
            return await _minimax.TextToSpeechAsync(StripControlTags(text), _profile.VoiceId, _currentEmotion);
        }
        catch (Exception ex)
        {
            LastFailure = ApiFailureInfo.FromException(ex);
            Log.Warning(ex, "TTS failed");
            return null;
        }
    }

    private static readonly string[] ValidEmotions = ["happy", "sad", "calm", "neutral", "angry", "surprised"];

    private static string DetectEmotion(string text)
    {
        var match = Regex.Match(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", RegexOptions.IgnoreCase);
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
        cleaned = Regex.Replace(cleaned, @"【(?:play:.+?|next|pause|resume)】", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
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

    public async Task<Track?> RecommendNextTrackAsync(Track? current)
    {
        if (_musicSearch == null) return null;

        try
        {
            // Build context from favorites if available (passed via current parameter's Tag)
            string favoriteContext = "";
            if (current?.Tag is IEnumerable<Track> favorites && favorites.Any())
            {
                var favList = favorites.Take(10).Select(t => $"{t.Title} by {t.Artist}").ToList();
                favoriteContext = $" The user likes these songs: {string.Join(", ", favList)}. ";
            }

            // Generate a music recommendation prompt based on current track and favorites
            var prompt = current != null
                ? $"Based on the song '{current.Title}' by '{current.Artist}', {favoriteContext}recommend ONE similar or related song that would fit well in a radio playlist. Reply with ONLY the song name in Chinese or English (no other text)."
                : "Recommend one popular song for an AI radio station. Reply with ONLY the song name in Chinese or English (no other text).";

            var response = await _minimax.ChatAsync(prompt, new List<ChatMessage>());
            var cleaned = response.Trim();

            // Remove URLs, thinking tags, and any other non-song content
            cleaned = Regex.Replace(cleaned, @"https?://\S+", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\[think\]:.*", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^[\[\(【\(].*?[\]\)】\)]:", "", RegexOptions.IgnoreCase);
            cleaned = cleaned.Trim();

            // Try multiple parsing strategies
            string? title = null;
            string? artist = null;

            // Strategy 1: "Song Name - Artist"
            if (cleaned.Contains(" - "))
            {
                var parts = cleaned.Split(new[] { " - " }, 2, StringSplitOptions.None);
                title = parts[0].Trim();
                artist = parts.Length > 1 ? parts[1].Trim() : null;
            }
            // Strategy 2: "Song Name by Artist"
            else if (cleaned.Contains(" by "))
            {
                var parts = cleaned.Split(new[] { " by " }, 2, StringSplitOptions.None);
                title = parts[0].Trim();
                artist = parts.Length > 1 ? parts[1].Trim() : null;
            }
            // Strategy 3: Just the song name - search with artist context
            else if (cleaned.Length >= 2 && cleaned.Length <= 100)
            {
                title = cleaned;
                artist = current?.Artist; // fallback to current artist
            }

            if (string.IsNullOrWhiteSpace(title)) return null;

            // Search for the recommended song
            var searchQuery = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var results = await _musicSearch.SearchAsync(searchQuery, 5);
            foreach (var item in results)
            {
                var url = await _musicSearch.GetPlayUrlAsync(item.Id);
                if (!string.IsNullOrEmpty(url))
                {
                    Log.Information("DJ recommended: {Title} by {Artist}", item.Title, item.Artist);
                    return item.ToTrack(url);
                }
            }

            Log.Warning("DJ could not find track for recommendation: {Response}", cleaned);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DJ recommendation failed");
            return null;
        }
    }
}
