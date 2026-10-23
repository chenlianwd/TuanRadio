using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 纯聊天接口，可接 OpenAI 兼容、Anthropic 兼容或本地 LLM。
/// </summary>
public interface ILLMService
{
    /// <summary>
    /// 配置 LLM 提供商。
    /// </summary>
    void Configure(LLMConfig config);

    /// <summary>
    /// 聊天对话。
    /// </summary>
    Task<string> ChatAsync(string userMessage, List<ChatMessage> history);

    /// <summary>
    /// 枚举本地模型名称（Ollama /api/tags）；baseUrl 为空走本地默认端点。
    /// 失败返回空列表。端点只从入参派生，不读服务的聊天配置——
    /// 设置页翻看 provider 下拉不应触碰运行中服务的配置。
    /// </summary>
    Task<List<string>> ListLocalModelsAsync(string? baseUrl, CancellationToken cancellationToken);
}

/// <summary>
/// LLM 提供商配置。
/// </summary>
public record LLMConfig
{
    /// <summary>
    /// 接口格式："openai", "anthropic", "local"
    /// </summary>
    public string Provider { get; init; } = "openai";

    /// <summary>
    /// API Key（本地模型通常不需要）。
    /// </summary>
    public string ApiKey { get; init; } = "";

    /// <summary>
    /// 自定义端点（如本地模型: http://localhost:11434/v1）。
    /// </summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>
    /// 模型名称（如 gpt-4o-mini, claude-3-haiku, deepseek-chat）。
    /// </summary>
    public string Model { get; init; } = "";
}
