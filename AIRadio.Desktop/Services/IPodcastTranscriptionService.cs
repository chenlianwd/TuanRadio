using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 播客无 Shownotes 单集音频前段安全抓取、转码并调用 STT 转写文本的服务契约。
/// </summary>
public interface IPodcastTranscriptionService
{
    Task<string> TranscribeExcerptAsync(
        PodcastEpisode episode,
        string? languageHint = null,
        CancellationToken cancellationToken = default);
}
