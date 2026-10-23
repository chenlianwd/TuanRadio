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
    /// <summary>撤销指定音乐身份的会话 Dislike，与长期画像黑名单移除同步；其它反馈保留。</summary>
    bool RemoveDislikeFeedback(string? title, string? artist);
    IReadOnlyCollection<UserMusicFeedback> FeedbackHistory { get; }
    IReadOnlyCollection<Track> RecentlyPlayed { get; }
    void RecordPlayedTrack(Track track);

    /// <summary>设置会话级氛围偏好（如 calm/energetic），影响后续节目单的意图检测与搜索词生成。传 null 清除。</summary>
    string? SetMoodBias(string? mood);
}
