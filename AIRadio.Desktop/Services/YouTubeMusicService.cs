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
    private readonly string _ytdlpPath;
    private readonly MusicAccountStore? _accounts;

    public string Name => "YouTube";

    public YouTubeMusicService(string ytdlpPath, MusicAccountStore? accounts = null)
    {
        _ytdlpPath = ytdlpPath;
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
        catch (Exception ex)
        {
            Log.Warning(ex, "YouTube search failed for {Keyword}", keyword);
            return new List<OnlineTrack>();
        }
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
        catch (Exception ex)
        {
            Log.Warning(ex, "YouTube get play URL failed for {TrackId}", trackId);
            return null;
        }
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
            catch (JsonException)
            {
                // Skip malformed lines
                continue;
            }
        }

        return tracks;
    }

    private async Task<string?> RunYtdlpAsync(string args, CancellationToken cancellationToken)
    {
        try
        {
            var ytdlpPath = File.Exists(_ytdlpPath)
                ? _ytdlpPath
                : await YtdlpManager.EnsureInstalledAsync(cancellationToken);

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
                Log.Warning("yt-dlp timed out after 30s");
                return null;
            }

            var output = await outputTask;
            var stderr = await errorTask;

            if (process.ExitCode != 0)
            {
                Log.Warning("yt-dlp exited with code {Code}: {Error}", process.ExitCode, stderr);
                return null;
            }

            return output;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "yt-dlp execution failed");
            return null;
        }
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

    private static string EscapeArg(string arg)
    {
        // Strip control characters and escape embedded double quotes for safe process arguments
        var sanitized = arg.Replace("\r", "").Replace("\n", "").Replace("\0", "");
        var escaped = sanitized.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
