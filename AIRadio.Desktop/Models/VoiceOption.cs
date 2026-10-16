namespace AIRadio.Desktop.Models;

/// <summary>
/// TTS 可选语音的展示项（Id + 本地化名称）。曾驻 ViewModels/SettingsViewModel，
/// 因 ITtsService 接口契约引用而迫使服务层反向依赖 VM 层，迁至 Models 归位。
/// </summary>
public class VoiceOption
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
}
