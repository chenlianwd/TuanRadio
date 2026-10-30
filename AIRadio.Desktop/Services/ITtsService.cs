using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

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
    /// 将文本合成为音频数据（可取消；调用方透传取消可提前终止内部合成，
    /// 让实现方尽快释放连接锁——等待与在途收发均可中断——而非占满其总限时）。
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, string voiceId, string emotion, CancellationToken cancellationToken);

    /// <summary>
    /// 获取可用的语音列表。
    /// </summary>
    Task<IReadOnlyList<VoiceOption>> GetVoicesAsync();
}
