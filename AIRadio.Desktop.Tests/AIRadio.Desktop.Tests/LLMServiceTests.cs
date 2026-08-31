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
            """{"content":[{"type":"thinking","thinking":"分析中"},{"type":"text","text":"第一段"},{"type":"text","text":"第二段"}]}""",
            request => capturedRequest = request));
        service.Configure(new LLMConfig
        {
            Provider = "anthropic",
            BaseUrl = baseUrl,
            Model = "claude-test",
            ApiKey = "  test-key  "
        });

        var result = await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.Equal("第一段\n第二段", result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(expectedEndpoint, capturedRequest!.RequestUri!.ToString());
        Assert.Null(capturedRequest.Headers.Authorization);
        Assert.Contains("test-key", capturedRequest.Headers.GetValues("x-api-key"));
    }

    [Fact]
    public async Task ChatAsync_AnthropicFormat_SkipsThinkingBlocksThatUseTextField()
    {
        var service = new LLMService(CreateMockHttpClient(
            """{"content":[{"type":"thinking","text":"内部推理"},{"type":"text","text":"正文"}]}"""));
        service.Configure(new LLMConfig
        {
            Provider = "anthropic",
            BaseUrl = "https://proxy.example/v1",
            Model = "claude-test",
            ApiKey = "k"
        });

        var result = await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.Equal("正文", result);
    }

    [Fact]
    public void BuildMessages_PreservesPersonaSystemPromptWhenHistoryReachesCap()
    {
        var service = new LLMService(CreateMockHttpClient("{}"));

        // DJService 裁剪稳态：history[0] 人设 system + 10 对 user/assistant = 21 条
        var history = new List<ChatMessage>
        {
            new() { Role = MessageRole.System, Content = "PERSONA-AND-COMMAND-RULES" }
        };
        for (var i = 0; i < 20; i++)
            history.Add(new ChatMessage
            {
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = $"msg{i}"
            });

        var messages = service.BuildMessages(history, "hello");

        // 兜底 system + 人设 system + 最近 18 条（msg2..msg19，9 个完整对话轮）+ 本轮 user = 21；
        // 直接 TakeLast(20) 会丢掉人设，长对话后 DJ 角色与指令规则静默失效
        Assert.Equal(21, messages.Count);
        Assert.Contains(messages, m => GetMessageContent(m) == "PERSONA-AND-COMMAND-RULES");
        Assert.DoesNotContain(messages, m => GetMessageContent(m) == "msg0");
        Assert.DoesNotContain(messages, m => GetMessageContent(m) == "msg1");
        Assert.Contains(messages, m => GetMessageContent(m) == "msg2");

        // Anthropic 硬约束：首条非 system 消息必须是 user，否则整个请求 400
        var firstNonSystem = messages.First(m => GetMessageRole(m) != "system");
        Assert.Equal("user", GetMessageRole(firstNonSystem));
    }

    private static string? GetMessageContent(object message)
        => message.GetType().GetProperty("content")?.GetValue(message) as string;

    private static string? GetMessageRole(object message)
        => message.GetType().GetProperty("role")?.GetValue(message) as string;
}
