using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public enum MusicSearchIntent
{
    Explicit,
    Automatic
}

public interface IMusicSearchService
{
    string Name { get; }
    /// <summary>慢源仅供显式搜索/播放使用，不参与自动跨源恢复。</summary>
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
    /// <summary>播放地址解析所需的稳定音源参数；禁止保存凭据或临时直链。</summary>
    public Dictionary<string, string> ProviderMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // 数据层不写语言相关占位值（持久化发生在 Track 侧），展示层统一本地化，避免空白或错误语言
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) || Artist is "未知艺术家" or "Unknown artist" or "未知" or "Unknown"
        ? AppLanguage.T("未知艺术家", "Unknown artist")
        : Artist;
    public string DisplayDuration => DurationMs <= 0
        ? string.Empty
        : System.TimeSpan.FromMilliseconds(DurationMs).ToString(
            DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");

    public Track ToTrack(string playUrl) => new()
    {
        Id = Id,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = System.TimeSpan.FromMilliseconds(DurationMs),
        FilePath = playUrl,
        SourceId = Id,  // store original ID for URL re-resolution
        ProviderMetadata = new Dictionary<string, string>(ProviderMetadata, StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>音源业务失败的结构化分类（docs/plans/2026-09-15-music-source-experience-enhancement-design.md）。
/// None=非业务失败（超时/传输故障走既有 timeout/failed 状态，不参与分类渲染）。</summary>
public enum MusicSourceFailureKind
{
    None,
    /// <summary>未登录（酷狗搜索/歌单在无 Cookie 时直接拒绝）。</summary>
    NotSignedIn,
    /// <summary>业务码异常：登录态或本地代理可能失效（网易 code≠200、酷狗 status≠1 非 20028）。</summary>
    AuthExpired,
    /// <summary>酷狗风控验证（error_code 20028）。</summary>
    RiskControl,
    /// <summary>播放接口只返回试听片段，不能当完整曲目播放。</summary>
    PreviewOnly,
    /// <summary>网页接口/工具链失效（酷我、咪咕门户劫持、yt-dlp 不可用）。</summary>
    ApiBroken,
    Unknown
}

/// <summary>
/// 音源接口返回了明确的业务失败（鉴权失败、风控、代理失效等）。
/// 与"正常搜索无结果"区分：聚合层把这类异常透传为逐源 failed 状态，而不是误报"成功 0 条"。
/// Kind 供 UI 按类型渲染用户可读文案与恢复建议（默认 Unknown 时显示原始错误文本）。
/// </summary>
public class MusicSourceBusinessException : Exception
{
    public MusicSourceFailureKind Kind { get; }

    public MusicSourceBusinessException(string message,
        MusicSourceFailureKind kind = MusicSourceFailureKind.Unknown) : base(message)
    {
        Kind = kind;
    }
}
