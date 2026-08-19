using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IMusicSearchService
{
    string Name { get; }
    Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20);
    Task<string?> GetPlayUrlAsync(string trackId);

    // 保留旧签名，避免现有插件/测试实现立即失效；真实音源可覆写该重载，
    // 聚合服务用它把超时和页面离开取消传递到底层 HTTP/进程。
    Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
        => SearchAsync(keyword, limit);

    Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
        => GetPlayUrlAsync(trackId);

    Task<string?> GetPlayUrlAsync(OnlineTrack track, CancellationToken cancellationToken)
        => GetPlayUrlAsync(track.Id, cancellationToken);
}

public class OnlineTrack
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string Source { get; set; } = string.Empty;

    public Track ToTrack(string playUrl) => new()
    {
        Id = Id,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = System.TimeSpan.FromMilliseconds(DurationMs),
        FilePath = playUrl,
        SourceId = Id  // store original ID for URL re-resolution
    };
}
