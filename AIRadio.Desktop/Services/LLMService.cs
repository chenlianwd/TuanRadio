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
        ["ollama"] = ("http://localhost:11434/v1", "llama3")
    };

    private readonly HttpClient _httpClient;
    private LLMConfig _config = new();
    private string _baseUrl = "https://api.openai.com/v1";
    private string _model = "gpt-4o-mini";

    public LLMService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void Configure(LLMConfig config)
    {
        _config = config;

        if (Providers.TryGetValue(config.Provider.ToLowerInvariant(), out var provider))
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
        if (_config.Provider == "none" || string.IsNullOrWhiteSpace(_config.ApiKey))
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
        if (_config.Provider == "none" || string.IsNullOrWhiteSpace(_config.ApiKey))
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
            messages.Add(new
            {
                role = msg.Role == MessageRole.User ? "user" : "assistant",
                content = msg.Content
            });
        }

        messages.Add(new { role = "user", content = userMessage });
        return messages;
    }

    private async Task<string> CallChatCompletionAsync(List<object> messages)
    {
        var requestBody = new
        {
            model = _model,
            messages,
            max_tokens = 300,
            temperature = 0.8
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = content
        };

        // Handle different auth header formats
        if (_config.Provider == "claude")
        {
            request.Headers.Add("x-api-key", _config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

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

        // Claude format: content[0].text
        if (root.TryGetProperty("content", out var contentArr) && contentArr.GetArrayLength() > 0 &&
            contentArr[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "";
        }

        return "";
    }
}
