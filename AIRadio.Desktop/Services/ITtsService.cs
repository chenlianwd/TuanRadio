using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.ViewModels;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 纯语音合成接口，当前使用 Edge TTS 实现。
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// 将文本合成为音频数据。
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, string voiceId, string emotion = "neutral");

    /// <summary>
    /// 获取可用的语音列表。
    /// </summary>
    Task<IReadOnlyList<VoiceOption>> GetVoicesAsync();
}
