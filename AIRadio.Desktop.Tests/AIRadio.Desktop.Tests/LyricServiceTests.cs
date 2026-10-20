using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 歌词取词链路：网易单步 /lyric、酷狗 /search/lyric + /lyric 两步与关键词兜底、
/// 三态契约（null / IsInstrumental / 有词）、前缀分发不发网络、会话级缓存、
/// 上游异常形状（非 200/404）按失败处理不入负缓存。
/// </summary>
public class LyricServiceTests
{
    private static (HttpClient client, RequestCapture capture) CreateClient(
        Func<HttpRequestMessage, string> respond)
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler((request, _) =>
        {
            capture.Record(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request))
            });
        });
        return (new HttpClient(handler), capture);
    }

    private static Track NeteaseTrack()
        => new() { SourceId = "netease:123", Title = "歌", Artist = "手" };

    private static Track KugouTrack(int durationSec = 210)
        => new()
        {
            SourceId = "kugou:HASH01",
            Title = "晴天",
            Artist = "周杰伦",
            Duration = TimeSpan.FromSeconds(durationSec)
        };

    [Fact]
    public async Task Netease_HappyPath_RequestsSongIdAndParsesLrc()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"code\":200,\"lrc\":{\"version\":1,\"lyric\":\"[00:05.00]第一句\\n[00:10.00]第二句\\n\"}}");
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsInstrumental);
        Assert.Equal(2, result.Lines.Count);
        Assert.Contains("/lyric?id=123", capture.Last!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Netease_PureMusicWithEmbeddedLines_ShowsLines()
    {
        var (client, _) = CreateClient(_ =>
            "{\"code\":200,\"pureMusic\":true,\"lrc\":{\"lyric\":\"[00:00.00] 作曲 : 某某\\n[00:05.00]纯音乐，请欣赏\\n\"}}");
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsInstrumental);
        Assert.Equal(2, result.Lines.Count);
    }

    [Fact]
    public async Task Netease_PureMusicFlagWithoutLines_ReturnsInstrumental()
    {
        var (client, _) = CreateClient(_ => "{\"code\":200,\"pureMusic\":true,\"lrc\":{\"lyric\":\"\"}}");
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.IsInstrumental);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task Netease_UncollectedPlaceholderOnly_ReturnsNull()
    {
        var (client, _) = CreateClient(_ =>
            "{\"code\":200,\"uncollected\":true,\"lrc\":{\"lyric\":\"[00:00.00]暂无歌词\\n\"}}");
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Netease_NonSuccessCode_ReturnsNull()
    {
        var (client, _) = CreateClient(_ => "{\"code\":404}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));
    }

    [Fact]
    public async Task Kugou_HashCandidateTwoStep_SucceedsWithDecodeParams()
    {
        var (client, capture) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)
                ? "{\"status\":200,\"candidates\":[{\"id\":\"ID9\",\"accesskey\":\"KEY9\",\"duration\":210000}]}"
                : "{\"status\":200,\"content\":\"YmFzZTY0\",\"contenttype\":1,\"decodeContent\":\"[00:08.00]酷狗第一句\\n\"}");
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Lines);
        Assert.Equal("酷狗第一句", result.Lines[0].Text);
        var lyricUrl = capture.Requests.Last().RequestUri!.AbsoluteUri;
        Assert.Contains("/lyric?id=ID9&accesskey=KEY9&fmt=lrc&decode=1", lyricUrl);
    }

    [Fact]
    public async Task Kugou_HashMiss_FallsBackToTitleOnlyKeywords()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]关键词命中的词\\n\"}";
            return url.Contains("hash=HASH01", StringComparison.Ordinal)
                ? "{\"status\":404,\"candidates\":[]}"
                : "{\"status\":200,\"candidates\":[{\"id\":\"ID1\",\"accesskey\":\"KEY1\"}]}";
        });
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("关键词命中的词", result!.Lines[0].Text);
        Assert.Contains("keywords=", capture.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Contains("duration=210000", capture.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Kugou_TitleOnlyMiss_TriesTitlePlusArtistThenGivesUp()
    {
        var (client, capture) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)
                ? "{\"status\":404,\"candidates\":[]}"
                : "{\"status\":200,\"decodeContent\":\"词\"}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));

        Assert.Equal(3, capture.Requests.Count(r => r.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)));
        var keywords = capture.Requests
            .Where(r => r.RequestUri!.AbsoluteUri.Contains("keywords=", StringComparison.Ordinal))
            .Select(r => r.RequestUri!.Query)
            .ToList();
        Assert.Contains(keywords, q => q.Contains(Uri.EscapeDataString("晴天")) && !q.Contains(Uri.EscapeDataString("周杰伦")));
        Assert.Contains(keywords, q => q.Contains(Uri.EscapeDataString("晴天 周杰伦")));
    }

    [Fact]
    public async Task Kugou_CandidateSelection_PrefersDurationWithinTolerance()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!url.Contains("/search/lyric", StringComparison.Ordinal))
            {
                // 断言取词用的是时长匹配的那个候选
                Assert.Contains("id=ID2", url);
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]正确的词\\n\"}";
            }
            return "{\"status\":200,\"candidates\":[" +
                   "{\"id\":\"ID1\",\"accesskey\":\"K1\",\"duration\":60000}," +
                   "{\"id\":\"ID2\",\"accesskey\":\"K2\",\"duration\":209000}," +
                   "{\"id\":\"ID3\",\"accesskey\":\"K3\",\"duration\":300000}]}";
        });
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("正确的词", result!.Lines[0].Text);
    }

    [Fact]
    public async Task Kugou_DurationMatchWithoutAccesskey_SkipsToNextUsableCandidate()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!url.Contains("/search/lyric", StringComparison.Ordinal))
            {
                Assert.Contains("id=ID2", url);
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]第二个候选\\n\"}";
            }
            return "{\"status\":200,\"candidates\":[" +
                   "{\"id\":\"ID1\",\"duration\":210000}," + // 时长命中但缺 accesskey
                   "{\"id\":\"ID2\",\"accesskey\":\"K2\",\"duration\":211000}]}";
        });
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("第二个候选", result!.Lines[0].Text);
        // 跳过坏候选后正常两步完成，没有再触发关键词兜底
        Assert.Equal(1, capture.Requests.Count(r =>
            r.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Kugou_NoDurationMatch_FallsBackToFirstCandidate()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!url.Contains("/search/lyric", StringComparison.Ordinal))
            {
                Assert.Contains("id=ID1", url);
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]第一个候选\\n\"}";
            }
            return "{\"status\":200,\"candidates\":[{\"id\":\"ID1\",\"accesskey\":\"K1\",\"duration\":60000}]}";
        });
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("第一个候选", result!.Lines[0].Text);
    }

    [Fact]
    public async Task Kugou_DecodeContentPlaceholder_ReturnsNull()
    {
        var (client, _) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)
                ? "{\"status\":200,\"candidates\":[{\"id\":\"ID1\",\"accesskey\":\"K1\"}]}"
                : "{\"status\":200,\"decodeContent\":\"[00:00.00]暂无歌词\\n\"}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownPrefixOrNullSourceId_MakesNoNetworkRequest()
    {
        var (client, capture) = CreateClient(_ => "{\"status\":200}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(new Track { SourceId = "youtube:abc" }, CancellationToken.None));
        Assert.Null(await service.GetLyricsAsync(new Track { SourceId = null, FilePath = "C:/a.mp3" }, CancellationToken.None));
        Assert.Null(await service.GetLyricsAsync(new Track { SourceId = "无前缀" }, CancellationToken.None));
        Assert.Empty(capture.Requests);
    }

    [Fact]
    public async Task Cache_SecondFetchForSameSourceId_HitsCacheWithoutNewRequest()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"code\":200,\"lrc\":{\"lyric\":\"[00:05.00]句\\n\"}}");
        var service = new LyricService(client);

        var first = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);
        var second = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(capture.Requests);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNullWithoutThrowing()
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler((request, _) =>
        {
            capture.Record(request);
            throw new HttpRequestException("proxy down");
        });
        var service = new LyricService(new HttpClient(handler));

        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));
        Assert.Single(capture.Requests);
    }

    // 瞬时失败（代理冷启动/连接拒绝/内部超时）不得写入无 TTL 负缓存：否则启动首播
    // 曲目的歌词整会话恒空（Node 代理 14s 冷启动 > 5s 取词预算必踩），下次取词应重试
    [Fact]
    public async Task TransientFailure_DoesNotEnterNegativeCache_RetriedOnNextFetch()
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler((request, _) =>
        {
            capture.Record(request);
            if (capture.Requests.Count == 1)
                throw new HttpRequestException("proxy cold start");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":200,\"lrc\":{\"lyric\":\"[00:05.00]句\\n\"}}")
            });
        });
        var service = new LyricService(new HttpClient(handler));

        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));

        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(2, capture.Requests.Count);
    }

    // 与失败相反的另一半语义：服务端明确回答"无词"（code≠200）是确认结果，仍入缓存
    [Fact]
    public async Task ConfirmedNoLyrics_IsCached_SecondFetchMakesNoRequest()
    {
        var (client, capture) = CreateClient(_ => "{\"code\":404}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));
        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));

        Assert.Single(capture.Requests);
    }

    [Fact]
    public async Task CallerCancellation_ThrowsOperationCanceledAndDoesNotPolluteCache()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"code\":200,\"lrc\":{\"lyric\":\"[00:05.00]句\\n\"}}");
        var service = new LyricService(client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 外部取消必须透传（应用关闭链路依赖它中断在途请求）
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetLyricsAsync(NeteaseTrack(), cts.Token));

        // 取消的结果不得写入缓存：重新取词应真正发起请求并成功（若缓存了 null，这里会拿 null 且零请求）
        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Single(capture.Requests);
    }

    // 上游异常形状（非 200/404；实机观测网关抖动窗口对歌词搜索返回 status:1 的歌曲搜索
    // 形状响应）必须按"获取失败"处理：不入无 TTL 负缓存，下次取词重试——否则一次抖动
    // 就让该曲目整会话恒"暂无歌词"。异常时也不再烧预算做关键词兜底（抖动期兜底同样拿
    // 不到有效响应），失败立即上抛
    [Fact]
    public async Task Kugou_AnomalousSearchStatus_NotNegativeCached_RetriedOnNextFetch()
    {
        var anomalous = true;
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]恢复后的词\\n\"}";
            if (anomalous)
            {
                anomalous = false;
                return "{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[{\"FileName\":\"x\"}]}}";
            }
            return "{\"status\":200,\"candidates\":[{\"id\":\"ID1\",\"accesskey\":\"K1\"}]}";
        });
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));
        // 异常形状在 hash 搜索一步即失败：不烧预算做关键词兜底
        Assert.Single(capture.Requests);

        var result = await service.GetLyricsAsync(KugouTrack(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("恢复后的词", result!.Lines[0].Text);
    }

    [Fact]
    public async Task Kugou_AnomalousDownloadStatus_NotNegativeCached_RetriedOnNextFetch()
    {
        var anomalous = true;
        var (client, _) = CreateClient(request =>
        {
            if (!request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal))
            {
                if (anomalous)
                {
                    anomalous = false;
                    return "{\"status\":1,\"error_code\":0,\"data\":{}}";
                }
                return "{\"status\":200,\"decodeContent\":\"[00:08.00]恢复后的词\\n\"}";
            }
            return "{\"status\":200,\"candidates\":[{\"id\":\"ID1\",\"accesskey\":\"K1\"}]}";
        });
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));

        var result = await service.GetLyricsAsync(KugouTrack(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("恢复后的词", result!.Lines[0].Text);
    }

    // 与异常形状相反的另一半语义：全链 404 是服务端明确回答"无匹配"的确认结果，
    // 仍入缓存不重试（hash → title → title+artist 三步走完）
    [Fact]
    public async Task Kugou_AllSearch404_IsConfirmed_CachedWithoutRetry()
    {
        var (client, capture) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)
                ? "{\"status\":404,\"candidates\":[]}"
                : "{\"status\":200,\"decodeContent\":\"词\"}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));
        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));

        Assert.Equal(3, capture.Requests.Count);
    }

    private sealed class RequestCapture
    {
        private readonly List<HttpRequestMessage> _requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public HttpRequestMessage? Last => _requests.LastOrDefault();

        public void Record(HttpRequestMessage request)
        {
            lock (_requests)
                _requests.Add(request);
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 与真实传输层同口径：令牌已取消时发请求前即抛 OCE
            cancellationToken.ThrowIfCancellationRequested();
            return _handler(request, cancellationToken);
        }
    }
}
