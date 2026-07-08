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
    private static HttpClient CreateMockHttpClient(string responseJson)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task ChatAsync_AllowsOllamaWithoutApiKey()
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
            Provider = "ollama",
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3",
            ApiKey = string.Empty
        });

        var result = await service.ChatAsync("你好", new List<ChatMessage>());

        Assert.Equal("本地模型回复", result);
    }
}
