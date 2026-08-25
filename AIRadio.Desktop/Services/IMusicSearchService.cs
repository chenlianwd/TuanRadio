using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IMusicSearchService
{
    string Name { get; }
    /// <summary>慢源只在快速源没有结果时使用，但仍受同一个用户操作 deadline 约束。</summary>
    bool IsSlowSource => false;
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

/// <summary>
/// 音源接口返回了明确的业务失败（鉴权失败、风控、代理失效等）。
/// 与"正常搜索无结果"区分：聚合层把这类异常透传为逐源 failed 状态，而不是误报"成功 0 条"。
/// </summary>
public class MusicSourceBusinessException : Exception
{
    public MusicSourceBusinessException(string message) : base(message)
    {
    }
}
