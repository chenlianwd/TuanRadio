using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MinimaxServiceTests
{
    private static HttpClient CreateMockHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    [Fact]
    public void SetApiKey_StoresKey()
    {
        var httpClient = CreateMockHttpClient("{}");
        var service = new MinimaxService(httpClient);

        service.SetApiKey("test-api-key-12345");

        // SetApiKey doesn't throw = success
    }

    [Fact]
    public async Task ChatAsync_ReturnsEmptyOnInvalidResponse()
    {
        var httpClient = CreateMockHttpClient("{}");
        var service = new MinimaxService(httpClient);
        service.SetApiKey("test-key");

        var result = await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ChatAsync_ParsesChoicesCorrectly()
    {
        var responseJson = @"{
            ""choices"": [{
                ""message"": {
                    ""content"": ""测试回复[happy]""
                }
            }]
        }";
        var httpClient = CreateMockHttpClient(responseJson);
        var service = new MinimaxService(httpClient);
        service.SetApiKey("test-key");

        var result = await service.ChatAsync("你好", new List<ChatMessage>());

        Assert.Contains("测试回复", result);
    }

    [Fact]
    public async Task TextToSpeechAsync_ReturnsEmptyOnInvalidResponse()
    {
        var httpClient = CreateMockHttpClient("{}");
        var service = new MinimaxService(httpClient);
        service.SetApiKey("test-key");

        var result = await service.TextToSpeechAsync("测试", "female-shaonv", "happy");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateTrackIntroductionAsync_CallsChatAsync()
    {
        var responseJson = @"{
            ""choices"": [{
                ""message"": {
                    ""content"": ""即将播放下一首[happy]""
                }
            }]
        }";
        var httpClient = CreateMockHttpClient(responseJson);
        var service = new MinimaxService(httpClient);
        service.SetApiKey("test-key");

        var current = new Track { Title = "歌曲A", Artist = "歌手A" };
        var next = new Track { Title = "歌曲B", Artist = "歌手B" };

        var result = await service.GenerateTrackIntroductionAsync(current, next);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task ChatAsync_ThrowsOnNonSuccessStatus()
    {
        var httpClient = CreateMockHttpClient("error", HttpStatusCode.Unauthorized);
        var service = new MinimaxService(httpClient);
        service.SetApiKey("bad-key");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.ChatAsync("test", new List<ChatMessage>()));
    }
}
