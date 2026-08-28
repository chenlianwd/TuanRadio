using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// LLM 服务，支持 OpenAI 兼容、Anthropic 兼容和本地 OpenAI 兼容接口。
/// </summary>
public class LLMService : ILLMService
{
    private const int MaxOutputTokens = 800;

    private static readonly Dictionary<string, string> Providers = new()
    {
        ["openai"] = "https://api.openai.com/v1",
        ["anthropic"] = "https://api.anthropic.com/v1",
        ["local"] = "http://localhost:11434/v1"
    };

    private readonly HttpClient _httpClient;
    private volatile LLMConfig _config = new();
    private volatile string _baseUrl = "https://api.openai.com/v1";
    private volatile string _model = string.Empty;

    public LLMService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void Configure(LLMConfig config)
    {
        var provider = NormalizeProvider(config.Provider);
        var apiKey = (config.ApiKey ?? string.Empty).Trim();
        var baseUrl = (config.BaseUrl ?? string.Empty).Trim();
        var model = (config.Model ?? string.Empty).Trim();
        _config = config with
        {
            Provider = provider,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Model = model
        };

        var defaultBaseUrl = Providers[provider];
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? defaultBaseUrl : baseUrl.TrimEnd('/');
        _model = model;
    }

    public Task<string> ChatAsync(string userMessage, List<ChatMessage> history)
        => ChatAsync(userMessage, history, CancellationToken.None);

    public async Task<string> ChatAsync(
        string userMessage,
        List<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured())
            return AppLanguage.T("请先在设置中配置 AI 服务。", "Configure the AI service in Settings first.");

        try
        {
            var messages = BuildMessages(history, userMessage);
            return await CallChatCompletionAsync(messages, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmApiException)
        {
            // 已带失败分类（如 InvalidResponse），不要二次包装丢失分类
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LLM chat failed");
            throw new LlmApiException(ApiFailureInfo.FromException(ex));
        }
    }

    public Task<string> GenerateTrackIntroductionAsync(Track current, Track next)
        => GenerateTrackIntroductionAsync(current, next, CancellationToken.None);

    public async Task<string> GenerateTrackIntroductionAsync(
        Track current,
        Track next,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured())
            return AppLanguage.T($"接下来播放 {next.Title}。", $"Up next: {next.Title}.");

        try
        {
            var prompt = AppLanguage.T(
                $"你现在是 AI 电台 DJ。上一首歌是《{current.Title}》（{current.Artist}），下一首是《{next.Title}》（{next.Artist}）。请用一句话自然地衔接两首歌，像电台 DJ 一样。不要超过30字。末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。",
                $"You are an AI radio DJ. The previous track was '{current.Title}' by {current.Artist}; the next is '{next.Title}' by {next.Artist}. Connect them naturally in one short sentence and end with one emotion tag: [happy] [sad] [calm] [neutral] [angry] [surprised].");

            var messages = new List<object>
            {
                new { role = "system", content = AppLanguage.T("你是一个中文电台 DJ，用自然的口语衔接歌曲。", "You are an English-speaking radio DJ who connects songs naturally.") },
                new { role = "user", content = prompt }
            };

            return await CallChatCompletionAsync(messages, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Track introduction generation failed");
            return AppLanguage.T($"接下来播放 {next.Title}。", $"Up next: {next.Title}.");
        }
    }

    private List<object> BuildMessages(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<object>
        {
            new { role = "system", content = AppLanguage.T("你是一个中文电台 DJ，名叫小音。用温暖自然的语气和听众交流。", "You are an English-speaking AI radio DJ. Talk to listeners in a warm, natural tone.") }
        };

        foreach (var msg in history.TakeLast(20))
        {
            var role = msg.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.System => "system",
                _ => "assistant"
            };
            messages.Add(new { role, content = msg.Content });
        }

        messages.Add(new { role = "user", content = userMessage });
        return messages;
    }

    private async Task<string> CallChatCompletionAsync(
        List<object> messages,
        CancellationToken cancellationToken)
    {
        var config = _config;
        if (config.Provider == "anthropic")
            return await CallAnthropicApiAsync(messages, cancellationToken);

        var baseUrl = _baseUrl;
        var model = _model;
        var apiKey = config.ApiKey;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        return await RetryPolicy.ExecuteAsync(async cancellationToken =>
        {
            var requestBody = new
            {
                model,
                messages,
                max_tokens = MaxOutputTokens,
                temperature = 0.8
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = content
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("LLM API error {StatusCode}: {Body}", response.StatusCode, responseJson);
                throw new LlmApiException(ApiFailureInfo.FromStatusCode(response.StatusCode, responseJson));
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // OpenAI format: choices[0].message.content
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var msgContent))
            {
                return msgContent.GetString() ?? "";
            }

            Log.Warning("Unrecognized LLM response format: {Response}", responseJson[..Math.Min(200, responseJson.Length)]);
            throw new LlmApiException(new ApiFailureInfo(
                ApiFailureKind.InvalidResponse,
                "AI 返回了无法识别的格式",
                "响应不是预期的 OpenAI chat/completions 结构。",
                "请检查设置中的模型名称与服务地址是否匹配，或稍后重试。"));
        }, timeoutCts.Token, maxRetries: 2);
    }

    private async Task<string> CallAnthropicApiAsync(
        List<object> messages,
        CancellationToken cancellationToken)
    {
        // Extract system message and convert to Claude format.
        // 多条 system（LLMService 内置"小音"人设 + DJ 角色人设）必须合并而不是相互覆盖
        var systemParts = new List<string>();
        var claudeMessages = new List<object>();
        foreach (var msg in messages)
        {
            var msgDict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(msg));
            if (msgDict == null) continue;
            var role = msgDict.GetValueOrDefault("role")?.ToString() ?? "user";
            var msgContent = msgDict.GetValueOrDefault("content")?.ToString() ?? "";
            if (role == "system")
            {
                if (!string.IsNullOrWhiteSpace(msgContent))
                    systemParts.Add(msgContent);
            }
            else
            {
                claudeMessages.Add(new { role, content = msgContent });
            }
        }

        var config = _config;
        var model = _model;
        var baseUrl = _baseUrl;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        return await RetryPolicy.ExecuteAsync(async ct =>
        {
            var requestBody = new
            {
                model,
                system = string.Join("\n\n", systemParts),
                messages = claudeMessages,
                max_tokens = MaxOutputTokens
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildAnthropicMessagesEndpoint(baseUrl))
            {
                Content = content
            };
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Anthropic API error {StatusCode}: {Body}", response.StatusCode, responseJson);
                throw new LlmApiException(ApiFailureInfo.FromStatusCode(response.StatusCode, responseJson));
            }

            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // Anthropic 可能先返回 thinking 块，正文不一定是 content[0]。
            if (root.TryGetProperty("content", out var contentElement))
            {
                if (contentElement.ValueKind == JsonValueKind.String)
                    return contentElement.GetString() ?? "";

                if (contentElement.ValueKind == JsonValueKind.Array)
                {
                    var textParts = new List<string>();
                    foreach (var block in contentElement.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(block.GetString()))
                        {
                            textParts.Add(block.GetString()!);
                        }

                        if (block.ValueKind == JsonValueKind.Object &&
                            block.TryGetProperty("text", out var text) &&
                            !string.IsNullOrWhiteSpace(text.GetString()))
                        {
                            textParts.Add(text.GetString()!);
                        }
                    }

                    if (textParts.Count > 0)
                        return string.Join("\n", textParts);
                }
            }

            Log.Warning("Unrecognized Anthropic response format: {Response}",
                responseJson[..Math.Min(200, responseJson.Length)]);
            throw new LlmApiException(new ApiFailureInfo(
                ApiFailureKind.InvalidResponse,
                "AI 返回了无法识别的格式",
                "响应不是预期的 Anthropic messages 结构。",
                "请检查设置中的模型名称与服务地址是否匹配，或稍后重试。"));
        }, timeoutCts.Token, maxRetries: 2);
    }

    public bool IsConfigured()
    {
        var provider = _config.Provider;
        if (provider is null)
            return false;

        if (string.IsNullOrWhiteSpace(_model))
            return false;

        return provider.Equals("local", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(_config.ApiKey);
    }

    private static string NormalizeProvider(string? provider) => provider?.ToLowerInvariant() switch
    {
        "claude" or "anthropic" => "anthropic",
        "ollama" or "local" => "local",
        _ => "openai"
    };

    private static string BuildAnthropicMessagesEndpoint(string baseUrl)
    {
        // Kimi 等兼容服务可能给出 /v1 的上一级 Base URL，标准 Anthropic 地址则通常已经包含 /v1。
        var normalized = baseUrl.TrimEnd('/');
        if (normalized.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return normalized;
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return $"{normalized}/messages";

        return $"{normalized}/v1/messages";
    }
}
