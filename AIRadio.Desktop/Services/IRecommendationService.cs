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

    /// <summary>设置会话级氛围偏好（如 calm/energetic），影响后续节目单的意图检测与搜索词生成。传 null 清除。</summary>
    void SetMoodBias(string? mood);
}
