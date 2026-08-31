using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 管理 yt-dlp 可执行文件的下载、版本治理和路径。
/// 安装策略：固定官方 release 版本 + SHA256 校验（不追随 latest），
/// 低于最低支持版本的安装禁用 YouTube 音源并给出可理解诊断。
/// </summary>
public static class YtdlpManager
{
    // 固定版本清单：升级 yt-dlp 时同步更新版本号与官方 release 的 SHA256。
    // SHA256 来源于发布时人工核对官方 release 资产，随应用版本固定发布。
    public const string PinnedVersion = "2026.08.19";
    public const string PinnedSha256 = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";

    // 低于该版本不再进入兼容支持范围并禁用 YouTube 音源。
    // 这不是安全公告推导出的漏洞边界；已知漏洞版本应另行按官方公告显式封禁。
    public const string MinimumSupportedVersion = "2025.08.19";

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string YtdlpDir = Path.Combine(AppDataDir, "ytdlp");
    private static readonly string YtdlpExe = Path.Combine(YtdlpDir, "yt-dlp.exe");
    private static readonly string VersionFile = Path.Combine(YtdlpDir, "yt-dlp.version");
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static string GetYtdlpPath() => YtdlpExe;

    public static bool IsInstalled() => File.Exists(YtdlpExe);

    public sealed record YtdlpStatus(bool Installed, string? Version, bool MeetsMinimumSupportedVersion)
    {
        /// <summary>禁用 YouTube 音源时的用户可理解原因；可用时为 null。</summary>
        public string? DisableReason => Installed && MeetsMinimumSupportedVersion
            ? null
            : Installed
                ? AppLanguage.T(
                    $"yt-dlp 版本过旧（{Version ?? "未知"}），低于最低支持版本 {MinimumSupportedVersion}，请联网后重启应用以自动更新",
                    $"yt-dlp is outdated ({Version ?? "unknown"}); minimum supported version is {MinimumSupportedVersion}. Connect to the internet and restart the app to update it.")
                : AppLanguage.T("yt-dlp 未安装", "yt-dlp is not installed");
    }

    /// <summary>读取当前安装状态；版本记录缺失（旧版应用用 latest 安装）视为未知。</summary>
    public static YtdlpStatus GetStatus()
    {
        var installed = File.Exists(YtdlpExe);
        string? version = null;
        if (installed && File.Exists(VersionFile))
        {
            try { version = File.ReadAllText(VersionFile).Trim(); } catch { }
        }

        var meets = installed &&
                    !string.IsNullOrEmpty(version) &&
                    CompareVersions(version, MinimumSupportedVersion) >= 0;
        return new YtdlpStatus(installed, version, meets);
    }

    /// <summary>
    /// 确保安装的是固定版本：缺失则安装，版本不同（旧版或无版本记录）则替换为固定版本。
    /// 离线且已有可用安装时不抛错、保留现状；更新失败抛出异常由调用方决定是否回退。
    /// </summary>
    public static async Task<string> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (IsUpToDate(status))
            return YtdlpExe;

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            status = GetStatus();
            if (IsUpToDate(status))
                return YtdlpExe;

            // 旧版应用按 latest 安装的可执行文件没有版本记录：先探测实际版本，已是固定版本则只补记录
            if (status.Installed && string.IsNullOrEmpty(status.Version))
            {
                var detected = await DetectInstalledVersionAsync(cancellationToken);
                if (detected == PinnedVersion)
                {
                    WriteVersionFile(detected);
                    return YtdlpExe;
                }
                Log.Information("Detected yt-dlp {Detected}, replacing with pinned {Pinned}", detected, PinnedVersion);
            }
            else if (status.Installed)
            {
                Log.Information("Updating yt-dlp {Current} to pinned {Pinned}", status.Version, PinnedVersion);
            }

            Directory.CreateDirectory(YtdlpDir);
            Log.Information("Downloading yt-dlp {Version}...", PinnedVersion);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await http.GetAsync(
                BuildDownloadUrl(PinnedVersion),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var installed = await InstallVerifiedAsync(
                input,
                YtdlpExe,
                VersionFile,
                PinnedSha256,
                PinnedVersion,
                cancellationToken);
            if (!installed)
                return YtdlpExe; // 目标被占用跳过更新：旧版继续可用，避免误报“已安装新版”
        }
        finally
        {
            InstallGate.Release();
        }

        Log.Information("yt-dlp {Version} installed at {Path}", PinnedVersion, YtdlpExe);
        return YtdlpExe;
    }

    /// <summary>
    /// 把下载流写入临时文件、校验 SHA256 后原子替换目标文件并记录版本。
    /// 校验失败或取消时清理半成品临时文件，绝不替换现有可执行文件。
    /// 返回是否实际完成替换（目标文件被占用时跳过更新并返回 false，保留旧版可用）。
    /// </summary>
    internal static async Task<bool> InstallVerifiedAsync(
        Stream input,
        string targetPath,
        string versionFilePath,
        string expectedSha256,
        string version,
        CancellationToken cancellationToken)
    {
        var tempPath = targetPath + ".tmp";
        try
        {
            await WriteVerifiedFileAsync(tempPath, input, expectedSha256, cancellationToken);
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // Windows 不允许覆盖正在运行的可执行文件：更新恰逢旧版 yt-dlp 正在播放时，
                // 保留旧版并跳过本次更新（版本文件不写，状态判定维持旧版可用，下次启动再更）
                Log.Warning("yt-dlp update skipped: target executable is currently in use");
                return false;
            }
            File.WriteAllText(versionFilePath, version);
            return true;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    internal static bool IsUpToDate(YtdlpStatus status)
        => status.Installed && status.Version == PinnedVersion;

    internal static string BuildDownloadUrl(string version)
        => $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/yt-dlp.exe";

    /// <summary>运行 <c>yt-dlp --version</c> 探测已安装版本；失败或超时返回 null。</summary>
    internal static async Task<string?> DetectInstalledVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(YtdlpExe))
            return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = YtdlpExe,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
                return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var version = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            return version.Length > 0 ? version : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "yt-dlp version detection failed");
            return null;
        }
    }

    /// <summary>
    /// 把输入流写入临时文件并校验 SHA256；不匹配抛 <see cref="YtdlpVerificationException"/>，
    /// 由调用方清理临时文件且不替换现有安装。
    /// </summary>
    internal static async Task WriteVerifiedFileAsync(
        string tempPath,
        Stream input,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        var actual = await ComputeSha256Async(tempPath, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // 校验失败自清理：不留"看似可用"的半成品，由调用方决定错误处理
            try { File.Delete(tempPath); } catch { }
            throw new YtdlpVerificationException(
                $"yt-dlp SHA256 mismatch: expected {expectedSha256}, got {actual}");
        }
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>按 "YYYY.MM.DD[.rev]" 数值段比较版本；null/空视为最低。</summary>
    internal static int CompareVersions(string? left, string? right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        var count = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < count; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : 0;
            var r = i < rightParts.Length ? rightParts[i] : 0;
            if (l != r) return l.CompareTo(r);
        }
        return 0;
    }

    private static int[] ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Array.Empty<int>();

        var segments = version.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parts[i]))
                parts[i] = 0;
        }
        return parts;
    }

    private static void WriteVersionFile(string version)
    {
        try { File.WriteAllText(VersionFile, version); }
        catch (Exception ex) { Log.Debug(ex, "Failed to persist yt-dlp version file"); }
    }
}

/// <summary>下载的 yt-dlp 哈希校验失败：不得替换现有可执行文件。</summary>
public sealed class YtdlpVerificationException : Exception
{
    public YtdlpVerificationException(string message) : base(message)
    {
    }
}

/// <summary>yt-dlp 缺失或低于最低支持版本：YouTube 音源不可用。</summary>
public sealed class YtdlpUnavailableException : Exception
{
    public YtdlpUnavailableException(string message) : base(message)
    {
    }
}
