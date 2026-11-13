using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class StreamProbeServiceTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    [Fact]
    public async Task ProbeAsync_ExtractsIcyHeadersAndContentType()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.True(req.Headers.Contains("Icy-MetaData"));

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("icy-name", "Midnight Lofi &amp; Chill");
            response.Headers.TryAddWithoutValidation("icy-br", "256");
            response.Headers.TryAddWithoutValidation("icy-genre", "Lofi, Ambient");
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);

        var result = await svc.ProbeAsync("http://192.168.1.100:8000/stream");

        Assert.True(result.Success);
        Assert.Equal("Midnight Lofi & Chill", result.StationName);
        Assert.Equal("MP3", result.Codec);
        Assert.Equal(256, result.BitrateKbps);
        Assert.Equal("Lofi, Ambient", result.Tags);
        Assert.False(result.LooksLikeStaticFile);
    }

    [Fact]
    public async Task ProbeAsync_AllowsPrivateLanIp_AndBlocksLoopbackOrMetadata()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/aac");
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);

        var lanResult = await svc.ProbeAsync("http://192.168.1.50:8000/nas.aac");
        Assert.True(lanResult.Success);
        Assert.Equal("AAC", lanResult.Codec);

        var loopbackResult = await svc.ProbeAsync("http://127.0.0.1:8000/stream");
        Assert.False(loopbackResult.Success);

        var localhostResult = await svc.ProbeAsync("http://localhost:8000/stream");
        Assert.False(localhostResult.Success);

        var metadataResult = await svc.ProbeAsync("http://169.254.169.254/latest/meta-data");
        Assert.False(metadataResult.Success);

        var fileResult = await svc.ProbeAsync("file:///C:/music/test.mp3");
        Assert.False(fileResult.Success);
    }

    [Fact]
    public async Task ProbeAsync_FlagsStaticAudioFileWithContentLength()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            response.Content.Headers.ContentLength = 4_200_000;
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);

        var result = await svc.ProbeAsync("http://10.0.0.8/podcast.mp3");

        Assert.True(result.Success);
        Assert.True(result.LooksLikeStaticFile);
        Assert.Equal("MP3", result.Codec);
    }

    [Fact]
    public async Task ProbeAsync_FallsBackGracefullyOnProtocolViolationAndInfersCodecFromUrl()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("The response ended prematurely (ICY 200 OK)"));

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);

        var result = await svc.ProbeAsync("http://192.168.1.20:8000/radio.aacp");

        Assert.False(result.Success);
        Assert.Equal("AAC", result.Codec);
        Assert.NotNull(result.ErrorMessage);
    }

    [Theory]
    [InlineData("128, 128", 128)]
    [InlineData("192 kbps", 192)]
    [InlineData("invalid", null)]
    public async Task ProbeAsync_ParsesVariousIcyBitrateFormats(string rawBitrate, int? expected)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Headers.TryAddWithoutValidation("icy-br", rawBitrate);
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        using var svc = new RadioStreamProbeService(client);

        var result = await svc.ProbeAsync("http://192.168.1.10:8000/live");
        Assert.True(result.Success);
        Assert.Equal(expected, result.BitrateKbps);
    }
}
