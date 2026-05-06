using System;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IDJService
{
    void Initialize(DJProfile profile);
    Task<DJScript> GenerateTrackIntroductionAsync(Track current, Track next);
    Task<Track?> RecommendNextTrackAsync(Track? current);
    Task<string> GenerateChatResponseAsync(string userMessage);
    Task<byte[]?> GenerateSpeechAsync(string text);
    string CurrentEmotion { get; }
    bool TtsEnabled { get; }
    ApiFailureInfo? LastFailure { get; }
}

public interface ILive2DViewer
{
    void SetExpression(string expressionName);
    void PlayMotion(string motionName);
    void UpdateLipSync(float[] spectrumData);
    IObservable<string> MotionFinished { get; }
}
