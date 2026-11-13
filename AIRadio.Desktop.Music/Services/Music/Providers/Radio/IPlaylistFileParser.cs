using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

public sealed record PlaylistParseResult(
    IReadOnlyList<RadioStation> Stations,
    int SkippedCount,
    bool WasTruncated);

/// <summary>
/// M3U / M3U8 / PLS 网络电台播放列表文件解析器。
/// </summary>
public interface IPlaylistFileParser
{
    Task<PlaylistParseResult> ParseFileAsync(string filePath, CancellationToken ct = default);
    PlaylistParseResult ParseContent(string content, string? defaultNamePrefix = null);
}
