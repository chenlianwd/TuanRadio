using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public static class RetryPolicy
{
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
