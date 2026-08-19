using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MultiSourceMusicServiceTests
{
    [Fact]
    public async Task SearchAsync_CancellationStopsPrimaryRequest()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new MultiSourceMusicService(client);
        using var cancellation = new CancellationTokenSource();

        var searchTask = service.SearchAsync("测试", 5, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searchTask);
    }

    [Fact]
    public async Task GetPlayUrlAsync_UsesMetadataFallbackWhenPreferredSourceHasNoUrl()
    {
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("/song/url", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"code\":200,\"data\":[{\"url\":null}]}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            });
        }));
        var service = new MultiSourceMusicService(client, new FallbackMusicService());

        var url = await service.GetPlayUrlAsync(
            new OnlineTrack
            {
                Id = "netease:123",
                Title = "测试歌曲",
                Artist = "测试歌手"
            },
            CancellationToken.None);

        Assert.Equal("https://fallback.invalid/test.mp3", url);
    }

    [Fact]
    public async Task GetPlayUrlAsync_CallerCancellationStopsLegacySourceThatIgnoresToken()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0}")
            })));
        var service = new MultiSourceMusicService(
            client,
            new IgnoringCancellationMusicService());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetPlayUrlAsync("ignoringcancellation:123", cancellation.Token));
    }

    private sealed class FallbackMusicService : IMusicSearchService
    {
        public string Name => "备用音源";

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => Task.FromResult(new List<OnlineTrack>
            {
                new()
                {
                    Id = "fallback:456",
                    Title = "测试歌曲",
                    Artist = "测试歌手",
                    Source = Name
                }
            });

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>("https://fallback.invalid/test.mp3");
    }

    private sealed class IgnoringCancellationMusicService : IMusicSearchService
    {
        private readonly TaskCompletionSource<string?> _neverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "忽略取消的旧音源";

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => Task.FromResult(new List<OnlineTrack>());

        public Task<string?> GetPlayUrlAsync(string trackId)
            => _neverCompletes.Task;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
