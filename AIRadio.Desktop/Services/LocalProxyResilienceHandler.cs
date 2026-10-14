using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 本地音乐代理（127.0.0.1:37250/37251）的连接级自愈。代理进程死亡——典型为启动时
/// 复用的孤儿代理在会话中途退出，或自有进程崩溃——会让请求以"连接被拒绝/重置"失败，
/// 且 StartAsync 只在应用启动时执行一次，整个会话将永久失联。此 handler 挂在共享
/// HttpClient 上：捕获本地代理端口的连接级失败后，先触发对应 MusicApiServer 重新拉起
/// 再重试一次；重试仍连接失败则进入短冷却，冷却期内直接失败，避免每次请求都付出
/// 完整的拉起等待。仅重放 GET/HEAD——带请求体的请求无法安全重发。
/// </summary>
internal sealed class LocalProxyResilienceHandler : DelegatingHandler
{
    private static readonly TimeSpan DefaultResurrectCooldown = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyCollection<int> _proxyPorts;
    private readonly Func<int, CancellationToken, Task> _resurrectAsync;
    private readonly CancellationToken _appLifetime;
    private readonly TimeSpan _resurrectCooldown;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _cooldownUntil = new();

    public LocalProxyResilienceHandler(
        IReadOnlyCollection<int> proxyPorts,
        Func<int, CancellationToken, Task> resurrectAsync,
        CancellationToken appLifetime,
        HttpMessageHandler innerHandler,
        TimeSpan? resurrectCooldown = null)
    {
        _proxyPorts = proxyPorts;
        _resurrectAsync = resurrectAsync;
        _appLifetime = appLifetime;
        _resurrectCooldown = resurrectCooldown ?? DefaultResurrectCooldown;
        InnerHandler = innerHandler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var port = request.RequestUri?.Port ?? -1;
        if (!_proxyPorts.Contains(port) || !IsReplayable(request))
            return await base.SendAsync(request, cancellationToken);

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex) when (LocalProxyTransport.IsConnectFailure(ex) && CanResurrect(port, cancellationToken))
        {
            Log.Information(
                "Local music proxy on port {Port} is unreachable; restarting it and retrying once", port);
            try
            {
                // 用应用级令牌而非请求令牌：请求被取消也不应中止已开始的拉起
                await _resurrectAsync(port, _appLifetime);
            }
            catch (Exception resurrectEx)
            {
                Log.Debug(resurrectEx, "Music proxy resurrect attempt failed for port {Port}", port);
            }

            // 拉起期间请求令牌可能已被取消（HttpClient 超时/消费方预算）：已取消的令牌上重试
            // 只会立刻抛 TaskCanceledException，回抛原始连接失败让消费方按"代理不可达"归类
            if (cancellationToken.IsCancellationRequested)
            {
                _cooldownUntil[port] = DateTimeOffset.UtcNow + _resurrectCooldown;
                throw;
            }

            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                _cooldownUntil.TryRemove(port, out _);
                return response;
            }
            catch (HttpRequestException retryEx) when (LocalProxyTransport.IsConnectFailure(retryEx))
            {
                _cooldownUntil[port] = DateTimeOffset.UtcNow + _resurrectCooldown;
                throw;
            }
        }
    }

    private bool CanResurrect(int port, CancellationToken requestCancellationToken)
        => !requestCancellationToken.IsCancellationRequested
           && !_appLifetime.IsCancellationRequested
           && (!_cooldownUntil.TryGetValue(port, out var until) || DateTimeOffset.UtcNow >= until);

    private static bool IsReplayable(HttpRequestMessage request)
        => request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
}
