using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface ILyricService
{
    /// <summary>
    /// 按曲目当前 SourceId 取词。三态契约：
    /// null = 无词或失败（含超时/网络异常，歌词是展示增强，不抛异常、不进熔断与健康统计）；
    /// IsInstrumental=true 且 Lines 为空 = 纯音乐；
    /// Lines 非空 = 有词。
    /// </summary>
    Task<LyricResult?> GetLyricsAsync(Track track, CancellationToken cancellationToken);
}

public sealed class LyricResult
{
    public bool IsInstrumental { get; init; }
    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();
}

public sealed record LyricLine(TimeSpan Time, string Text);
