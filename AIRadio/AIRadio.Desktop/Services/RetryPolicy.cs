using System;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;

namespace AIRadio.Desktop.Services;

public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxRetries = 3,
        int baseDelayMs = 1000)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                attempt++;
                var delay = baseDelayMs * Math.Pow(2, attempt - 1);
                Log.Warning(ex, "Retry {Attempt}/{Max} after {Delay}ms", attempt, maxRetries, delay);
                await Task.Delay(TimeSpan.FromMilliseconds(delay));
            }
        }
    }

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
