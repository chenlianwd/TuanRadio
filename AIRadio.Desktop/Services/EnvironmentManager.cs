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
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                var version = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);
                if (proc.ExitCode == 0 && version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Using system Node.js {Version}", version.Trim());
                    return "node";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Node.js detection failed, falling through to portable runtime");
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
        var zipPath = Path.Combine(NodeDir, "node.zip.tmp");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Log.Information("Downloading Node.js from {Url}...", url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using (var fileStream = File.Create(zipPath))
        await using (var downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await downloadStream.CopyToAsync(fileStream, cancellationToken);
            Log.Information("Node.js downloaded ({Size}MB), extracting...", fileStream.Length / 1024 / 1024);
        }

        // 计算 SHA256 供审计（完整校验需对照 Node.js 官方 SHASUMS256.txt，子项目 5）
        try
        {
            await using var hashStream = File.OpenRead(zipPath);
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(hashStream, cancellationToken);
            Log.Information("Node.js zip SHA256: {Hash}", Convert.ToHexString(hashBytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) { Log.Warning(ex, "Failed to compute Node.js SHA256"); }

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
