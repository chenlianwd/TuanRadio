using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIRadio.Desktop.Views;

/// <summary>播放器面板。seek/volume 释放在此处理（spec §5.5）。</summary>
public partial class PlayerDeck : UserControl
{
    public PlayerDeck()
    {
        InitializeComponent();

        // Slider 模板内的 Thumb/RepeatButton 会把 Pointer 事件标记为 handled，
        // XAML 属性挂接在拖动 thumb 时收不到事件，必须以 handledEventsToo 订阅
        ProgressSlider.AddHandler(
            InputElement.PointerPressedEvent,
            OnProgressSliderPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        ProgressSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            OnProgressSliderReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnProgressSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PlayerVM.StartSeek();
    }

    private void OnProgressSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PlayerVM.EndSeek(ProgressSlider.Value);
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainWindowViewModel vm)
            vm.PlayerVM.Volume = (float)slider.Value;
    }
}
