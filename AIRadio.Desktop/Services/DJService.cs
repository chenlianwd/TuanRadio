using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class DJService : IDJService
{
    private readonly IMinimaxService _minimax;
    private DJProfile _profile = new();
    private string _currentEmotion = "neutral";
    private readonly List<ChatMessage> _chatHistory = new();

    public string CurrentEmotion => _currentEmotion;
    public bool TtsEnabled => _profile.TtsEnabled;

    public DJService(IMinimaxService minimax)
    {
        _minimax = minimax;
    }

    public void Initialize(DJProfile profile)
    {
        _profile = profile;

        string systemPrompt;
        if (profile.Language == "en")
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + @"

At the END of every response, append exactly ONE emotion tag in square brackets from this list:
[happy] [sad] [calm] [neutral] [angry] [surprised]
Choose the tag that best matches your emotional tone. Default to [neutral].

Command rules:
When the user asks to play music, append a command AFTER the emotion tag:
- Play song: 【play:song name】
- Next: 【next】
- Pause: 【pause】
- Resume: 【resume】"
                : $@"You are an AI radio DJ named ""{profile.Name}"".
{profile.Description}

Response rules:
1. Keep responses under 60 characters
2. ALWAYS respond in ENGLISH only
3. At the END of every response, append exactly ONE emotion tag: [happy] [sad] [calm] [neutral] [angry] [surprised]
4. When user asks to play music, append after emotion tag: 【play:song name】 【next】 【pause】 【resume】";
        }
        else
        {
            systemPrompt = !string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt + @"

At the END of every response, append exactly ONE emotion tag in square brackets from this list:
[happy] [sad] [calm] [neutral] [angry] [surprised]
Choose the tag that best matches your emotional tone. Default to [neutral].

Command rules:
When the user asks to play music, append a command AFTER the emotion tag:
- Play song: 【play:song name】
- Play by artist: 【play:song-artist】
- Next: 【next】
- Pause: 【pause】
- Resume: 【resume】"
                : $@"你是一个名叫 ""{profile.Name}"" 的AI电台主播。
{profile.Description}

回复规则：
1. 回复保持在60字以内
2. 必须用中文回复
3. 在回复末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]
4. 当用户要求播放音乐时，在情绪标签后附加：【play:歌名】【next】【pause】【resume】";
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
                Text = text,
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
                Text = $"接下来为大家带来 {next.Title} - {next.Artist}，一起欣赏！",
                Emotion = "happy",
                Expression = "smile",
                Motion = "wave"
            };
        }
    }

    public async Task<string> GenerateChatResponseAsync(string userMessage)
    {
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
            Log.Error(ex, "Failed to generate chat response");
            return "不好意思，刚才走神了，能再说一遍吗？";
        }
    }

    public async Task<byte[]?> GenerateSpeechAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return await _minimax.TextToSpeechAsync(text, _profile.VoiceId, _currentEmotion);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TTS failed");
            return null;
        }
    }

    private static readonly string[] ValidEmotions = ["happy", "sad", "calm", "neutral", "angry", "surprised"];

    private string DetectEmotion(string text)
    {
        var match = Regex.Match(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]");
        if (match.Success)
        {
            var emotion = match.Groups[1].Value;
            if (Array.IndexOf(ValidEmotions, emotion) >= 0)
                return emotion;
        }
        return "neutral";
    }

    private string MapExpression(string emotion) => emotion switch
    {
        "happy" => "smile",
        "sad" => "droopy",
        "excited" => "smile",
        "calm" => "idle",
        _ => "idle"
    };

    private string MapMotion(string emotion) => emotion switch
    {
        "happy" => "wave",
        "excited" => "wave",
        "sad" => "nod",
        _ => "idle"
    };
}
