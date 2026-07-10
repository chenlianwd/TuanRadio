using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// LLM 服务，支持 OpenAI 兼容、Anthropic 兼容和本地 OpenAI 兼容接口。
/// </summary>
public class LLMService : ILLMService
{
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

    public async Task<string> ChatAsync(string userMessage, List<ChatMessage> history)
    {
        if (!IsConfigured())
            return "请先在设置中配置 AI 服务。";

        try
        {
            var messages = BuildMessages(history, userMessage);
            return await CallChatCompletionAsync(messages);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LLM chat failed");
            throw new LlmApiException(ApiFailureInfo.FromException(ex));
        }
    }

    public async Task<string> GenerateTrackIntroductionAsync(Track current, Track next)
    {
        if (!IsConfigured())
            return $"接下来播放 {next.Title}。";

        try
        {
            var prompt = $"你现在是 AI 电台 DJ。上一首歌是《{current.Title}》（{current.Artist}），" +
                         $"下一首是《{next.Title}》（{next.Artist}）。" +
                         $"请用一句话自然地衔接两首歌，像电台 DJ 一样。不要超过30字。" +
                         $"末尾附加一个情绪标签：[happy] [sad] [calm] [neutral] [angry] [surprised]。";

            var messages = new List<object>
            {
                new { role = "system", content = "你是一个中文电台 DJ，用自然的口语衔接歌曲。" },
                new { role = "user", content = prompt }
            };

            return await CallChatCompletionAsync(messages);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Track introduction generation failed");
            return $"接下来播放 {next.Title}。";
        }
    }

    private List<object> BuildMessages(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<object>
        {
            new { role = "system", content = "你是一个中文电台 DJ，名叫小音。用温暖自然的语气和听众交流。" }
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

    private async Task<string> CallChatCompletionAsync(List<object> messages)
    {
        if (_config.Provider == "anthropic")
            return await CallAnthropicApiAsync(messages);

        var baseUrl = _baseUrl;
        var model = _model;
        var apiKey = _config.ApiKey;

        return await RetryPolicy.ExecuteAsync(async () =>
        {
            var requestBody = new
            {
                model,
                messages,
                max_tokens = 300,
                temperature = 0.8
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
            {
                Content = content
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

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
            return "";
        });
    }

    private async Task<string> CallAnthropicApiAsync(List<object> messages)
    {
        // Extract system message and convert to Claude format
        string systemPrompt = "";
        var claudeMessages = new List<object>();
        foreach (var msg in messages)
        {
            var msgDict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(msg));
            if (msgDict == null) continue;
            var role = msgDict.GetValueOrDefault("role")?.ToString() ?? "user";
            var msgContent = msgDict.GetValueOrDefault("content")?.ToString() ?? "";
            if (role == "system")
                systemPrompt = msgContent;
            else
                claudeMessages.Add(new { role, content = msgContent });
        }

        var model = _model;
        var requestBody = new
        {
            model,
            system = systemPrompt,
            messages = claudeMessages,
            max_tokens = 300
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, BuildAnthropicMessagesEndpoint(_baseUrl))
        {
            Content = content
        };
        request.Headers.Add("x-api-key", _config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await _httpClient.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

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
        return "";
    }

    private bool IsConfigured()
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
