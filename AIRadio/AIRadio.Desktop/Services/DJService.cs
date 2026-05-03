using System;
using System.Collections.Generic;
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

    public DJService(IMinimaxService minimax)
    {
        _minimax = minimax;
    }

    public void Initialize(DJProfile profile)
    {
        _profile = profile;
        var systemPrompt = $@"你是一个电台AI主播，名字叫""{profile.Name}""。

性格特点：
- 活泼开朗，善于与听众互动
- 说话自然流畅，像朋友聊天
- 熟悉流行音乐，能准确介绍歌曲
- 语气亲切，有时会开玩笑

发言规则：
1. 每次发言不超过60字（短小精悍）
2. 介绍歌曲时包含：歌名、歌手、专辑
3. 根据歌曲类型调整语气（摇滚热烈，抒情温柔）
4. 适当加入口头禅（如""好听的来啦""、""这首歌我超喜欢""）";

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

    private string DetectEmotion(string text)
    {
        if (text.Contains("超") || text.Contains("太棒") || text.Contains("期待") || text.Contains("嗨"))
            return "excited";
        if (text.Contains("温柔") || text.Contains("安静") || text.Contains("轻轻"))
            return "calm";
        if (text.Contains("感动") || text.Contains("怀念") || text.Contains("曾经"))
            return "sad";
        if (text.Contains("开心") || text.Contains("喜欢") || text.Contains("好听"))
            return "happy";
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
