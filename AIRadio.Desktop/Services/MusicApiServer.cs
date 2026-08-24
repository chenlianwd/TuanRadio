using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    private readonly string _healthQuery;
    private readonly Func<string, bool> _healthValidator;
    private readonly string _logTag;
    private readonly bool _requireSuccessStatusCode;
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

    /// <summary>
    /// 本地 Node 音乐 API 代理管理器。默认参数对应网易云代理（server 目录 + 37250 端口），
    /// 酷狗代理复用本类并传入各自的目录/端口/健康检查形状。
    /// requireSuccessStatusCode=false 适用于"业务失败也用 2xx 之外的状态码表达"的代理
    /// （如酷狗未登录搜索返回 502 + 合法 JSON），此时仅凭响应体形状判定健康。
    /// </summary>
    public MusicApiServer(
        int port = DefaultPort,
        string? serverDirName = null,
        string? healthQuery = null,
        Func<string, bool>? healthResponseValidator = null,
        string? logTag = null,
        bool requireSuccessStatusCode = true)
    {
        _port = port;
        _serverDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, serverDirName ?? "server");
        _healthQuery = healthQuery ?? "/search?keywords=test&limit=1";
        _healthValidator = healthResponseValidator ?? LooksLikeNeteaseSearchResponse;
        _logTag = logTag ?? "MusicApi";
        _requireSuccessStatusCode = requireSuccessStatusCode;
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
            Log.Information("{Tag} server is already available on port {Port}", _logTag, _port);
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
                        Log.Debug("{Tag}: {Line}", _logTag, e.Data);
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Log.Warning("{Tag} ERR: {Line}", _logTag, e.Data);
                };

                cancellationToken.ThrowIfCancellationRequested();
                _process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            // Short-lived HttpClient for startup health check only (called once)
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < MaxStartupRetries; i++)
            {
                try
                {
                    using var resp = await http.GetAsync(
                        $"http://127.0.0.1:{_port}{_healthQuery}",
                        cancellationToken);
                    if (HealthResponseAccepted(resp) &&
                        _healthValidator(await resp.Content.ReadAsStringAsync(cancellationToken)))
                    {
                        Log.Information("{Tag} server ready on port {Port}", _logTag, _port);
                        return;
                    }
                }
                catch
                {
                }

                await Task.Delay(500, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0)
                    return;
            }

            // 健康检查失败也要回收进程：留着只会占用端口并让后续启动复用判断复杂化
            Stop();
            Log.Warning("{Tag} server did not become ready within 15 seconds", _logTag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Stop();
        }
        catch (Exception ex)
        {
            Stop();
            Log.Error(ex, "Failed to start {Tag} server", _logTag);
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
                Log.Information("{Tag} server stopped", _logTag);
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
                $"http://127.0.0.1:{_port}{_healthQuery}",
                cancellationToken);
            if (!HealthResponseAccepted(response))
                return false;

            // 仅凭 2xx 复用端口会被恰好占用本地端口的第三方进程冒充，
            // 校验响应体是目标 API 形状的 JSON 才认可为自有服务
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return _healthValidator(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "{Tag} readiness probe failed on port {Port}", _logTag, _port);
            return false;
        }
    }

    private bool HealthResponseAccepted(HttpResponseMessage response)
        => !_requireSuccessStatusCode || response.IsSuccessStatusCode;

    private static bool LooksLikeNeteaseSearchResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64 * 1024)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
