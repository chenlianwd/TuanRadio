using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class PodcastRssServiceTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int RequestCount { get; private set; }

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void ParseFeedXml_ParsesRss20_StripsHtmlAndParsesDurations()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>极客FM</title>
                <itunes:author>主播A</itunes:author>
                <description><![CDATA[<p>科技与<b>生活</b></p>]]></description>
                <item>
                  <title>EP 101: AI 电台架构</title>
                  <guid>ep-101</guid>
                  <pubDate>Tue, 10 Nov 2026 08:00:00 +0800</pubDate>
                  <itunes:duration>01:02:30</itunes:duration>
                  <content:encoded><![CDATA[<p>第一段内容。</p><p>第二段 <strong>重点</strong> &amp; 笔记。</p>]]></content:encoded>
                  <enclosure url="https://cdn.example.com/audio/ep101.mp3" type="audio/mpeg" length="123456"/>
                </item>
                <item>
                  <title>EP 100: 秒数时长测试</title>
                  <guid>ep-100</guid>
                  <itunes:duration>2720</itunes:duration>
                  <itunes:summary>简短摘要</itunes:summary>
                  <enclosure url="https://cdn.example.com/audio/ep100.m4a" type="audio/mp4"/>
                </item>
                <item>
                  <title>恶意本地回环附件应被跳过</title>
                  <guid>ep-bad</guid>
                  <enclosure url="http://127.0.0.1:8080/evil.mp3" type="audio/mpeg"/>
                </item>
              </channel>
            </rss>
            """;

        var result = PodcastRssService.ParseFeedXml(xml, "https://example.com/rss.xml");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Feed);
        Assert.Equal("极客FM", result.Feed!.Title);
        Assert.Equal("主播A", result.Feed.Author);
        Assert.Equal("科技与 生活", result.Feed.Description);
        Assert.Equal(2, result.Episodes.Count);

        var ep1 = result.Episodes[0];
        Assert.Equal("EP 101: AI 电台架构", ep1.Title);
        Assert.Equal(new TimeSpan(1, 2, 30), ep1.Duration);
        Assert.Equal("MP3", ep1.Codec);
        Assert.Contains("第一段内容。", ep1.Summary);
        Assert.Contains("重点 & 笔记。", ep1.Summary);
        Assert.DoesNotContain("<p>", ep1.Summary);

        var ep2 = result.Episodes[1];
        Assert.Equal(TimeSpan.FromSeconds(2720), ep2.Duration);
        Assert.Equal("AAC", ep2.Codec);
    }

    [Fact]
    public void ParseFeedXml_ParsesAtomFeed()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Tech Talk</title>
              <author><name>Host B</name></author>
              <entry>
                <id>atom-1</id>
                <title>Episode One</title>
                <updated>2026-11-11T12:00:00Z</updated>
                <summary>Atom summary text.</summary>
                <link rel="enclosure" href="https://cdn.example.com/ep1.ogg" type="audio/ogg"/>
              </entry>
            </feed>
            """;

        var result = PodcastRssService.ParseFeedXml(xml, "https://example.com/atom.xml");

        Assert.True(result.IsSuccess);
        Assert.Equal("Atom Tech Talk", result.Feed!.Title);
        Assert.Single(result.Episodes);
        Assert.Equal("Episode One", result.Episodes[0].Title);
        Assert.Equal("OGG", result.Episodes[0].Codec);
        Assert.Equal("Atom summary text.", result.Episodes[0].Summary);
    }

    [Fact]
    public void ParseFeedXml_RejectsDtdXxePayload()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [
              <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">
            ]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;

        var result = PodcastRssService.ParseFeedXml(xxe, "https://example.com/xxe.xml");

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Episodes);
    }

    [Theory]
    [InlineData("http://127.0.0.1/feed.xml")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost:3000/feed.xml")]
    [InlineData("ftp://example.com/feed.xml")]
    public async Task FetchFeedAsync_BlocksLoopbackMetadataAndUnsafeSchemes(string badUrl)
    {
        using var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Should not issue HTTP request"));
        using var client = new HttpClient(handler);
        using var service = new PodcastRssService(client);

        var result = await service.FetchFeedAsync(badUrl);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FetchFeedAsync_BlocksUnsafeRedirectTarget()
    {
        using var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://public.example.com/rss")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
                resp.Headers.Location = new Uri("http://127.0.0.1:37250/internal.xml");
                return resp;
            }
            throw new InvalidOperationException("Must not follow redirect to loopback");
        });
        using var client = new HttpClient(handler);
        using var service = new PodcastRssService(client, uriValidator: MediaUriPolicy.ValidateCustomRadioAsync);

        // 注入仅允许 public.example.com 的测试解析器，避免单元测试依赖真实公网 DNS
        using var deterministicService = new PodcastRssService(
            client,
            uriValidator: (uri, ct) => uri.Host == "public.example.com"
                ? Task.FromResult(true)
                : MediaUriPolicy.ValidateCustomRadioAsync(uri, ct));

        var result = await deterministicService.FetchFeedAsync("https://public.example.com/rss");

        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchFeedAsync_CachesResultsAndSupportsForceRefresh()
    {
        var rssXml = """
            <rss version="2.0"><channel><title>Cache Test</title>
              <item><title>Ep 1</title><enclosure url="https://cdn.example.com/1.mp3" type="audio/mpeg"/></item>
            </channel></rss>
            """;

        using var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rssXml, Encoding.UTF8, "application/rss+xml")
            });
        using var client = new HttpClient(handler);
        using var service = new PodcastRssService(client, (_, _) => Task.FromResult(true));

        var first = await service.FetchFeedAsync("https://example.com/rss");
        var second = await service.FetchFeedAsync("https://example.com/rss");
        var forced = await service.FetchFeedAsync("https://example.com/rss", forceRefresh: true);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(forced.IsSuccess);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchFeedAsync_RejectsResponseExceedingMaxBytes()
    {
        var oversized = new byte[PodcastRssService.MaxFeedBytes + 1024];
        using var handler = new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversized)
            });
        using var client = new HttpClient(handler);
        using var service = new PodcastRssService(client, (_, _) => Task.FromResult(true));

        var result = await service.FetchFeedAsync("https://example.com/huge.xml");

        Assert.False(result.IsSuccess);
    }
}
