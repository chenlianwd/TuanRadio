using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public class MusicApiServer : IDisposable
{
    private Process? _process;
    private readonly int _port;
    private readonly string _serverDir;

    public int Port => _port;
    public bool IsRunning => _process is { HasExited: false };

    public MusicApiServer(int port = 37250)
    {
        _port = port;
        _serverDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server");
    }

    public async Task StartAsync()
    {
        if (!Directory.Exists(_serverDir))
        {
            Log.Warning("Music server directory not found: {Dir}", _serverDir);
            return;
        }

        var startScript = Path.Combine(_serverDir, "start.js");
        if (!File.Exists(startScript))
        {
            Log.Warning("Music server start script not found");
            return;
        }

        try
        {
            // 确保 Node.js 可用（自动下载便携版）
            var nodePath = await EnvironmentManager.EnsureNodeJsAsync();
            Log.Information("Using Node.js at: {Path}", nodePath);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = nodePath,
                    Arguments = $"\"{startScript}\"",
                    WorkingDirectory = _serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Debug("MusicApi: {Line}", e.Data);
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Warning("MusicApi ERR: {Line}", e.Data);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Wait for server to be ready
            using var http = new HttpClient();
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var resp = await http.GetAsync($"http://127.0.0.1:{_port}/search?keywords=test&limit=1");
                    if (resp.IsSuccessStatusCode)
                    {
                        Log.Information("Music API server ready on port {Port}", _port);
                        return;
                    }
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            Log.Warning("Music API server did not become ready within 15 seconds");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start music API server");
        }
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
                Log.Information("Music API server stopped");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error stopping music API server");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
