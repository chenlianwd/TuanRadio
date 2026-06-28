using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    /// Ensure Node.js is available. Prefer a system installation, otherwise download a portable copy.
    /// </summary>
    public static async Task<string> EnsureNodeJsAsync()
    {
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
        await DownloadNodeJsAsync();
        return NodeExe;
    }

    private static async Task DownloadNodeJsAsync()
    {
        Directory.CreateDirectory(NodeDir);

        var url = "https://nodejs.org/dist/v20.18.3/node-v20.18.3-win-x64.zip";
        var zipPath = Path.Combine(NodeDir, "node.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Log.Information("Downloading Node.js from {Url}...", url);

        var bytes = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(zipPath, bytes);
        Log.Information("Node.js downloaded ({Size}MB), extracting...", bytes.Length / 1024 / 1024);

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

        try { File.Delete(zipPath); } catch { }
    }
}
