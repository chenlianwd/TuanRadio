using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 播客 RSS 解析结果。
/// </summary>
public sealed record PodcastFeedParseResult(
    bool IsSuccess,
    PodcastFeed? Feed,
    IReadOnlyList<PodcastEpisode> Episodes,
    string? ErrorMessage = null);

/// <summary>
/// 播客 RSS / Atom 安全抓取与解析服务。
/// </summary>
public interface IPodcastRssService
{
    Task<PodcastFeedParseResult> FetchFeedAsync(
        string rssUrl,
        bool forceRefresh = false,
        CancellationToken ct = default);
}
