using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public class MusicApiServer : IDisposable
{
    private Process? _process;
    private KillOnCloseJob? _childJob;
    private readonly int _port;

    /// <summary>网易/酷狗本地代理端口的自愈与装配共享同一来源，避免多处字面量漂移。</summary>
    public const int NeteaseProxyPort = 37250;
    public const int KugouProxyPort = 37251;
    private const int DefaultPort = NeteaseProxyPort;
    private const int MaxStartupRetries = 30;
    private readonly string _serverDir;
    private readonly string _healthQuery;
    private readonly Func<string, bool> _healthValidator;
    private readonly string _logTag;
    private readonly bool _requireSuccessStatusCode;
    private readonly Func<IReadOnlyDictionary<string, string>>? _environmentFactory;
    private readonly object _processGate = new();
    // 串行化整个“探测→拉起→就绪确认”启动序列，防并发启动泄漏 Node 进程
    private readonly SemaphoreSlim _startGate = new(1, 1);
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
    /// 网易代理的启动环境：固定 CORS（响应头只拦浏览器读取，请求准入在代理侧）+
    /// 安装级身份令牌（KugouDevice 摘要，LoadAsync 已在代理启动前恢复为持久化值）。
    /// 代理侧 /tuanradio/identity 返回令牌摘要，收养判定据此比对——仅受 start.js
    /// 控制的自有进程持有注入的令牌，端口上的冒充者答不上来。
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildNeteaseProxyEnvironment(MusicAccountStore accounts)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CORS_ALLOW_ORIGIN"] = "http://tuanradio.local",
            ["TUANRADIO_PROXY_IDENTITY"] = accounts.KugouDevice.ComputeProxyIdentityHash(),
        };
    }

    /// <summary>代理身份令牌的摘要（与代理侧 sha256(令牌) 同口径）。</summary>
    public static string ProxyIdentityHash(string token)
        => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    /// <summary>酷狗/网易共用的 /tuanradio/identity 响应校验（服务名 + 协议版 + 令牌摘要比对）。</summary>
    public static bool MatchesProxyIdentityResponse(string? json, string serviceName, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.TryGetInt32(out var statusCode) && statusCode == 1 &&
                   root.TryGetProperty("service", out var service) &&
                   string.Equals(service.GetString(), serviceName, StringComparison.Ordinal) &&
                   root.TryGetProperty("protocol", out var protocol) &&
                   protocol.TryGetInt32(out var protocolVersion) && protocolVersion == 1 &&
                   root.TryGetProperty("device_hash", out var deviceHash) &&
                   string.Equals(deviceHash.GetString(), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
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
        bool requireSuccessStatusCode = true,
        Func<IReadOnlyDictionary<string, string>>? environmentFactory = null)
    {
        _port = port;
        _serverDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, serverDirName ?? "server");
        _healthQuery = healthQuery ?? "/search?keywords=test&limit=1";
        _healthValidator = healthResponseValidator ?? LooksLikeNeteaseSearchResponse;
        _logTag = logTag ?? "MusicApi";
        _requireSuccessStatusCode = requireSuccessStatusCode;
        _environmentFactory = environmentFactory;
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

        // 整个“探测→拉起→就绪确认”序列互斥：并发 StartAsync 会各启一个 Node 进程，
        // 后写 _process 覆盖前者，泄漏的进程无人回收
        await _startGate.WaitAsync(cancellationToken);
        try
        {
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
                            RedirectStandardError = true,
                            // Node 进程固定输出 UTF-8；不声明则按系统 ANSI 代码页解码出乱码
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        },
                        EnableRaisingEvents = true
                    };

                    if (_environmentFactory?.Invoke() is { } environment)
                    {
                        foreach (var (name, value) in environment)
                        {
                            if (!string.IsNullOrWhiteSpace(name) && value != null)
                                process.StartInfo.Environment[name] = value;
                        }
                    }

                    process.OutputDataReceived += (_, e) =>
                    {
                        // 代理 stdout 原样落 Debug 文件日志：kugou 侧有 redactUrl 但上游日志
                        // 形态可能变化，C# 侧按脱敏键集兜底（与 AddSearchReport/UrlForLog 同族）
                        if (!string.IsNullOrEmpty(e.Data))
                            Log.Debug("{Tag}: {Line}", _logTag, SensitiveDataSanitizer.Sanitize(e.Data));
                    };

                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                            Log.Warning("{Tag} ERR: {Line}", _logTag, e.Data);
                    };

                    cancellationToken.ThrowIfCancellationRequested();
                    _process = process;
                    process.Start();
                    // 强杀（停止调试/任务管理器）不再留孤儿：作业对象 kill-on-close 绑定父子
                    // 生命周期；创建/绑定失败仅降级为旧语义（可留孤儿），不阻断启动
                    var job = _childJob ??= KillOnCloseJob.TryCreate();
                    if (job == null)
                        Log.Warning("{Tag} server could not create a kill-on-close job; force-killing the app may leave orphan Node processes", _logTag);
                    else if (!job.TryAssign(process))
                        Log.Warning("{Tag} server process could not be bound to the parent lifetime job", _logTag);
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
        finally
        {
            _startGate.Release();
        }
    }

    public void Stop()
    {
        var process = DetachProcess();
        if (process == null)
            return;

        TerminateProcess(process);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 关闭线程绝不同步 Kill(entireProcessTree)：在调试器附加的进程树上该调用
        // 实测阻塞 8~12 秒（.NET 的整树终止确认要等调试器拆卸调试对象），两个
        // 代理串行会把后续清理（含音频停止）推迟约 20 秒。先关 job 句柄——内核
        // kill-on-close 立即终止全部绑定进程，不经过 .NET 的整树轮询路径；Kill
        // 与 Process 句柄回收降级为后台任务，兜底 job 创建失败的降级场景。后台
        // 线程不拖进程退出（线程池线程随 CLR 退出终止，进程必死由 job 保证）。
        _childJob?.Dispose();
        var process = DetachProcess();
        if (process != null)
        {
            _ = Task.Factory.StartNew(
                () => TerminateProcess(process),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private Process? DetachProcess()
    {
        lock (_processGate)
        {
            var process = _process;
            _process = null;
            return process;
        }
    }

    private void TerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Kill(entireProcessTree) 后进程退出是既定事实，短等待只为收敛句柄
                var killSw = System.Diagnostics.Stopwatch.StartNew();
                process.Kill(entireProcessTree: true);
                killSw.Stop();
                var waitSw = System.Diagnostics.Stopwatch.StartNew();
                bool exitedInTime = process.WaitForExit(1000);
                waitSw.Stop();
                if (!exitedInTime)
                    Log.Debug("{Tag} server process has not exited within 1s after kill", _logTag);
                Log.Information("{Tag} server stopped (kill={KillMs}ms, waitExit={WaitMs}ms, inTime={InTime})",
                    _logTag, killSw.ElapsedMilliseconds, waitSw.ElapsedMilliseconds, exitedInTime);
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
