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

    /// <summary>当前设置的会话级氛围偏好（未设置时为 null）。</summary>
    string? CurrentMoodBias { get; }

    /// <summary>设置会话级氛围偏好（如 calm/energetic），影响后续节目单的意图检测与搜索词生成。传 null 清除。</summary>
    string? SetMoodBias(string? mood);

    /// <summary>当前电台场景模式（由场景服务同步，保障专注/伴眠过滤与选曲约束独立于临时氛围微调）。</summary>
    RadioSceneMode CurrentSceneMode { get; }

    /// <summary>同步当前电台场景模式并刷新节目单缓存。</summary>
    void UpdateSceneMode(RadioSceneMode mode);
}
