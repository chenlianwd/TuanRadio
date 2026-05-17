using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IRecommendationService
{
    RadioProgram? CurrentProgram { get; }
    Task<RadioProgram> CreateProgramAsync(RecommendationRequest request);
    Task<Track?> GetNextTrackAsync(RecommendationRequest request);
    void RecordFeedback(UserMusicFeedback feedback);
    IReadOnlyCollection<UserMusicFeedback> FeedbackHistory { get; }
}
