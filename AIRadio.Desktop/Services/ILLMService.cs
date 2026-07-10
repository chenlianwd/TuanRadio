using System.Collections.Generic;
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
