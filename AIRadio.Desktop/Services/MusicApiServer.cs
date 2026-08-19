using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
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
    private readonly object _processGate = new();
    private int _disposed;

    public int Port => _port;
    public bool IsRunning
    {
        get
        {
            lock (_processGate)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public MusicApiServer(int port = DefaultPort)
    {
        _port = port;
        _serverDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
            return;

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

        // 复用已经在运行的自有 API 实例；绝不按端口盲杀进程，避免误杀用户的
        // 其他 Node/开发服务。
        if (await IsServerReadyAsync(cancellationToken))
        {
            Log.Information("Music API server is already available on port {Port}", _port);
            return;
        }

        try
        {
            // 确保 Node.js 可用（自动下载便携版）
            var nodePath = await EnvironmentManager.EnsureNodeJsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Log.Information("Using Node.js at: {Path}", nodePath);

            Process process;
            lock (_processGate)
            {
                if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
                    return;

                process = new Process
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

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log.Debug("MusicApi: {Line}", e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log.Warning("MusicApi ERR: {Line}", e.Data);
                };

                cancellationToken.ThrowIfCancellationRequested();
                _process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            // Short-lived HttpClient for startup health check only (called once)
            using var http = new HttpClient();
            for (int i = 0; i < MaxStartupRetries; i++)
            {
                try
                {
                    using var resp = await http.GetAsync(
                        $"http://127.0.0.1:{_port}/search?keywords=test&limit=1",
                        cancellationToken);
                    if (resp.IsSuccessStatusCode)
                    {
                        Log.Information("Music API server ready on port {Port}", _port);
                        return;
                    }
                }
                catch
                {
                    await Task.Delay(500, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0)
                    return;
            }

            Log.Warning("Music API server did not become ready within 15 seconds");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Stop();
        }
        catch (Exception ex)
        {
            Stop();
            Log.Error(ex, "Failed to start music API server");
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_processGate)
        {
            process = _process;
            _process = null;
        }

        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                Log.Information("Music API server stopped");
            }
        }
        catch (Exception ex)
        {
            // Shutdown can race with StartAsync cancellation and Process.Dispose.
            Log.Debug(ex, "Error stopping music API server");
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
    }

    private async Task<bool> IsServerReadyAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        try
        {
            using var response = await http.GetAsync(
                $"http://127.0.0.1:{_port}/search?keywords=test&limit=1",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Music API readiness probe failed on port {Port}", _port);
            return false;
        }
    }
}
