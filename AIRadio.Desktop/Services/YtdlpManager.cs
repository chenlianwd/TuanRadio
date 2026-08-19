using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
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
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    // 官方 release endpoint，首次真正使用 YouTube 时按需下载，避免启动阶段阻塞。
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static string GetYtdlpPath() => YtdlpExe;

    public static bool IsInstalled() => File.Exists(YtdlpExe);

    public static async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled())
            return YtdlpExe;

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            if (IsInstalled())
                return YtdlpExe;

            Directory.CreateDirectory(YtdlpDir);

            Log.Information("Downloading yt-dlp...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await http.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var tempPath = YtdlpExe + ".tmp";
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(tempPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            File.Move(tempPath, YtdlpExe, overwrite: true);
        }
        finally
        {
            InstallGate.Release();
        }

        Log.Information("yt-dlp downloaded to {Path}", YtdlpExe);
        return YtdlpExe;
    }
}
