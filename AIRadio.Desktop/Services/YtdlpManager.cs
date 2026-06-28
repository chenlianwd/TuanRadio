using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 管理 yt-dlp 可执行文件的下载和路径。
/// 类似 EnvironmentManager 对 Node.js 的处理。
/// </summary>
public static class YtdlpManager
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string YtdlpDir = Path.Combine(AppDataDir, "ytdlp");
    private static readonly string YtdlpExe = Path.Combine(YtdlpDir, "yt-dlp.exe");

    // Pinned version for reproducibility
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static string GetYtdlpPath() => YtdlpExe;

    public static bool IsInstalled() => File.Exists(YtdlpExe);

    public static async Task<string> EnsureInstalledAsync()
    {
        if (IsInstalled())
            return YtdlpExe;

        Directory.CreateDirectory(YtdlpDir);

        Log.Information("Downloading yt-dlp...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        var bytes = await http.GetByteArrayAsync(DownloadUrl);
        await File.WriteAllBytesAsync(YtdlpExe, bytes);

        Log.Information("yt-dlp downloaded to {Path}", YtdlpExe);
        return YtdlpExe;
    }
}
