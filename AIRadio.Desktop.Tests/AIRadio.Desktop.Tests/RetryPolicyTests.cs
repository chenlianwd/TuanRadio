using System;
using System.Net.Http;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsResultOnFirstSuccess()
    {
        var result = await RetryPolicy.ExecuteAsync(() => Task.FromResult(42), maxRetries: 3, baseDelayMs: 1);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesOnHttpRequestException()
    {
        int attempts = 0;
        var result = await RetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new HttpRequestException("fail");
            return Task.FromResult("ok");
        }, maxRetries: 3, baseDelayMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsAfterMaxRetries()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryPolicy.ExecuteAsync<string>(() =>
            {
                attempts++;
                throw new HttpRequestException("always fail");
            }, maxRetries: 2, baseDelayMs: 1));

        Assert.Equal(3, attempts); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryNonHttpRequestException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryPolicy.ExecuteAsync<string>(() =>
                throw new InvalidOperationException("not http"), maxRetries: 3, baseDelayMs: 1));
    }

    [Fact]
    public async Task ExecuteAsync_VoidOverload_Works()
    {
        int attempts = 0;
        await RetryPolicy.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 2)
                throw new HttpRequestException("fail");
            return Task.CompletedTask;
        }, maxRetries: 3, baseDelayMs: 1);

        Assert.Equal(2, attempts);
    }
}
