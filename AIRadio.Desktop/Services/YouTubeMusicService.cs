using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// YouTube 音乐搜索服务，通过 yt-dlp 获取音频流。
/// 作为现有四源的最低优先级兜底。
/// </summary>
public class YouTubeMusicService : IMusicSearchService
{
    private readonly MusicAccountStore? _accounts;

    public string Name => "YouTube";
    public bool IsSlowSource => true;

    // ytdlpPath 参数保留以兼容现有调用方；yt-dlp 的安装与版本治理统一由 YtdlpManager 受管路径负责
    public YouTubeMusicService(string ytdlpPath, MusicAccountStore? accounts = null)
    {
        _accounts = accounts;
    }

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
        => SearchAsync(keyword, limit, CancellationToken.None);

    public async Task<List<OnlineTrack>> SearchAsync(
        string keyword,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use --dump-json for structured output (one JSON object per line)
            var args = $"\"ytsearch{limit}:{EscapeArg(keyword)}\" --dump-json --no-download --no-warnings{BuildCookieArgs()}";
            var output = await RunYtdlpAsync(args, cancellationToken);

            if (string.IsNullOrWhiteSpace(output))
                return new List<OnlineTrack>();

            return ParseSearchResultsJson(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (YtdlpUnavailableException ex)
        {
            // yt-dlp 未安装/版本被禁用属于源不可用（业务态）：透传为业务异常，聚合层
            // 记 failed；塌缩成空结果会被记 success("ok(0)")，YouTube 永不熔断、状态失真
            throw new MusicSourceBusinessException(ex.Message, MusicSourceFailureKind.ApiBroken);
        }
        // 其余异常不再吞掉：与其他源的 rethrow 口径一致，由聚合层按 failed/timeout 记账
    }

    public Task<string?> GetPlayUrlAsync(string trackId)
        => GetPlayUrlAsync(trackId, CancellationToken.None);

    public async Task<string?> GetPlayUrlAsync(string trackId, CancellationToken cancellationToken)
    {
        try
        {
            // trackId format: "youtube:VIDEO_ID"
            var videoId = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;

            // Validate YouTube video ID format (11 chars, alphanumeric + _ -)
            if (string.IsNullOrWhiteSpace(videoId) || videoId.Length > 20 ||
                videoId.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
            {
                Log.Warning("Invalid YouTube video ID: {VideoId}", videoId);
                return null;
            }

            var url = $"https://www.youtube.com/watch?v={videoId}";

            var args = $"-f ba --get-url --no-warnings --ignore-errors{BuildCookieArgs()} {EscapeArg(url)}";
            var output = await RunYtdlpAsync(args, cancellationToken);

            var playUrl = output?.Trim();
            if (!string.IsNullOrWhiteSpace(playUrl) &&
                (playUrl.StartsWith("http://") || playUrl.StartsWith("https://")))
            {
                return playUrl;
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (YtdlpUnavailableException ex)
        {
            // 同 SearchAsync：源不可用按业务异常透传，聚合层记录后继续回退下一源
            throw new MusicSourceBusinessException(ex.Message, MusicSourceFailureKind.ApiBroken);
        }
        // TimeoutException/其余异常上抛：聚合层按 timeout/failed 记账后返回 null 走跨源回退
    }

    private List<OnlineTrack> ParseSearchResultsJson(string output)
    {
        var tracks = new List<OnlineTrack>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                var channel = root.TryGetProperty("channel", out var chEl) ? chEl.GetString() : null;
                // yt-dlp 的 duration 常为小数或 null，GetInt64 会抛 InvalidOperationException 且逃过 JsonException 捕获
                var duration = 0L;
                if (root.TryGetProperty("duration", out var durEl) &&
                    durEl.ValueKind == JsonValueKind.Number &&
                    durEl.TryGetInt64(out var parsedDuration))
                {
                    duration = parsedDuration;
                }

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    continue;

                tracks.Add(new OnlineTrack
                {
                    Id = $"youtube:{id}",
                    Title = title,
                    Artist = channel ?? "",
                    Source = "YouTube",
                    DurationMs = duration * 1000
                });
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // GetString 对非字符串值抛 InvalidOperationException：一条畸形行
                // 不能把前面所有正常行一起丢掉（异常穿出会被上层当整次搜索失败）
                continue;
            }
        }

        return tracks;
    }

    private async Task<string?> RunYtdlpAsync(string args, CancellationToken cancellationToken)
    {
        // 不再兜底吞异常：执行失败若塌缩成 null 会被聚合层记 success("ok(0)")，
        // YouTube 源永远不熔断、逐源状态失真；异常上抛由聚合层统一记账
        var ytdlpPath = await EnsureUsableYtdlpAsync(cancellationToken);

        var psi = new ProcessStartInfo
        {
            FileName = ytdlpPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            // 内层 30s 超时按 TimeoutException 上抛：聚合层才能记 timeout（外层预算更大
            // 的调用路径下，返回 null 会被记成 success）
            Log.Warning("yt-dlp timed out after 30s");
            throw new TimeoutException("yt-dlp timed out after 30s");
        }

        var output = await outputTask;
        var stderr = await errorTask;

        if (process.ExitCode != 0)
        {
            // 搜索无结果时 yt-dlp 也以非零退出：无法与真实故障区分，保守按"无输出"处理
            Log.Warning("yt-dlp exited with code {Code}: {Error}", process.ExitCode, stderr);
            return null;
        }

        return output;
    }

    /// <summary>
    /// 获取可用的 yt-dlp 路径：低于最低支持版本的安装禁用本源；
    /// 更新失败（如离线）时保留满足最低版本的现有安装。
    /// </summary>
    private async Task<string> EnsureUsableYtdlpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await YtdlpManager.EnsureInstalledAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "yt-dlp version check/update failed; falling back to existing install");
        }

        var status = YtdlpManager.GetStatus();
        if (!status.Installed || !status.MeetsMinimumSupportedVersion)
        {
            Log.Warning("YouTube source disabled: {Reason}", status.DisableReason);
            throw new YtdlpUnavailableException(status.DisableReason ?? "yt-dlp 不可用");
        }

        return YtdlpManager.GetYtdlpPath();
    }

    /// <summary>
    /// YouTube 对匿名流量默认弹出 "Sign in to confirm you're not a bot" 风控，
    /// 配置了浏览器来源 cookies 时透传给 yt-dlp 复用真实登录态。
    /// </summary>
    private string BuildCookieArgs()
    {
        var browser = _accounts?.YtdlpCookieBrowser;
        return string.IsNullOrEmpty(browser) ? "" : $" --cookies-from-browser {EscapeArg(browser)}";
    }

    internal static string EscapeArg(string arg)
    {
        // Strip control characters and escape for safe process arguments (CRT parsing rules)：
        // 嵌入引号翻倍为 ""，且紧邻任意引号（含嵌入与闭合）前的连续反斜杠必须翻倍，
        // 否则结尾的 \" 被解析为字面引号、其后拼接的固定参数会全部并入同一参数
        var sanitized = arg.Replace("\r", "").Replace("\n", "").Replace("\0", "");
        var sb = new System.Text.StringBuilder(sanitized.Length + 2);
        var pendingBackslashes = 0;
        foreach (var c in sanitized)
        {
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (c == '"')
                sb.Append('\\', pendingBackslashes * 2).Append("\"\"");
            else
                sb.Append('\\', pendingBackslashes).Append(c);
            pendingBackslashes = 0;
        }
        sb.Append('\\', pendingBackslashes * 2); // 闭合引号前的结尾反斜杠翻倍
        return $"\"{sb}\"";
    }
}
