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

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

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

    // 网易主链业务错误（code≠200，如 301 需要登录的鉴权抖动）是"没有答案"而非"确认
    // 无词"：返回 null 但不入负缓存，下次取词重试——与兜底搜索步的口径一致
    [Fact]
    public async Task Netease_BusinessError_ReturnsNull_NotCached_RetriedOnNextFetch()
    {
        var businessError = true;
        var (client, capture) = CreateClient(_ => businessError
            ? "{\"code\":301,\"msg\":\"需要登录\"}"
            : "{\"code\":200,\"lrc\":{\"lyric\":\"[00:05.00]恢复后的词\\n\"}}");
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None));

        businessError = false;
        var result = await service.GetLyricsAsync(NeteaseTrack(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(2, capture.Requests.Count); // 未入负缓存：第二次是真请求
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

    // 与失败相反的另一半语义：服务端明确回答"无词"（code:200 + 占位 lrc）是确认
    // 结果，仍入缓存。业务错误（code≠200）不再算确认无词，见 BusinessError 用例
    [Fact]
    public async Task ConfirmedNoLyrics_IsCached_SecondFetchMakesNoRequest()
    {
        var (client, capture) = CreateClient(_ =>
            "{\"code\":200,\"lrc\":{\"lyric\":\"[00:00.00]暂无歌词\\n\"}}");
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
        // 异常形状在 hash 搜索一步即失败：不烧预算做酷狗关键词兜底（仍会降级网易词库搜一次）
        Assert.Single(capture.Requests, r =>
            r.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal));
        Assert.Single(capture.Requests, r =>
            r.RequestUri!.AbsoluteUri.Contains("/search?keywords=", StringComparison.Ordinal));
        Assert.Equal(2, capture.Requests.Count);

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
    // 仍入缓存不重试（hash → title → title+artist 三步走完 + 网易兜底搜一次确认无词）
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

        Assert.Equal(3, capture.Requests.Count(r =>
            r.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)));
        // 第二次调用零请求 = 缓存命中（首调含一次网易兜底搜索，共 4 个请求）
        Assert.Equal(4, capture.Requests.Count);
    }

    // 酷狗上游持续异常形状（2026-09-23 升级后 status:1 歌曲搜索形状）时降级网易词库：
    // 按标题+歌手搜歌，时长 ±3s 内的同名候选被采纳取词，兜底结果按酷狗 SourceId 入缓存
    [Fact]
    public async Task Kugou_UpstreamBroken_NeteaseFallbackFindsLyrics_AndCachesUnderKugouId()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[]}}";
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return "{\"code\":200,\"result\":{\"songs\":[" +
                       "{\"id\":777,\"name\":\"晴天\",\"duration\":211000}," +
                       "{\"id\":888,\"name\":\"晴天\",\"duration\":60000}]}}";
            return "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]网易兜底的词\\n\"}}";
        });
        var service = new LyricService(client);

        var result = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("网易兜底的词", result!.Lines[0].Text);
        Assert.Contains("keywords=" + Uri.EscapeDataString("晴天 周杰伦"),
            capture.Requests[1].RequestUri!.Query);
        // 本曲的时长过滤由首位命中（777 时长 ±3s 内）走通；时长悬殊候选的拒绝
        // 负向锚定在 FallbackAllCandidatesMismatch（其候选全被时长过滤）
        Assert.Contains("id=777", capture.Requests[2].RequestUri!.AbsoluteUri);

        var second = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);
        Assert.Same(result, second);
        Assert.Equal(3, capture.Requests.Count); // 缓存命中：重播零请求
    }

    // 酷狗异常 + 网易兜底确认无命中候选：酷狗状态未知，整体按失败不入缓存，下次重试
    [Fact]
    public async Task Kugou_UpstreamBroken_FallbackAllCandidatesMismatch_NotCached()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":1,\"error_code\":0,\"data\":{\"lists\":[]}}";
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return "{\"code\":200,\"result\":{\"songs\":[" +
                       "{\"id\":888,\"name\":\"晴天\",\"duration\":60000}]}}"; // 同名但时长差 150s
            return "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]词\\n\"}}";
        });
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None));

        // 未入缓存：第二次调用重新发起酷狗搜索 + 网易兜底搜索
        Assert.Null(await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None));
        Assert.Equal(4, capture.Requests.Count);
    }

    // 酷狗异常 + 网易兜底传输失败：任一链路故障即不入负缓存，留给下次取词重试
    [Fact]
    public async Task Kugou_UpstreamBroken_FallbackTransportFailure_NotCached()
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler((request, _) =>
        {
            capture.Record(request);
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                throw new HttpRequestException("netease proxy down");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(url.Contains("/search/lyric", StringComparison.Ordinal)
                    ? "{\"status\":1,\"error_code\":0,\"data\":{}}"
                    : "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]词\\n\"}}")
            });
        });
        var service = new LyricService(new HttpClient(handler));

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));
        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));

        Assert.Equal(4, capture.Requests.Count); // 两次调用 ×（酷狗搜索 + 网易兜底搜索）
    }

    // 无标题的酷狗曲目（标题缺失无法构造搜索词）不走网易兜底，只尝试酷狗原生链
    [Fact]
    public async Task Kugou_NoTitle_UpstreamBroken_NoFallbackAttempted()
    {
        var (client, capture) = CreateClient(request =>
            request.RequestUri!.AbsoluteUri.Contains("/search/lyric", StringComparison.Ordinal)
                ? "{\"status\":1,\"error_code\":0,\"data\":{}}"
                : "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]词\\n\"}}");
        var service = new LyricService(client);
        var track = new Track { SourceId = "kugou:HASH01", Duration = TimeSpan.FromSeconds(210) };

        Assert.Null(await service.GetLyricsAsync(track, CancellationToken.None));

        Assert.Single(capture.Requests);
        Assert.Contains("/search/lyric", capture.Requests[0].RequestUri!.AbsoluteUri);
    }

    // 网易兜底搜索的业务错误（code≠200，鉴权失效/敏感词拦截）是"没有答案"而非
    // "确认无词"：按兜底失败处理不入负缓存——否则与酷狗确认无词叠加会把该曲整会话钉死
    [Fact]
    public async Task Kugou_FallbackSearchBusinessError_NotCached()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":404,\"candidates\":[]}"; // 酷狗全链确认无词
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return "{\"code\":301,\"msg\":\"需要登录\"}";
            return "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]词\\n\"}}";
        });
        var service = new LyricService(client);

        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));

        // 网易从未回答"无词"，不入缓存：第二次调用重新发起两链路请求
        Assert.Null(await service.GetLyricsAsync(KugouTrack(), CancellationToken.None));
        Assert.Equal(8, capture.Requests.Count); // 2 ×（酷狗 3 步 + 网易兜底搜索 1 次）
    }

    // 酷狗链路挂起（风控拖慢/网关抖动）耗尽共享 5s 预算触发内部超时后，兜底必须拿到
    // 独立新鲜预算——复用已取消的令牌会让 GetAsync 零请求即抛、兜底形同虚设，慢挂型
    // 故障永远到不了网易词库（修复前该场景 fallback 请求根本发不出去）
    [Fact]
    public async Task Kugou_HangsUntilSharedBudgetExhausted_FallbackFetchesWithFreshBudget()
    {
        var capture = new RequestCapture();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            capture.Record(request);
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                await Task.Delay(Timeout.Infinite, cancellationToken); // 挂起至共享预算耗尽
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return Ok("{\"code\":200,\"result\":{\"songs\":[" +
                          "{\"id\":777,\"name\":\"晴天\",\"duration\":211000}]}}");
            return Ok("{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]独立预算兜底的词\\n\"}}");
        });
        var service = new LyricService(new HttpClient(handler), fetchTimeout: TimeSpan.FromMilliseconds(400));

        var result = await service.GetLyricsAsync(KugouTrack(durationSec: 210), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("独立预算兜底的词", result!.Lines[0].Text);
        Assert.Contains("search?keywords=", capture.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Contains("id=777", capture.Requests[2].RequestUri!.AbsoluteUri);
    }

    // 兜底候选的歌手此前被整体忽略：同名异曲（谭咏麟/周华健《朋友》）只要时长在容忍
    // 窗口内即可能被采纳、错词整会话钉死缓存。双方歌手信息具备时须任一歌手归一化相等
    [Fact]
    public async Task Kugou_Fallback_SameTitleDifferentArtist_SkippedInFavorOfMatchingArtist()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":1,\"error_code\":0,\"data\":{}}";
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return "{\"code\":200,\"result\":{\"songs\":[" +
                       "{\"id\":888,\"name\":\"朋友\",\"duration\":270000,\"artists\":[{\"name\":\"周华健\"}]}," +
                       "{\"id\":999,\"name\":\"朋友\",\"duration\":271000,\"artists\":[{\"name\":\"谭咏麟\"}]}]}}";
            return "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]谭咏麟版歌词\\n\"}}";
        });
        var service = new LyricService(client);
        var track = new Track
        {
            SourceId = "kugou:HASH02",
            Title = "朋友",
            Artist = "谭咏麟",
            Duration = TimeSpan.FromSeconds(270)
        };

        var result = await service.GetLyricsAsync(track, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("谭咏麟版歌词", result!.Lines[0].Text);
        // 取词用的是歌手匹配的 999：同名异曲的 888 被跳过（两者时长都在 ±3s 窗口内）
        Assert.Contains("id=999", capture.Requests[2].RequestUri!.AbsoluteUri);
    }

    // 全部候选歌手不匹配 = 网易侧未给出可用答案，叠加酷狗故障整体按失败处理不入缓存
    [Fact]
    public async Task Kugou_Fallback_AllArtistsMismatch_NotCached()
    {
        var (client, capture) = CreateClient(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/lyric", StringComparison.Ordinal))
                return "{\"status\":1,\"error_code\":0,\"data\":{}}";
            if (url.Contains("/search?keywords=", StringComparison.Ordinal))
                return "{\"code\":200,\"result\":{\"songs\":[" +
                       "{\"id\":888,\"name\":\"朋友\",\"duration\":270000,\"artists\":[{\"name\":\"周华健\"}]}]}}";
            return "{\"code\":200,\"lrc\":{\"lyric\":\"[00:08.00]词\\n\"}}";
        });
        var service = new LyricService(client);
        var track = new Track
        {
            SourceId = "kugou:HASH02",
            Title = "朋友",
            Artist = "谭咏麟",
            Duration = TimeSpan.FromSeconds(270)
        };

        Assert.Null(await service.GetLyricsAsync(track, CancellationToken.None));

        // 未入缓存：第二次调用重新发起酷狗搜索 + 网易兜底搜索
        Assert.Null(await service.GetLyricsAsync(track, CancellationToken.None));
        Assert.Equal(4, capture.Requests.Count);
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
