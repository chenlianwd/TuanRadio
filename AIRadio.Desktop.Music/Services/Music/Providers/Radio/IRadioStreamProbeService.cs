using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services.Music;

public sealed record StreamProbeResult(
    bool Success,
    string? StationName,
    string? Codec,
    int? BitrateKbps,
    string? Tags,
    bool LooksLikeStaticFile,
    string? ErrorMessage);

/// <summary>
/// 轻量网络电台流媒体探测服务（仅读取响应头与 ICY 元数据，严禁下载正文）。
/// </summary>
public interface IRadioStreamProbeService
{
    Task<StreamProbeResult> ProbeAsync(string streamUrl, CancellationToken ct = default);
}
