using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace AIRadio.Desktop.Views;

/// <summary>播放器面板。seek/volume 释放在此处理（spec §5.5）。</summary>
public partial class PlayerDeck : UserControl
{
    public PlayerDeck()
    {
        InitializeComponent();
    }

    private void OnProgressSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainWindowViewModel vm)
            vm.PlayerVM.SeekTo(slider.Value);
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainWindowViewModel vm)
            vm.PlayerVM.Volume = (float)slider.Value;
    }
}
