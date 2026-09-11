using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public static class EnvironmentManager
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string NodeDir = Path.Combine(AppDataDir, "node");
    private static readonly string NodeExe = Path.Combine(NodeDir, "node.exe");
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static string NodeJsPath => NodeExe;

    /// <summary>
    /// Ensure Node.js is available. Prefer a system installation, otherwise download a portable copy.
    /// </summary>
    public static async Task<string> EnsureNodeJsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 探测加超时：损坏的 node shim 可能挂死，无超时会让音乐代理初始化无限延迟；
        // 超时/取消都要击杀探测进程
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(10));
        Process? probe = null;
        try
        {
            probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (probe != null)
            {
                var version = await probe.StandardOutput.ReadToEndAsync(probeCts.Token);
                await probe.WaitForExitAsync(probeCts.Token);
                if (probe.ExitCode == 0 && version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Using system Node.js {Version}", version.Trim());
                    return "node";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { probe?.Kill(entireProcessTree: true); } catch { }
            throw; // 应用关闭的取消不能被当成"没有 node"落入下载分支
        }
        catch (OperationCanceledException)
        {
            try { probe?.Kill(entireProcessTree: true); } catch { }
            Log.Debug("Node.js version probe timed out; falling through to portable runtime");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Node.js detection failed, falling through to portable runtime");
        }
        finally
        {
            probe?.Dispose();
        }

        if (File.Exists(NodeExe))
        {
            Log.Information("Using portable Node.js at {Path}", NodeExe);
            return NodeExe;
        }

        Log.Information("Node.js not found, downloading portable version...");
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(NodeExe))
                await DownloadNodeJsAsync(cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }

        return NodeExe;
    }

    private static async Task DownloadNodeJsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(NodeDir);

        var url = "https://nodejs.org/dist/v20.18.3/node-v20.18.3-win-x64.zip";
        // 官方 SHASUMS256.txt (https://nodejs.org/dist/v20.18.3/SHASUMS256.txt) 中
        // node-v20.18.3-win-x64.zip 的校验值：下载产物必须完全一致才允许落盘解压
        const string expectedSha256 = "11D483DFBA711BC7C9BCB513E80A2941BE0C2E7CBF62753755785B9A6E80A731";
        var zipPath = Path.Combine(NodeDir, "node.zip.tmp");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Log.Information("Downloading Node.js from {Url}...", url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var fileStream = File.Create(zipPath))
        await using (var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await downloadStream.CopyToAsync(fileStream, cancellationToken);
            Log.Information("Node.js downloaded ({Size}MB), verifying...", fileStream.Length / 1024 / 1024);
        }

        // 对照官方 SHASUMS256 校验，fail-closed：读不出哈希同样拒绝落盘（供应链防护不允许静默跳过）
        string actualSha256;
        await using (var hashStream = File.OpenRead(zipPath))
        {
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, cancellationToken);
            actualSha256 = Convert.ToHexString(hashBytes);
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidDataException(
                $"Node.js download integrity check failed: expected {expectedSha256}, got {actualSha256}");
        }

        Log.Information("Node.js zip SHA256 verified: {Hash}", actualSha256);

        var extracted = false;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                var extractedPath = NodeExe + ".tmp";
                entry.ExtractToFile(extractedPath, overwrite: true);
                File.Move(extractedPath, NodeExe, overwrite: true);
                extracted = true;
                Log.Information("Node.js extracted to {Path}", NodeExe);
                break;
            }
        }

        try { File.Delete(zipPath); } catch { }
        if (!extracted)
            throw new InvalidDataException("Downloaded Node.js archive did not contain node.exe");
    }
}
