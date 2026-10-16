using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IDJService
{
    void Initialize(DJProfile profile);
    Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next);
    Task<SongStory> GenerateSongStoryAsync(Track track);
    Task<SongStory> GenerateSongStoryAsync(Track track, CancellationToken cancellationToken)
        => GenerateSongStoryAsync(track);
    Task<Track?> RecommendNextTrackAsync(Track? current);
    Task<string> GenerateChatResponseAsync(string userMessage);
    Task<string> GenerateChatResponseAsync(string userMessage, CancellationToken cancellationToken)
        => GenerateChatResponseAsync(userMessage);
    /// <summary>语音识别结果的同音近音纠错，失败时应返回原文。</summary>
    Task<string> CorrectTranscriptionAsync(string transcript, CancellationToken cancellationToken);
    Task<byte[]?> GenerateSpeechAsync(string text);
    string CurrentEmotion { get; }
    bool TtsEnabled { get; }
    /// <summary>最近一次聊天生成的失败信息（成功即清空）。与 TTS 失败分字段存放：
    /// 后台串场 TTS 与用户聊天并发时，共享单字段会互相覆盖导致聊天侧误报/漏报。</summary>
    ApiFailureInfo? LastChatFailure { get; }
    /// <summary>最近一次语音合成的失败信息（成功即清空），语义同上按流程隔离。</summary>
    ApiFailureInfo? LastTtsFailure { get; }
}
