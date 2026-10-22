using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IDJService
{
    void Initialize(DJProfile profile);
    /// <summary>生成曲目串场词。current 为 null 表示电台刚开播，按"第一首歌"开场。</summary>
    Task<DJScript> GenerateTrackIntroductionAsync(Track? current, Track next);
    /// <summary>更新串场环境感知的天气快照（UI 层天气刷新落定时推送；null 表示当前无天气数据）。</summary>
    void UpdateWeatherSnapshot(WeatherInfo? weather);
    /// <summary>更新聊天链的电台现场快照：当前在播曲目与会话播放历史（换曲在 UI 线程、进入播放态在
    /// 播放器事件线程推送，服务内加锁保证一致；nowPlaying 传 null 表示电台空闲）。
    /// 聊天凭它接住"这首歌 / X 的版本"类指代。</summary>
    void UpdateStationContext(Track? nowPlaying, IReadOnlyList<Track> recentlyPlayed);
    /// <summary>记一条 DJ 现场发言进短期记忆（节目单开场白等"上过广播但不在聊天记录里"的话），
    /// 与串场词共用队列：聊天链凭它记得自己说过什么，串场链凭它防复读。</summary>
    void RememberOnAirLine(string? text);
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
    /// <summary>当前 DJ 实名（人设 DisplayName，随 Initialize 更新），供聊天署名等展示。</summary>
    string DjName { get; }
    /// <summary>最近一次聊天生成的失败信息（成功即清空）。与 TTS 失败分字段存放：
    /// 后台串场 TTS 与用户聊天并发时，共享单字段会互相覆盖导致聊天侧误报/漏报。</summary>
    ApiFailureInfo? LastChatFailure { get; }
    /// <summary>最近一次语音合成的失败信息（成功即清空），语义同上按流程隔离。</summary>
    ApiFailureInfo? LastTtsFailure { get; }
}
