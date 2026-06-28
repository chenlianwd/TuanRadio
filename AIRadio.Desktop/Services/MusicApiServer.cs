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
    private const int DefaultPort = 37250;
    private const int MaxStartupRetries = 30;
    private readonly string _serverDir;

    public int Port => _port;
    public bool IsRunning => _process is { HasExited: false };

    public MusicApiServer(int port = DefaultPort)
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

        // Kill any leftover Node.js process on the same port
        KillProcessOnPort();

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
            for (int i = 0; i < MaxStartupRetries; i++)
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

    private void KillProcessOnPort()
    {
        try
        {
            // Use netstat to find PID using the port
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netstat -ano | findstr :{_port}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[0].Contains("TCP") && parts[1].Contains($":{_port}"))
                {
                    var pidStr = parts[^1];
                    if (int.TryParse(pidStr, out var pid) && pid > 0)
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            p.Kill(entireProcessTree: true);
                            Log.Information("Killed leftover process PID {Pid} on port {Port}", pid, _port);
                        }
                        catch (Exception ex) { Log.Debug(ex, "Failed to kill process PID {Pid}", pid); }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not check for processes on port {Port}", _port);
        }
    }
}
