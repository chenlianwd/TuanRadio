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
    // 思考型模型的推理 token 与正文共用 max_tokens：800 会被思考阶段烧光、
    // 正文块不生成（GLM 实机踩坑），2048 给短串场/台词留足余量；计费按实际
    // 生成量，上调上限不增加典型响应成本
    private const int MaxOutputTokens = 2048;

    private static readonly Dictionary<string, string> Providers = new()
    {
        ["openai"] = "https://api.openai.com/v1",
        ["anthropic"] = "https://api.anthropic.com/v1",
        ["local"] = "http://localhost:11434/v1"
    };

    private readonly HttpClient _httpClient;
    // 配置单字段原子发布：baseUrl/model/apiKey 一律从调用点捕获的 config 快照经
    // ResolveBaseUrl/内联派生，分多次读 _config 会看到切库瞬间的撕裂组合
    private volatile LLMConfig _config = new();

    private static string ResolveBaseUrl(LLMConfig config)
    {
        var baseUrl = (config.BaseUrl ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl.TrimEnd('/');
        return Providers.TryGetValue(config.Provider, out var fallback) ? fallback : Providers["openai"];
    }

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
    }

    public Task<string> ChatAsync(string userMessage, List<ChatMessage> history)
        => ChatAsync(userMessage, history, CancellationToken.None);

    /// <summary>
    /// 无 DJ 人设的原始调用：关键词生成等工具型调用需要纯指令输出。
    /// 走 ChatAsync 时 system 固定是 DJ 人设，模型会以 DJ 口吻回整段台词，
    /// 台词碎片会被调用方当成数据用（如搜索关键词）。
    /// </summary>
    public async Task<string> ChatRawAsync(string userMessage, CancellationToken cancellationToken)
    {
        // 未配置必须抛错而不是把提示文案当数据返回：本方法输出会被调用方直接当
        // 关键词/纠错结果使用（历史上正是"台词碎片被当搜索词"的事故来源）
        if (!IsConfigured())
            throw new LlmApiException(ApiFailureInfo.MissingApiKey());

        try
        {
            var messages = BuildRawMessages(userMessage);
            return await CallChatCompletionAsync(messages, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LLM raw chat failed");
            throw new LlmApiException(ApiFailureInfo.FromException(ex));
        }
    }

    /// <summary>
    /// 枚举本地模型（Ollama GET /api/tags）。尽力而为的 UI 增强：
    /// 服务未启动/端点不支持/任何失败一律返回空列表，由调用方静默降级为手填。
    /// </summary>
    public async Task<List<string>> ListLocalModelsAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        // 端点只从入参派生（空=本地默认端点），绝不读服务的聊天配置快照：
        // 本方法由设置页在切换 provider 下拉时触发，不能有"翻看=改运行配置"的副作用。
        // /v1 是 OpenAI 兼容层前缀，tags 属于 Ollama 原生 API，挂在根路径上
        var effective = string.IsNullOrWhiteSpace(baseUrl)
            ? Providers["local"]
            : baseUrl.Trim().TrimEnd('/');
        var root = effective.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? effective[..^3]
            : effective;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var json = await _httpClient.GetStringAsync($"{root}/api/tags", timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return [];
            var names = new List<string>();
            foreach (var entry in models.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                    names.Add(value);
            }
            return names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            // 枚举失败不影响手填模型名，也不该打断设置页交互
            Log.Debug(ex, "Local model enumeration failed");
            return [];
        }
    }

    internal List<object> BuildRawMessages(string userMessage)
    {
        // 不注入 DJ 人设；Anthropic 首条非 system 消息必须是 user，此处无 system 直接以 user 开头
        return [new { role = "user", content = userMessage }];
    }

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

    internal List<object> BuildMessages(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<object>();

        // 内置兜底人设只服务空历史调用（连接测试、歌曲故事、接口 mock 分支——提示词全在
        // user 消息里，无 system 会完全无人格语境）。history 自带 system（DJ 聊天恒以角色
        // 人设开头）时不再前置：中文版"名叫小音"与所选角色名会形成双人格指令冲突。
        var hasOwnSystem = history.Count > 0 && history[0].Role == MessageRole.System;
        if (!hasOwnSystem)
        {
            messages.Add(new { role = "system", content = AppLanguage.T("你是一个中文电台 DJ，名叫小音。用温暖自然的语气和听众交流。", "You are an English-speaking AI radio DJ. Talk to listeners in a warm, natural tone.") });
        }

        // 历史满 21 条（DJService 裁剪上限 = system + 10 对）时，直接 TakeLast(20) 会丢掉
        // history[0] 的 DJ 人设/指令规则，长对话后角色静默退化为通用小音。
        // 取人设 + 最近 18 条（9 个完整对话轮），并防御性丢弃以 assistant 开头的尾部：
        // Anthropic 要求首条非 system 消息必须是 user，否则整个请求 400。
        IEnumerable<ChatMessage> recent = history;
        if (history.Count > 20)
        {
            var tail = history.Skip(history.Count - 18).ToList();
            while (tail.Count > 0 && tail[0].Role != MessageRole.User && tail[0].Role != MessageRole.System)
                tail.RemoveAt(0);
            recent = history.Take(1).Concat(tail);
        }

        foreach (var msg in recent)
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
        // 全部字段从同一次 config 快照派生：分多次读 _config 会看到"新 provider + 旧
        // baseUrl/model/apiKey"的撕裂组合（Configure 并发落在这几次读之间时）
        var config = _config;
        if (config.Provider == "anthropic")
            return await CallAnthropicApiAsync(messages, cancellationToken);

        var baseUrl = ResolveBaseUrl(config);
        var model = (config.Model ?? string.Empty).Trim();
        var apiKey = config.ApiKey;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        return await RetryPolicy.ExecuteAsync(async cancellationToken =>
        {
            // local（Ollama）跑 Qwen 系思考模型时 DJ 台词会被 16s 隐式推理拖死；
            // Ollama 兼容层把 reasoning_effort=none 映射为关闭思考（实测 16s→0.7s），
            // 仅对 local 注入，云端 OpenAI 兼容服务不认这个取值会 400。
            // 留档：local≠必是 Ollama——严格校验未知字段的本地兼容层（vLLM 类）
            // 可能因此 400，届时再裁决按端点收窄或加开关，llama.cpp/LM Studio 已知容忍
            object requestBody = config.Provider == "local"
                ? new { model, messages, max_tokens = MaxOutputTokens, temperature = 0.8, reasoning_effort = "none" }
                : new { model, messages, max_tokens = MaxOutputTokens, temperature = 0.8 };

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
                // content 为 null/空白（部分兼容网关的过滤/拒绝场景）按无效响应抛错，
                // 与 Anthropic 分支同口径：空串会被当合法台词写入历史并播报成空气泡
                var contentText = msgContent.ValueKind == JsonValueKind.String
                    ? msgContent.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(contentText))
                    throw new LlmApiException(new ApiFailureInfo(
                        ApiFailureKind.InvalidResponse,
                        "AI 返回了空回复",
                        "模型未返回任何正文内容（可能被内容过滤拦截）。",
                        "请重试，或检查模型/服务是否支持当前请求。"));
                return contentText;
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
        // 多条 system 必须合并而不是相互覆盖（当前生产聊天恒单 system=角色人设；
        // BuildMessages 空历史兜底与角色人设不再并存，合并逻辑保留为防御）
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

        // 与 CallChatCompletionAsync 同理：单次快照派生，避免撕裂读
        var config = _config;
        var model = (config.Model ?? string.Empty).Trim();
        var baseUrl = ResolveBaseUrl(config);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        return await RetryPolicy.ExecuteAsync(async ct =>
        {
            // 无 system（如 ChatRawAsync 的纯指令调用）时整个省略字段：
            // 官方 API 把空串视为省略，但部分 Anthropic 兼容代理严格校验非空
            var systemText = string.Join("\n\n", systemParts);
            var requestBody = systemText.Length == 0
                ? (object)new { model, messages = claudeMessages, max_tokens = MaxOutputTokens }
                : new { model, system = systemText, messages = claudeMessages, max_tokens = MaxOutputTokens };

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
                            // 部分 Anthropic 兼容服务以 {"type":"thinking","text":...} 形态输出推理过程，
                            // 思考文本不是播报正文，不能拼进 DJ 台词
                            var blockType = block.TryGetProperty("type", out var typeEl) &&
                                            typeEl.ValueKind == JsonValueKind.String
                                ? typeEl.GetString()
                                : null;
                            if (blockType is "thinking" or "redacted_thinking")
                                continue;

                            textParts.Add(text.GetString()!);
                        }
                    }

                    if (textParts.Count > 0)
                        return string.Join("\n", textParts);

                    // content 存在但没有任何正文块：思考型模型把 max_tokens 烧在思考阶段
                    // 是典型场景（GLM 等），与"结构不符"分开报，不再误导排查方向
                    var stopReason = root.TryGetProperty("stop_reason", out var stopReasonEl) &&
                                     stopReasonEl.ValueKind == JsonValueKind.String
                        ? stopReasonEl.GetString()
                        : null;
                    throw new LlmApiException(stopReason == "max_tokens"
                        ? new ApiFailureInfo(
                            ApiFailureKind.InvalidResponse,
                            AppLanguage.T("AI 思考超限未产出正文", "AI ran out of output budget while thinking"),
                            AppLanguage.T("模型的思考过程耗尽了输出上限（max_tokens），正文没有生成。",
                                "The model's reasoning consumed the entire output budget (max_tokens) before any text was generated."),
                            AppLanguage.T("请重试，或更换输出更紧凑的模型。",
                                "Try again, or switch to a more concise model."))
                        : new ApiFailureInfo(
                            ApiFailureKind.InvalidResponse,
                            AppLanguage.T("AI 返回了空回复", "AI returned an empty reply"),
                            AppLanguage.T("模型未返回任何正文内容（可能被内容过滤拦截）。",
                                "The model returned no body text (possibly blocked by content filtering)."),
                            AppLanguage.T("请重试，或检查模型/服务是否支持当前请求。",
                                "Try again, or check whether the model/service supports this request.")));
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
        var config = _config;
        var provider = config.Provider;
        if (provider is null)
            return false;

        if (string.IsNullOrWhiteSpace((config.Model ?? string.Empty).Trim()))
            return false;

        return provider.Equals("local", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(config.ApiKey);
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
