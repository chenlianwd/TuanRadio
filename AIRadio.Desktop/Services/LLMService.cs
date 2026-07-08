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
/// OpenAI 兼容的 LLM 服务，支持 OpenAI/Claude/DeepSeek/Ollama 等提供商。
/// </summary>
public class LLMService : ILLMService
{
    private static readonly Dictionary<string, (string BaseUrl, string DefaultModel)> Providers = new()
    {
        ["openai"] = ("https://api.openai.com/v1", "gpt-4o-mini"),
        ["deepseek"] = ("https://api.deepseek.com/v1", "deepseek-chat"),
        ["claude"] = ("https://api.anthropic.com/v1", "claude-3-haiku-20240307"),
        ["openrouter"] = ("https://openrouter.ai/api/v1", "anthropic/claude-3-haiku"),
        ["ollama"] = ("http://localhost:11434/v1", "llama3")
    };

    private readonly HttpClient _httpClient;
    private volatile LLMConfig _config = new();
    private volatile string _baseUrl = "https://api.openai.com/v1";
    private volatile string _model = "gpt-4o-mini";

    public LLMService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void Configure(LLMConfig config)
    {
        _config = config;

        if (Providers.TryGetValue((config.Provider ?? "none").ToLowerInvariant(), out var provider))
        {
            _baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? provider.BaseUrl : config.BaseUrl;
            _model = string.IsNullOrWhiteSpace(config.Model) ? provider.DefaultModel : config.Model;
        }
        else
        {
            _baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl;
            _model = string.IsNullOrWhiteSpace(config.Model) ? "gpt-4o-mini" : config.Model;
        }
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
            throw new MinimaxApiException(ApiFailureInfo.FromException(ex));
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
        // Claude uses a different API format
        if (_config.Provider == "claude")
            return await CallClaudeApiAsync(messages);

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
                throw new MinimaxApiException(ApiFailureInfo.FromStatusCode(response.StatusCode, responseJson));
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

    private async Task<string> CallClaudeApiAsync(List<object> messages)
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

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/messages")
        {
            Content = content
        };
        request.Headers.Add("x-api-key", _config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await _httpClient.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Claude API error {StatusCode}: {Body}", response.StatusCode, responseJson);
            throw new MinimaxApiException(ApiFailureInfo.FromStatusCode(response.StatusCode, responseJson));
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // Claude format: content[0].text
        if (root.TryGetProperty("content", out var contentArr) && contentArr.GetArrayLength() > 0 &&
            contentArr[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "";
        }

        Log.Warning("Unrecognized Claude response format");
        return "";
    }

    private bool IsConfigured()
    {
        var provider = _config.Provider;
        if (provider is null or "none")
            return false;

        return provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(_config.ApiKey);
    }
}
