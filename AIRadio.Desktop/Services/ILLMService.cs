using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 纯聊天接口，可接 OpenAI/Claude/Ollama 等兼容 LLM。
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
    /// 生成歌曲介绍文本（纯文本，不含语音）。
    /// </summary>
    Task<string> GenerateTrackIntroductionAsync(Track current, Track next);
}

/// <summary>
/// LLM 提供商配置。
/// </summary>
public record LLMConfig
{
    /// <summary>
    /// 提供商标识："openai", "claude", "deepseek", "ollama", "none"
    /// </summary>
    public string Provider { get; init; } = "none";

    /// <summary>
    /// API Key（Ollama 不需要）。
    /// </summary>
    public string ApiKey { get; init; } = "";

    /// <summary>
    /// 自定义端点（如 Ollama: http://localhost:11434/v1）。
    /// </summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>
    /// 模型名称（如 gpt-4o-mini, claude-3-haiku, deepseek-chat）。
    /// </summary>
    public string Model { get; init; } = "";
}
