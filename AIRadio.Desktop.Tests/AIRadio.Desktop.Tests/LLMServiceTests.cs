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

public class LLMServiceTests
{
    private static HttpClient CreateMockHttpClient(string responseJson, Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                 "SendAsync",
                 ItExpr.IsAny<HttpRequestMessage>(),
                 ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => onRequest?.Invoke(request))
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task ChatAsync_AllowsLocalModelWithoutApiKey()
    {
        var responseJson = @"{
            ""choices"": [{
                ""message"": {
                    ""content"": ""本地模型回复""
                }
            }]
        }";
        var service = new LLMService(CreateMockHttpClient(responseJson));
        service.Configure(new LLMConfig
        {
            Provider = "local",
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3",
            ApiKey = string.Empty
        });

        var result = await service.ChatAsync("你好", new List<ChatMessage>());

        Assert.Equal("本地模型回复", result);
    }

    [Theory]
    [InlineData("https://api.kimi.com/coding/", "https://api.kimi.com/coding/v1/messages")]
    [InlineData("https://proxy.example/v1/", "https://proxy.example/v1/messages")]
    [InlineData("https://proxy.example/v1/messages", "https://proxy.example/v1/messages")]
    public async Task ChatAsync_AnthropicFormat_UsesNormalizedMessagesEndpointAndApiKeyHeader(
        string baseUrl,
        string expectedEndpoint)
    {
        HttpRequestMessage? capturedRequest = null;
        var service = new LLMService(CreateMockHttpClient(
            """{"content":[{"text":"ok"}]}""",
            request => capturedRequest = request));
        service.Configure(new LLMConfig
        {
            Provider = "anthropic",
            BaseUrl = baseUrl,
            Model = "claude-test",
            ApiKey = "  test-key  "
        });

        var result = await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.Equal("ok", result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(expectedEndpoint, capturedRequest!.RequestUri!.ToString());
        Assert.Null(capturedRequest.Headers.Authorization);
        Assert.Contains("test-key", capturedRequest.Headers.GetValues("x-api-key"));
    }
}
