using AIRadio.Desktop.Models;
using AIRadio.Desktop.ViewModels;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>RadioState 派生优先级矩阵测试（spec §5.2.2 / §8.2）。</summary>
public class RadioStateDerivationTests
{
    [Fact]
    public void AllFalse_Idle() =>
        Assert.Equal(RadioState.Idle,
            MainWindowViewModel.DeriveRadioState(false, false, false, false, false));

    [Fact]
    public void HasFailure_HighestPriority() =>
        Assert.Equal(RadioState.Error,
            MainWindowViewModel.DeriveRadioState(true, true, true, true, true));

    [Fact]
    public void Speaking_Beats_Playing()
    {
        // TTS 串场时音乐被 ducked 但 IsPlaying 仍可能为 true —— 期望 Speaking
        Assert.Equal(RadioState.Speaking,
            MainWindowViewModel.DeriveRadioState(false, true, false, false, true));
    }

    [Fact]
    public void Searching_Beats_Curating() =>
        Assert.Equal(RadioState.Searching,
            MainWindowViewModel.DeriveRadioState(false, false, true, true, false));

    [Fact]
    public void Curating_When_OnlyProcessing() =>
        Assert.Equal(RadioState.Curating,
            MainWindowViewModel.DeriveRadioState(false, false, false, true, false));

    [Fact]
    public void Playing_When_OnlyPlaying() =>
        Assert.Equal(RadioState.Playing,
            MainWindowViewModel.DeriveRadioState(false, false, false, false, true));
}
