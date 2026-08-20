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
    Task<byte[]?> GenerateSpeechAsync(string text);
    string CurrentEmotion { get; }
    bool TtsEnabled { get; }
    ApiFailureInfo? LastFailure { get; }
}
