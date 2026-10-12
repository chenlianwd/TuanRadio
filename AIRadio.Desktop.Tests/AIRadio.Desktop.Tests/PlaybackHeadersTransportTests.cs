using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// PlaybackHeaders 端到端传输回归：头值注入拒绝（CR/LF/控制字符/超长/Cookie 非 ASCII）、
/// 按源档案映射、Adapter 生产净化、Broker/缓存透传、ApplyTrackUrlResolution 回写 Track、
/// MediaHttpOptions 的 VLC 选项组装（消费点兜底跳过不安全头）。
/// </summary>
public class PlaybackHeadersTransportTests
{
    [Fact]
    public void IsSafeHeaderValue_RejectsControlCharsAndOversize()
    {
        Assert.True(PlaybackHeaders.IsSafeHeaderValue(null));
        Assert.True(PlaybackHeaders.IsSafeHeaderValue(""));
        Assert.True(PlaybackHeaders.IsSafeHeaderValue(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)"));
        // CR/LF/控制字符是头拆分与 VLC 选项注入的唯一可行向量，全部拒绝。
        // 注意用 \u 定长转义：\x 变长转义会贪婪吞并后续十六进制字符（"a\x7Fb" 实为 U+0F7B）
        Assert.False(PlaybackHeaders.IsSafeHeaderValue("a\rb"));
        Assert.False(PlaybackHeaders.IsSafeHeaderValue("a\nb"));
        Assert.False(PlaybackHeaders.IsSafeHeaderValue("a\u0001b"));
        Assert.False(PlaybackHeaders.IsSafeHeaderValue("a\u007Fb"));
        Assert.False(PlaybackHeaders.IsSafeHeaderValue(new string('a', PlaybackHeaders.MaxHeaderValueLength + 1)));
    }

    [Fact]
    public void Sanitized_DropsUnsafeValuesAndReturnsNullWhenAllDropped()
    {
        var mixed = new PlaybackHeaders(
            UserAgent: "Bad\nAgent",
            Referer: new Uri("https://www.kuwo.cn/")).Sanitized();
        Assert.NotNull(mixed);
        Assert.Null(mixed.UserAgent);
        Assert.Equal("https://www.kuwo.cn/", mixed.Referer!.OriginalString);

        // Cookie 额外约束可打印 ASCII：全角/中文 cookie 值整项剔除
        var badCookie = new PlaybackHeaders(Cookie: "k=值").Sanitized();
        Assert.Null(badCookie);

        var allBad = new PlaybackHeaders(UserAgent: "x\r\ny", Cookie: "k=1;z=\u0002").Sanitized();
        Assert.Null(allBad);
    }

    [Fact]
    public void Profiles_MatchSourceEvidence()
    {
        Assert.True(PlaybackHeaderProfiles.TryGet("kuwo", out var kuwo));
        Assert.Null(kuwo.UserAgent);
        Assert.Equal("https://www.kuwo.cn/", kuwo.Referer!.OriginalString);

        Assert.True(PlaybackHeaderProfiles.TryGet("migu", out var migu));
        Assert.Equal(PlaybackHeaderProfiles.DesktopBrowserUserAgent, migu.UserAgent);
        Assert.Equal("https://m.music.migu.cn/", migu.Referer!.OriginalString);

        Assert.True(PlaybackHeaderProfiles.TryGet("youtube", out var youtube));
        Assert.Equal(PlaybackHeaderProfiles.DesktopBrowserUserAgent, youtube.UserAgent);
        Assert.Null(youtube.Referer);

        // 网易/酷狗为签名 URL 鉴权，无播放头档案
        Assert.False(PlaybackHeaderProfiles.TryGet("netease", out _));
        Assert.False(PlaybackHeaderProfiles.TryGet("kugou", out _));
    }

    [Fact]
    public async Task Adapter_AttachesSanitizedHeadersToResolvedMedia()
    {
        var unsafeHeaders = new PlaybackHeaders(
            UserAgent: PlaybackHeaderProfiles.DesktopBrowserUserAgent,
            Referer: new Uri("https://www.kuwo.cn/"),
            Cookie: "bad\u001Fcookie");
        var adapter = new MusicSearchServiceAdapter(
            new StubSearchService("https://203.0.113.20/stream.mp3"),
            playbackHeaders: unsafeHeaders);

        var result = await adapter.ResolveAsync(new ProviderTrackRef("kuwo", "1"), null, CancellationToken.None);
        Assert.NotNull(result.Media);
        // Cookie 含控制字符被剔除，UA/Referer 安全保留
        var headers = result.Media!.Headers!;
        Assert.Equal(PlaybackHeaderProfiles.DesktopBrowserUserAgent, headers.UserAgent);
        Assert.Equal("https://www.kuwo.cn/", headers.Referer!.OriginalString);
        Assert.Null(headers.Cookie);

        // 无档案构造：Headers 为 null（网易/酷狗路径）
        var bare = new MusicSearchServiceAdapter(new StubSearchService("https://203.0.113.20/stream.mp3"));
        var bareResult = await bare.ResolveAsync(new ProviderTrackRef("netease", "1"), null, CancellationToken.None);
        Assert.Null(bareResult.Media!.Headers);
    }

    [Fact]
    public void MediaHttpOptions_MapsHeadersAndSkipsUnsafeValues()
    {
        var options = MediaHttpOptions.Build(new PlaybackHeaders(
            UserAgent: "UA/1.0",
            Referer: new Uri("https://www.kuwo.cn/"),
            Cookie: "a=1; b=2"));
        Assert.Equal(
            new[] { ":http-user-agent=UA/1.0", ":http-referrer=https://www.kuwo.cn/", ":http-cookies=a=1; b=2" },
            options);

        // 消费点兜底：不安全 UA 跳过，其余头不受影响；null 头集不产出选项
        var partial = MediaHttpOptions.Build(new PlaybackHeaders(
            UserAgent: "Bad\r\nUA",
            Referer: new Uri("https://www.kuwo.cn/")));
        Assert.Equal(new[] { ":http-referrer=https://www.kuwo.cn/" }, partial);
        Assert.Empty(MediaHttpOptions.Build(null));
        Assert.Empty(MediaHttpOptions.Build(new PlaybackHeaders()));
    }

    [Fact]
    public void ApplyTrackUrlResolution_WritesPlaybackHeadersOntoTrack()
    {
        var track = new Track { FilePath = "https://203.0.113.30/old.mp3", SourceId = "kuwo:1" };
        var headers = new PlaybackHeaders(Referer: new Uri("https://www.kuwo.cn/"));
        var method = typeof(AudioService).GetMethod(
            "ApplyTrackUrlResolution", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, new object?[]
        {
            track,
            new TrackUrlResolution("https://203.0.113.30/new.mp3", "kuwo:1", Headers: headers)
        });

        Assert.Equal(headers, track.PlaybackHeaders);
    }

    private sealed class StubSearchService(string playUrl) : IMusicSearchService
    {
        public string Name => "stub";
        public bool IsSlowSource => false;

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20)
            => Task.FromResult(new List<OnlineTrack>());

        public Task<string?> GetPlayUrlAsync(string trackId)
            => Task.FromResult<string?>(playUrl);
    }
}
