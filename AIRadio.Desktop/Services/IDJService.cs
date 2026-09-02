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
    ApiFailureInfo? LastFailure { get; }
}
