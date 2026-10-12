using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// Broker 返回对象化回归：首播点经 ResolveTrackAsync 拿完整结果构造 Track
/// （播放头/声明值/新鲜度标记即时生效），新鲜解析跳过后台同源预刷新。
/// </summary>
public class FirstPlayResolutionTests
{
    private static ResolveTrackResult BuildResolution(
        string url = "https://203.0.113.50/stream.mp3",
        bool fellBack = false,
        PlaybackHeaders? headers = null,
        string? codec = "FLAC") => new(
        url,
        new ProviderTrackRef("stub", "1"),
        "stub",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        FellBack: fellBack,
        Codec: codec,
        BitrateKbps: 900,
        IsPreview: false,
        Headers: headers);

    [Fact]
    public void ToTrack_WithResolution_CarriesDeclaredInfoAndHeaders()
    {
        var headers = new PlaybackHeaders(Referer: new Uri("https://www.kuwo.cn/"));
        var online = new OnlineTrack { Id = "stub:1", Title = "S", Artist = "A", Source = "stub" };

        var track = online.ToTrack(BuildResolution(headers: headers));

        Assert.Equal("https://203.0.113.50/stream.mp3", track.FilePath);
        Assert.Equal("stub:1", track.SourceId);
        Assert.Equal("FLAC", track.DeclaredCodec);
        Assert.Equal(900, track.DeclaredBitrateKbps);
        Assert.Same(headers, track.PlaybackHeaders);
        Assert.NotNull(track.ResolvedAtUtc); // 非回退结果标记新鲜，播放时跳过后台预刷新
    }

    [Fact]
    public void ToTrack_WithFallbackResolution_DoesNotMarkFresh()
    {
        var online = new OnlineTrack { Id = "stub:1", Title = "S", Artist = "A", Source = "stub" };

        var track = online.ToTrack(BuildResolution(fellBack: true));

        // 回退结果无播放头（BuildResolveResult 回退路径不透传），不标记新鲜：
        // 保留后台刷新为回退首播补头，防"跳过刷新+回退无头"叠加
        Assert.Null(track.PlaybackHeaders);
        Assert.Null(track.ResolvedAtUtc);
    }

    [Fact]
    public async Task FreshlyResolvedOnlineTrack_SkipsBackgroundRefresh()
    {
        var service = new AudioService();
        try
        {
            var calls = await PlayAndCountResolverCallsAsync(service, DateTimeOffset.UtcNow);
            Assert.Equal(0, calls); // 刚解析的 URL 不再后台同源重解析
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task StaleOnlineTrack_TriggersBackgroundRefresh()
    {
        var service = new AudioService();
        try
        {
            var calls = await PlayAndCountResolverCallsAsync(service, DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.True(calls >= 1, $"后台预刷新未触发（resolver 调用 {calls} 次）");
        }
        finally
        {
            service.Dispose();
        }
    }

    private static async Task<int> PlayAndCountResolverCallsAsync(AudioService service, DateTimeOffset? resolvedAtUtc)
    {
        var calls = 0;
        service.SetTrackUrlResolver((track, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<TrackUrlResolution?>(
                new TrackUrlResolution("https://203.0.113.51/fresh.mp3", track.SourceId));
        });

        var track = new Track
        {
            Title = "T",
            SourceId = "stub:1",
            FilePath = "https://203.0.113.51/stale.mp3",
            ResolvedAtUtc = resolvedAtUtc
        };
        service.LoadTracks(new[] { track });
        service.PlayAtIndex(0);

        // 后台刷新 fire-and-forget：轮询窗口给足触发时间；新鲜用例同样等待以证明"未发生"
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (Volatile.Read(ref calls) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(300);
        return Volatile.Read(ref calls);
    }

    [Fact]
    public async Task ChatViewModel_SongRequest_FirstPlayTrackCarriesResolutionPayload()
    {
        var playlist = new List<Track>();
        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.TtsEnabled).Returns(false);
        djMock.SetupGet(x => x.CurrentEmotion).Returns("neutral");
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audioMock.Setup(x => x.TtsError).Returns(new Subject<string>());
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audioMock.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));
        var sttMock = new Mock<ISttService>();

        // 真实 Broker + 带声明值/播放头的 Provider；重定向探测注入即时 200 保持测试速度
        var headers = new PlaybackHeaders(UserAgent: PlaybackHeaderProfiles.DesktopBrowserUserAgent);
        var broker = new MusicSourceBroker(new PayloadProvider(headers))
        {
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new InstantOkHandler()))
        };

        var vm = new ChatViewModel(djMock.Object, audioMock.Object, broker, sttMock.Object);
        vm.InputText = "模特";
        await vm.SendMessageCommand.Execute().FirstAsync();

        var added = playlist.FirstOrDefault(t => t.SourceId == "stub:model");
        Assert.NotNull(added);
        Assert.Equal("FLAC", added.DeclaredCodec);
        Assert.Equal(900, added.DeclaredBitrateKbps);
        Assert.Same(headers, added.PlaybackHeaders);
        Assert.NotNull(added.ResolvedAtUtc);
    }

    private sealed class PayloadProvider(PlaybackHeaders headers) : IMusicProvider
    {
        public MusicProviderDescriptor Descriptor { get; } =
            new("stub", "Stub", ProviderNetworkScope.PublicInternet);

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<OnlineTrack>
            {
                new() { Id = "stub:model", Title = "模特", Artist = "李荣浩", Source = "stub" }
            });

        public Task<MediaResolutionResult> ResolveAsync(
            ProviderTrackRef trackRef,
            IReadOnlyDictionary<string, string>? providerMetadata,
            CancellationToken cancellationToken)
            => Task.FromResult(MediaResolutionResult.Playable(new ResolvedMedia(
                trackRef,
                new Uri("https://203.0.113.52/model.flac"),
                Codec: "FLAC",
                BitrateKbps: 900,
                Headers: headers)));
    }

    private sealed class InstantOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
