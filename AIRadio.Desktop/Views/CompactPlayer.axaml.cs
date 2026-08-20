using System;
using AIRadio.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>简洁模式两行紧凑卡：行 1 拖动/双击还原，行 2 播放控制 + 进度 + 迷你频谱。</summary>
public partial class CompactPlayer : UserControl
{
    private const float MinBarHeight = 2f;
    private const float MaxBarHeight = 18f;
    private MainWindowViewModel? _currentVm;

    public CompactPlayer()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Slider 模板内的 Thumb/RepeatButton 会吞掉 Pointer 事件，必须 handledEventsToo 订阅（同 PlayerDeck）
        CompactProgressSlider.AddHandler(
            InputElement.PointerPressedEvent,
            OnProgressSliderPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        CompactProgressSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            OnProgressSliderReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null)
            _currentVm.SpectrumVM.SpectrumReceived -= OnSpectrumReceived;

        _currentVm = DataContext as MainWindowViewModel;
        if (_currentVm != null)
            _currentVm.SpectrumVM.SpectrumReceived += OnSpectrumReceived;
    }

    private void OnSpectrumReceived(float[] data)
    {
        // SpectrumViewModel 已把事件切到 UI 线程；只更新 8 段小柱高度，开销可忽略
        var bars = SpectrumPanel.Children;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i] is not Border bar)
                continue;

            var value = i < data.Length ? data[i] : 0f;
            bar.Height = Math.Clamp(MinBarHeight + value * (MaxBarHeight - MinBarHeight), MinBarHeight, MaxBarHeight);
        }
    }

    private void OnProgressSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PlayerVM.StartSeek();
    }

    private void OnProgressSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.PlayerVM.EndSeek(CompactProgressSlider.Value);
    }

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // 命中行内按钮（收藏/最小化/关闭/展开）时不启动窗口拖动
        if (IsOverButton(e.Source))
            return;

        if (TopLevel.GetTopLevel(this) is Window window &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { window.BeginMoveDrag(e); }
            catch { /* 平台在部分状态下可能拒绝拖动 */ }
        }
    }

    private void OnExpandDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsOverButton(e.Source))
            return;

        if (DataContext is MainWindowViewModel vm && vm.IsCompactMode)
            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Close();
    }

    private static bool IsOverButton(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is Button)
                return true;

            visual = visual.GetVisualParent();
        }

        return false;
    }
}
