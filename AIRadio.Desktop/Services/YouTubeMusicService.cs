using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
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

    public string Name => "YouTube";

    public YouTubeMusicService(string ytdlpPath)
    {
        _ytdlpPath = ytdlpPath;
    }

    public async Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
    {
        try
        {
            var args = $"\"ytsearch{limit}:{EscapeArg(keyword)}\" --print id --print title --print duration_string --print channel --no-download --no-warnings --ignore-errors";
            var output = await RunYtdlpAsync(args);

            if (string.IsNullOrWhiteSpace(output))
                return new List<OnlineTrack>();

            return ParseSearchResults(output);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "YouTube search failed for {Keyword}", keyword);
            return new List<OnlineTrack>();
        }
    }

    public async Task<string?> GetPlayUrlAsync(string trackId)
    {
        try
        {
            // trackId format: "youtube:VIDEO_ID"
            var videoId = trackId.Contains(':') ? trackId.Split(':')[1] : trackId;
            var url = $"https://www.youtube.com/watch?v={videoId}";

            var args = $"-f ba --get-url --no-warnings --ignore-errors {EscapeArg(url)}";
            var output = await RunYtdlpAsync(args);

            var playUrl = output?.Trim();
            if (!string.IsNullOrWhiteSpace(playUrl) &&
                (playUrl.StartsWith("http://") || playUrl.StartsWith("https://")))
            {
                return playUrl;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "YouTube get play URL failed for {TrackId}", trackId);
            return null;
        }
    }

    private List<OnlineTrack> ParseSearchResults(string output)
    {
        var tracks = new List<OnlineTrack>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Each result has 4 lines: id, title, duration, channel
        for (int i = 0; i + 3 < lines.Length; i += 4)
        {
            var id = lines[i].Trim();
            var title = lines[i + 1].Trim();
            var durationStr = lines[i + 2].Trim();
            var channel = lines[i + 3].Trim();

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                continue;

            var durationMs = ParseDuration(durationStr);

            tracks.Add(new OnlineTrack
            {
                Id = $"youtube:{id}",
                Title = title,
                Artist = channel,
                Source = "YouTube",
                DurationMs = durationMs
            });
        }

        return tracks;
    }

    private static long ParseDuration(string durationStr)
    {
        // Format: HH:MM:SS or MM:SS
        try
        {
            var parts = durationStr.Split(':').Select(int.Parse).ToArray();
            if (parts.Length == 3)
                return (long)(new TimeSpan(parts[0], parts[1], parts[2])).TotalMilliseconds;
            if (parts.Length == 2)
                return (long)(new TimeSpan(0, parts[0], parts[1])).TotalMilliseconds;
        }
        catch { }
        return 0;
    }

    private async Task<string?> RunYtdlpAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ytdlpPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "yt-dlp execution failed");
            return null;
        }
    }

    private static string EscapeArg(string arg)
        => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
