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

    private static LLMService CreateAnthropicService(string responseJson, Action<HttpRequestMessage>? onRequest = null)
        => new(CreateMockHttpClient(responseJson, onRequest));

    private static LLMConfig AnthropicConfig => new()
    {
        Provider = "anthropic",
        BaseUrl = "https://proxy.example/v1",
        Model = "glm-thinking",
        ApiKey = "k"
    };

    // 思考型模型把 max_tokens 烧在思考阶段：只有 thinking 块、无正文块，
    // 报错必须指向预算耗尽而不是误导性的"无法识别的格式"
    [Fact]
    public async Task ChatAsync_AnthropicFormat_ThinkingOnlyWithMaxTokensReportsBudgetFailure()
    {
        var original = AppLanguage.Current;
        LLMService service = CreateAnthropicService(
            """{"stop_reason":"max_tokens","content":[{"type":"thinking","thinking":"思考中"}]}""");
        try
        {
            AppLanguage.Apply("zh");
            service.Configure(AnthropicConfig);

            var ex = await Assert.ThrowsAsync<LlmApiException>(
                () => service.ChatAsync("hello", new List<ChatMessage>()));

            Assert.Contains("思考超限", ex.Failure.Title);
        }
        finally
        {
            AppLanguage.Apply(original);
        }
    }

    [Fact]
    public async Task ChatAsync_AnthropicFormat_ThinkingOnlyWithoutMaxTokensReportsEmptyReply()
    {
        var original = AppLanguage.Current;
        LLMService service = CreateAnthropicService(
            """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"思考中"}]}""");
        try
        {
            AppLanguage.Apply("zh");
            service.Configure(AnthropicConfig);

            var ex = await Assert.ThrowsAsync<LlmApiException>(
                () => service.ChatAsync("hello", new List<ChatMessage>()));

            Assert.Contains("空回复", ex.Failure.Title);
        }
        finally
        {
            AppLanguage.Apply(original);
        }
    }

    [Fact]
    public async Task ChatAsync_AnthropicFormat_SendsExpandedMaxTokens()
    {
        // 请求在 LLMService 内部 using 释放，body 须在发送前的回调里读出
        string? body = null;
        var service = CreateAnthropicService(
            """{"content":[{"type":"text","text":"正文"}]}""",
            request => body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        service.Configure(AnthropicConfig);

        await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.NotNull(body);
        Assert.Contains("\"max_tokens\":2048", body);
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

    [Fact]
    public void BuildRawMessages_DoesNotInjectDjPersona()
    {
        var service = new LLMService(CreateMockHttpClient("{}"));

        var messages = service.BuildRawMessages("生成搜索关键词");

        // 关键词生成等工具型调用不能带 DJ 人设：人设会让模型以 DJ 口吻回整段台词，
        // 台词碎片被调用方当成搜索词拿去搜歌
        Assert.Single(messages);
        Assert.Equal("user", GetMessageRole(messages[0]));
        Assert.Equal("生成搜索关键词", GetMessageContent(messages[0]));
        Assert.DoesNotContain(messages, m => GetMessageRole(m) == "system");
    }

    private static string? GetMessageContent(object message)
        => message.GetType().GetProperty("content")?.GetValue(message) as string;

    private static string? GetMessageRole(object message)
        => message.GetType().GetProperty("role")?.GetValue(message) as string;

    [Fact]
    public async Task ListLocalModelsAsync_ParsesNamesAndStripsV1Prefix()
    {
        string? requestedUrl = null;
        var responseJson = @"{""models"":[{""name"":""qwen3.5:9b""},{""name"":""qwen3.5:4b-16k""}]}";
        var service = new LLMService(CreateMockHttpClient(responseJson, r => requestedUrl = r.RequestUri?.ToString()));

        var models = await service.ListLocalModelsAsync(baseUrl: null, CancellationToken.None);

        // baseUrl 为空走本地默认端点；/api/tags 是 Ollama 原生 API：
        // 挂在根上，必须把 OpenAI 兼容层的 /v1 前缀剥掉
        Assert.Equal("http://localhost:11434/api/tags", requestedUrl);
        Assert.Equal(new[] { "qwen3.5:9b", "qwen3.5:4b-16k" }, models);
    }

    [Fact]
    public async Task ListLocalModelsAsync_CustomBaseUrlWithoutV1_QueriesRootDirectly()
    {
        string? requestedUrl = null;
        var service = new LLMService(CreateMockHttpClient(@"{""models"":[]}", r => requestedUrl = r.RequestUri?.ToString()));

        _ = await service.ListLocalModelsAsync(baseUrl: "http://127.0.0.1:19999", CancellationToken.None);

        Assert.Equal("http://127.0.0.1:19999/api/tags", requestedUrl);
    }

    [Fact]
    public async Task ListLocalModelsAsync_FailureOrGarbage_ReturnsEmptyList()
    {
        // 服务未起/端点不认识 /api/tags/返回非 JSON：一律空列表静默降级，不打断设置页
        var service = new LLMService(CreateMockHttpClient("not json"));

        var models = await service.ListLocalModelsAsync(baseUrl: null, CancellationToken.None);

        Assert.Empty(models);
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("openai", false)]
    public async Task ChatAsync_ReasoningEffortNone_OnlyInjectedForLocalProvider(string provider, bool expectField)
    {
        // local（Ollama）不关思考 DJ 台词 16s；云端 OpenAI 兼容服务不认 none 会 400。
        // 条件写反或扩散到全部 provider 时此处必红
        string? body = null;
        var service = new LLMService(CreateMockHttpClient(
            """{"choices":[{"message":{"content":"ok"}}]}""",
            request => body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
        service.Configure(new LLMConfig { Provider = provider, Model = "m", ApiKey = "k" });

        await service.ChatAsync("hello", new List<ChatMessage>());

        Assert.NotNull(body);
        if (expectField)
            Assert.Contains("\"reasoning_effort\":\"none\"", body);
        else
            Assert.DoesNotContain("reasoning_effort", body);
    }
}
