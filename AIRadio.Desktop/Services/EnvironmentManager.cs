using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public static class EnvironmentManager
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string NodeDir = Path.Combine(AppDataDir, "node");
    private static readonly string NodeExe = Path.Combine(NodeDir, "node.exe");

    public static string NodeJsPath => NodeExe;

    /// <summary>
    /// 确保 Node.js 可用。优先用系统安装的，否则下载便携版。
    /// </summary>
    public static async Task<string> EnsureNodeJsAsync()
    {
        // 1. 尝试系统 Node.js
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                var version = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && version.StartsWith("v"))
                {
                    Log.Information("Using system Node.js {Version}", version.Trim());
                    return "node";
                }
            }
        }
        catch { }

        // 2. 尝试已下载的便携版
        if (File.Exists(NodeExe))
        {
            Log.Information("Using portable Node.js at {Path}", NodeExe);
            return NodeExe;
        }

        // 3. 下载便携版
        Log.Information("Node.js not found, downloading portable version...");
        await DownloadNodeJsAsync();
        return NodeExe;
    }

    private static async Task DownloadNodeJsAsync()
    {
        Directory.CreateDirectory(NodeDir);

        // Use Node.js v20 LTS for stability and smaller size
        var url = "https://nodejs.org/dist/v20.18.3/node-v20.18.3-win-x64.zip";
        var zipPath = Path.Combine(NodeDir, "node.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Log.Information("Downloading Node.js from {Url}...", url);

        var bytes = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(zipPath, bytes);
        Log.Information("Node.js downloaded ({Size}MB), extracting...", bytes.Length / 1024 / 1024);

        // Extract only node.exe to keep it small
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase))
            {
                entry.ExtractToFile(NodeExe, overwrite: true);
                Log.Information("Node.js extracted to {Path}", NodeExe);
                break;
            }
        }

        // Clean up zip
        try { File.Delete(zipPath); } catch { }
    }

    /// <summary>
    /// 检查 WebView2 Runtime 是否已安装
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsWebView2Installed()
    {
        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (key?.GetValue("pv") is string pv && !string.IsNullOrEmpty(pv))
                return true;
        }
        catch { }

        try
        {
            var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            if (key?.GetValue("pv") is string pv && !string.IsNullOrEmpty(pv))
                return true;
        }
        catch { }

        return false;
    }
}
