using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播客单集播放断点记录。
/// </summary>
public sealed record PodcastEpisodeProgress(
    long PositionMs,
    long DurationMs,
    bool IsCompleted,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 播客订阅源与单集断点续播持久化契约。
/// </summary>
public interface IPodcastStorage
{
    Task<IReadOnlyList<PodcastFeed>> LoadFeedsAsync(CancellationToken ct = default);
    Task<bool> AddOrUpdateFeedAsync(PodcastFeed feed, CancellationToken ct = default);
    Task<bool> RemoveFeedAsync(string rssUrl, CancellationToken ct = default);
    Task<PodcastEpisodeProgress?> GetEpisodeProgressAsync(string episodeKey, CancellationToken ct = default);
    Task UpdateEpisodeProgressAsync(
        string episodeKey,
        long positionMs,
        long durationMs,
        bool isCompleted,
        bool flushImmediately = false,
        CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
    void FlushBlocking() => FlushAsync().GetAwaiter().GetResult();
}
