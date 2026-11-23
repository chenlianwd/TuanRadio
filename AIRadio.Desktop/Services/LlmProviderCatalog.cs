using System;
using System.Collections.Generic;
using System.Linq;

namespace AIRadio.Desktop.Services;

/// <summary>单个预设服务商下可选接口端点（协议格式 / 区域 / 专属套餐入口）的定义。</summary>
public sealed record LlmEndpointSpec(
    string Id,
    string NameZh,
    string NameEn,
    string Protocol,
    string BaseUrl,
    IReadOnlyList<string> Models,
    string? HintZh = null,
    string? HintEn = null)
{
    public string DisplayName => AppLanguage.T(NameZh, NameEn);

    public string Hint => string.IsNullOrEmpty(HintZh) && string.IsNullOrEmpty(HintEn)
        ? string.Empty
        : AppLanguage.T(HintZh ?? string.Empty, HintEn ?? string.Empty);

    /// <summary>
    /// 凭据计费域标识：同一服务商下通用 OpenAI/Anthropic 端点共享默认空域（""），
    /// Coding Plan 专属套餐（"coding"）与国际站（"intl"）使用独立凭据域，防止不同套餐/站点的 API Key 互相覆盖。
    /// </summary>
    public string CredentialDomain => LlmProviderCatalog.ResolveCredentialDomain(Id);
}

/// <summary>主流大模型服务商预设定义（含多个兼容端点与常用模型清单）。</summary>
public sealed record LlmPresetSpec(
    string Id,
    string NameZh,
    string NameEn,
    IReadOnlyList<LlmEndpointSpec> Endpoints,
    bool IsCustom = false)
{
    public string DisplayName => AppLanguage.T(NameZh, NameEn);
}

/// <summary>内置大模型服务商目录、端点匹配与历史配置反推工具。</summary>
public static class LlmProviderCatalog
{
    public const string CustomId = "custom";

    public static IReadOnlyList<LlmPresetSpec> All { get; } = new List<LlmPresetSpec>
    {
        new(
            "deepseek",
            "DeepSeek",
            "DeepSeek",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容",
                    "OpenAI-compatible",
                    "openai",
                    "https://api.deepseek.com/v1",
                    new[] { "deepseek-chat", "deepseek-reasoner" }),
                new(
                    "anthropic",
                    "Anthropic 兼容",
                    "Anthropic-compatible",
                    "anthropic",
                    "https://api.deepseek.com/anthropic",
                    new[] { "deepseek-chat", "deepseek-reasoner" })
            }),
        new(
            "zhipu",
            "智谱 GLM",
            "Zhipu GLM",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（通用）",
                    "OpenAI-compatible (Standard)",
                    "openai",
                    "https://open.bigmodel.cn/api/paas/v4",
                    new[] { "glm-4.5-flash", "glm-4.5-air", "glm-4.5", "glm-4.6", "glm-4.7", "glm-4-flash", "glm-4-plus" }),
                new(
                    "anthropic",
                    "Anthropic 兼容",
                    "Anthropic-compatible",
                    "anthropic",
                    "https://open.bigmodel.cn/api/anthropic",
                    new[] { "glm-4.7", "glm-4.6", "glm-4.5", "glm-4.5-air" },
                    "若使用 Coding Plan，请填写套餐专属 API Key",
                    "For Coding Plan, use your dedicated Coding Plan API key"),
                new(
                    "coding-openai",
                    "OpenAI 兼容（Coding Plan）",
                    "OpenAI-compatible (Coding Plan)",
                    "openai",
                    "https://open.bigmodel.cn/api/coding/paas/v4",
                    new[] { "glm-4.7", "glm-4.6", "glm-4.5" },
                    "需使用智谱 GLM Coding Plan 专属 API Key",
                    "Requires a dedicated Zhipu GLM Coding Plan API key")
            }),
        new(
            "kimi",
            "Kimi (月之暗面)",
            "Kimi (Moonshot)",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（国内）",
                    "OpenAI-compatible (China)",
                    "openai",
                    "https://api.moonshot.cn/v1",
                    new[] { "kimi-k2.5", "kimi-k2-turbo-preview", "kimi-k2-0905-preview", "moonshot-v1-8k", "moonshot-v1-32k" }),
                new(
                    "anthropic",
                    "Anthropic 兼容（国内）",
                    "Anthropic-compatible (China)",
                    "anthropic",
                    "https://api.moonshot.cn/anthropic",
                    new[] { "kimi-k2.5", "kimi-k2-turbo-preview", "kimi-k2-0905-preview" }),
                new(
                    "coding-anthropic",
                    "Anthropic 兼容（Kimi Code）",
                    "Anthropic-compatible (Kimi Code)",
                    "anthropic",
                    "https://api.kimi.com/coding",
                    new[] { "kimi-for-coding", "kimi-k2.5" },
                    "需使用 Kimi Code / 编程套餐专属 API Key",
                    "Requires a dedicated Kimi Code API key"),
                new(
                    "openai-intl",
                    "OpenAI 兼容（国际）",
                    "OpenAI-compatible (Global)",
                    "openai",
                    "https://api.moonshot.ai/v1",
                    new[] { "kimi-k2.5", "kimi-k2-turbo-preview", "kimi-k2-0905-preview" }),
                new(
                    "anthropic-intl",
                    "Anthropic 兼容（国际）",
                    "Anthropic-compatible (Global)",
                    "anthropic",
                    "https://api.moonshot.ai/anthropic",
                    new[] { "kimi-k2.5", "kimi-k2-turbo-preview", "kimi-k2-0905-preview" })
            }),
        new(
            "qwen",
            "通义千问 (百炼)",
            "Qwen (DashScope)",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（通用）",
                    "OpenAI-compatible (Standard)",
                    "openai",
                    "https://dashscope.aliyuncs.com/compatible-mode/v1",
                    new[] { "qwen-plus", "qwen-turbo", "qwen-flash", "qwen-max", "qwen3-max" }),
                new(
                    "anthropic",
                    "Anthropic 兼容（通用）",
                    "Anthropic-compatible (Standard)",
                    "anthropic",
                    "https://dashscope.aliyuncs.com/apps/anthropic",
                    new[] { "qwen-plus", "qwen3-max", "qwen3-coder-plus" }),
                new(
                    "coding-openai",
                    "OpenAI 兼容（Coding Plan）",
                    "OpenAI-compatible (Coding Plan)",
                    "openai",
                    "https://coding.dashscope.aliyuncs.com/v1",
                    new[] { "qwen3-coder-plus", "qwen3.5-plus", "kimi-k2.5", "glm-4.7" },
                    "需使用阿里云百炼 Coding Plan 专属 API Key（sk-sp- 开头）",
                    "Requires an Alibaba DashScope Coding Plan API key (starts with sk-sp-)"),
                new(
                    "coding-anthropic",
                    "Anthropic 兼容（Coding Plan）",
                    "Anthropic-compatible (Coding Plan)",
                    "anthropic",
                    "https://coding.dashscope.aliyuncs.com/apps/anthropic",
                    new[] { "qwen3-coder-plus", "qwen3.5-plus", "kimi-k2.5", "glm-4.7" },
                    "需使用阿里云百炼 Coding Plan 专属 API Key（sk-sp- 开头）",
                    "Requires an Alibaba DashScope Coding Plan API key (starts with sk-sp-)")
            }),
        new(
            "minimax",
            "MiniMax",
            "MiniMax",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（国内）",
                    "OpenAI-compatible (China)",
                    "openai",
                    "https://api.minimaxi.com/v1",
                    new[] { "MiniMax-M2.5", "MiniMax-M2.1", "MiniMax-Text-01" }),
                new(
                    "anthropic",
                    "Anthropic 兼容（国内）",
                    "Anthropic-compatible (China)",
                    "anthropic",
                    "https://api.minimaxi.com/anthropic",
                    new[] { "MiniMax-M2.5", "MiniMax-M2.1" }),
                new(
                    "openai-intl",
                    "OpenAI 兼容（国际）",
                    "OpenAI-compatible (Global)",
                    "openai",
                    "https://api.minimax.io/v1",
                    new[] { "MiniMax-M2.5", "MiniMax-M2.1" }),
                new(
                    "anthropic-intl",
                    "Anthropic 兼容（国际）",
                    "Anthropic-compatible (Global)",
                    "anthropic",
                    "https://api.minimax.io/anthropic",
                    new[] { "MiniMax-M2.5", "MiniMax-M2.1" })
            }),
        new(
            "siliconflow",
            "硅基流动",
            "SiliconFlow",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（国内）",
                    "OpenAI-compatible (China)",
                    "openai",
                    "https://api.siliconflow.cn/v1",
                    new[] { "deepseek-ai/DeepSeek-V3", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen3-8B", "Qwen/Qwen2.5-7B-Instruct", "zai-org/GLM-4.5-Air" }),
                new(
                    "openai-intl",
                    "OpenAI 兼容（国际）",
                    "OpenAI-compatible (Global)",
                    "openai",
                    "https://api.siliconflow.com/v1",
                    new[] { "deepseek-ai/DeepSeek-V3", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen3-8B" })
            }),
        new(
            "doubao",
            "豆包 (火山方舟)",
            "Doubao (Volcengine Ark)",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容（北京）",
                    "OpenAI-compatible (Beijing)",
                    "openai",
                    "https://ark.cn-beijing.volces.com/api/v3",
                    new[] { "doubao-seed-1-6-250615", "doubao-1-5-pro-32k-250115", "doubao-1-5-lite-32k-250115" },
                    "也可直接输入火山方舟推理接入点 ID（如 ep-xxxx）",
                    "You can also type a Volcengine Ark endpoint ID (e.g. ep-xxxx)")
            }),
        new(
            "openai",
            "OpenAI",
            "OpenAI",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 官方",
                    "OpenAI Official",
                    "openai",
                    "https://api.openai.com/v1",
                    new[] { "gpt-4o-mini", "gpt-4.1-mini", "gpt-4.1", "gpt-4o" })
            }),
        new(
            "anthropic",
            "Anthropic (Claude)",
            "Anthropic (Claude)",
            new LlmEndpointSpec[]
            {
                new(
                    "anthropic",
                    "Anthropic 官方",
                    "Anthropic Official",
                    "anthropic",
                    "https://api.anthropic.com/v1",
                    new[] { "claude-sonnet-4-5", "claude-haiku-4-5", "claude-opus-4-5" })
            }),
        new(
            "gemini",
            "Google Gemini",
            "Google Gemini",
            new LlmEndpointSpec[]
            {
                new(
                    "openai",
                    "OpenAI 兼容",
                    "OpenAI-compatible",
                    "openai",
                    "https://generativelanguage.googleapis.com/v1beta/openai",
                    new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash" })
            }),
        new(
            "ollama",
            "本地模型 (Ollama)",
            "Local (Ollama / LM Studio)",
            new LlmEndpointSpec[]
            {
                new(
                    "ollama",
                    "Ollama 本地",
                    "Local Ollama",
                    "local",
                    "http://localhost:11434/v1",
                    Array.Empty<string>()),
                new(
                    "lmstudio",
                    "LM Studio 本地",
                    "Local LM Studio",
                    "local",
                    "http://localhost:1234/v1",
                    Array.Empty<string>())
            }),
        new(
            CustomId,
            "自定义",
            "Custom",
            Array.Empty<LlmEndpointSpec>(),
            IsCustom: true)
    };

    public static IReadOnlySet<string> NamedPresetIds { get; } = All
        .Where(p => !p.IsCustom)
        .Select(p => p.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static LlmPresetSpec? FindPreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return null;
        return All.FirstOrDefault(p => string.Equals(p.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static LlmEndpointSpec? FindEndpoint(LlmPresetSpec preset, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return null;
        return preset.Endpoints.FirstOrDefault(e =>
            string.Equals(e.Id, endpointId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 根据端点 ID 解析凭据计费域：通用端点返回空串（沿用 <c>llm.preset.{id}</c> 主槽），
    /// Coding Plan 返回 <c>"coding"</c>，国际站返回 <c>"intl"</c>。
    /// </summary>
    public static string ResolveCredentialDomain(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return string.Empty;
        var trimmed = endpointId.Trim();
        if (trimmed.StartsWith("coding-", StringComparison.OrdinalIgnoreCase))
            return "coding";
        if (trimmed.EndsWith("-intl", StringComparison.OrdinalIgnoreCase))
            return "intl";
        return string.Empty;
    }

    /// <summary>
    /// 在服务商支持的端点中寻找匹配目标协议的端点，尽量保留当前端点变体语义（Coding Plan / 国际版 / 标准版）。
    /// </summary>
    public static LlmEndpointSpec? FindMatchingEndpointForProtocol(
        LlmPresetSpec preset,
        string protocol,
        string? currentEndpointId)
    {
        var candidates = preset.Endpoints
            .Where(e => string.Equals(e.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(currentEndpointId))
        {
            var isCoding = currentEndpointId.StartsWith("coding-", StringComparison.OrdinalIgnoreCase);
            var isIntl = currentEndpointId.EndsWith("-intl", StringComparison.OrdinalIgnoreCase);

            if (isCoding)
            {
                var codingMatch = candidates.FirstOrDefault(e =>
                    e.Id.StartsWith("coding-", StringComparison.OrdinalIgnoreCase));
                if (codingMatch != null)
                    return codingMatch;
            }
            else if (isIntl)
            {
                var intlMatch = candidates.FirstOrDefault(e =>
                    e.Id.EndsWith("-intl", StringComparison.OrdinalIgnoreCase));
                if (intlMatch != null)
                    return intlMatch;
            }
            else
            {
                var standardMatch = candidates.FirstOrDefault(e =>
                    !e.Id.StartsWith("coding-", StringComparison.OrdinalIgnoreCase) &&
                    !e.Id.EndsWith("-intl", StringComparison.OrdinalIgnoreCase));
                if (standardMatch != null)
                    return standardMatch;
            }
        }

        return candidates[0];
    }

    /// <summary>归一化 Base URL 用于端点匹配：裁剪空白与尾斜杠，并剥离常见完整路径后缀。</summary>
    public static string NormalizeUrlForMatch(string? baseUrl)
    {
        var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
            return string.Empty;

        if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            value = value[..^17];
        else if (value.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            value = value[..^12];
        else if (value.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            value = value[..^9];

        return value.TrimEnd('/');
    }

    /// <summary>根据旧版配置中的协议与 Base URL 反推对应服务商预设及端点。</summary>
    public static (string PresetId, string EndpointId) InferPresetFromConfig(string? provider, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (CustomId, string.Empty);

        var normalized = NormalizeUrlForMatch(baseUrl);
        if (normalized.Length == 0)
            return (CustomId, string.Empty);

        var normalizedProvider = provider?.ToLowerInvariant() switch
        {
            "claude" or "anthropic" => "anthropic",
            "ollama" or "local" => "local",
            _ => "openai"
        };

        (string PresetId, string EndpointId)? anyProtocolMatch = null;

        foreach (var preset in All)
        {
            if (preset.IsCustom)
                continue;

            foreach (var endpoint in preset.Endpoints)
            {
                var endpointNormalized = NormalizeUrlForMatch(endpoint.BaseUrl);
                var isMatch = string.Equals(normalized, endpointNormalized, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(endpoint.Protocol, "anthropic", StringComparison.OrdinalIgnoreCase) &&
                     !endpointNormalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(normalized, $"{endpointNormalized}/v1", StringComparison.OrdinalIgnoreCase));

                if (!isMatch)
                    continue;

                if (string.Equals(endpoint.Protocol, normalizedProvider, StringComparison.OrdinalIgnoreCase))
                    return (preset.Id, endpoint.Id);

                anyProtocolMatch ??= (preset.Id, endpoint.Id);
            }
        }

        if (anyProtocolMatch.HasValue)
            return anyProtocolMatch.Value;

        if (string.Equals(normalized, "https://api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            return ("deepseek", "openai");
        if (string.Equals(normalized, "https://api.anthropic.com", StringComparison.OrdinalIgnoreCase))
            return ("anthropic", "anthropic");
        if (string.Equals(normalized, "https://api.openai.com", StringComparison.OrdinalIgnoreCase))
            return ("openai", "openai");
        if (string.Equals(normalized, "http://localhost:11434", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "http://127.0.0.1:11434", StringComparison.OrdinalIgnoreCase))
            return ("ollama", "ollama");
        if (string.Equals(normalized, "http://localhost:1234", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "http://127.0.0.1:1234", StringComparison.OrdinalIgnoreCase))
            return ("ollama", "lmstudio");

        return (CustomId, string.Empty);
    }
}
