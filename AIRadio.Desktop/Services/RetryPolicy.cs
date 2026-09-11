using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public static class RetryPolicy
{
    /// <summary>
    /// 无 token 重载仅供测试/无取消场景使用：内部固定 CancellationToken.None，
    /// 动作抛出的任何 OperationCanceledException（含调用方真实取消的穿透）都会被
    /// 判为瞬态而重试，且退避 Delay 不可取消。生产调用必须用带 token 的重载。
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxRetries = 3,
        int baseDelayMs = 1000)
        => await ExecuteAsync(
            _ => action(),
            CancellationToken.None,
            maxRetries,
            baseDelayMs);

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        int maxRetries = 3,
        int baseDelayMs = 1000)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                attempt++;
                var delay = baseDelayMs * Math.Pow(2, attempt - 1);
                Log.Warning(ex, "Retry {Attempt}/{Max} after {Delay}ms", attempt, maxRetries, delay);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
        }
    }

    // HttpClient 超时表现为 TaskCanceledException（派生自 OCE）——是最常见的瞬态网络
    // 故障之一；用户主动取消（外部 token 已触发）绝不能重试
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException or TimeoutException ||
           (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>同上：无 token 的 void 重载，仅供测试/无取消场景，生产调用用带 token 的重载。</summary>
    public static async Task ExecuteAsync(
        Func<Task> action,
        int maxRetries = 3,
        int baseDelayMs = 1000)
    {
        await ExecuteAsync(async () =>
        {
            await action();
            return true;
        }, maxRetries, baseDelayMs);
    }
}
