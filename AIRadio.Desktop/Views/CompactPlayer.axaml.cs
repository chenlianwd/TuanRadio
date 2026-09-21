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
    private Point? _infoPressPos;
    private bool _infoPressed;
    private bool _lastInfoTappedToggle;
    private PointerPressedEventArgs? _lastPressedEventArgs;

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
        // 本控件常驻可视树、标准模式下不可见：跳过隐藏状态的逐帧属性写
        if (!IsVisible)
            return;

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
        // 命中行内按钮（收藏/置顶/展开/最小化/关闭）时不启动窗口拖动
        if (InteractionGuards.IsOverButton(e.Source))
            return;

        if (TopLevel.GetTopLevel(this) is Window window &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { window.BeginMoveDrag(e); }
            catch { /* 平台在部分状态下可能拒绝拖动 */ }
        }
    }

    private void OnTrackInfoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (InteractionGuards.IsOverButton(e.Source))
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _infoPressPos = e.GetPosition(this);
            _infoPressed = true;
            _lastPressedEventArgs = e;
            // 不在此重置 _lastInfoTappedToggle：DoubleTapped 经 RouteFinished 在整条按下路由
            // 完成后才触发，若此处重置，OnExpandDoubleTapped 的撤销分支永远读到 false
            e.Handled = true;
        }
    }

    private void OnTrackInfoMoved(object? sender, PointerEventArgs e)
    {
        if (!_infoPressed || _infoPressPos == null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _infoPressed = false;
            _lastPressedEventArgs = null;
            return;
        }

        var current = e.GetPosition(this);
        var distance = current - _infoPressPos.Value;
        if (Math.Abs(distance.X) > 4 || Math.Abs(distance.Y) > 4)
        {
            _infoPressed = false;
            var pressedArgs = _lastPressedEventArgs;
            _lastPressedEventArgs = null;
            if (pressedArgs != null && TopLevel.GetTopLevel(this) is Window window)
            {
                try { window.BeginMoveDrag(pressedArgs); }
                catch { /* 平台在部分状态下可能拒绝拖动 */ }
            }
        }
    }

    private void OnTrackInfoReleased(object? sender, PointerReleasedEventArgs e)
    {
        _lastPressedEventArgs = null;
        if (_infoPressed)
        {
            _infoPressed = false;
            _lastInfoTappedToggle = true;
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ToggleCompactLyricsCommand?.Execute().Subscribe();
            }
            e.Handled = true;
        }
    }

    private void OnTrackInfoCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _infoPressed = false;
        _lastPressedEventArgs = null;
    }

    private void OnExpandDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (InteractionGuards.IsOverButton(e.Source))
            return;

        e.Handled = true;

        if (DataContext is MainWindowViewModel vm && vm.IsCompactMode)
        {
            // 只撤销发生在本信息区上的双击（行内其余区域的双击不应吞掉早前单击的切换）
            if (_lastInfoTappedToggle && ReferenceEquals(e.Source, TrackInfoArea))
            {
                // 双击还原：撤销第 1 次单击造成的歌词切换
                vm.ToggleCompactLyricsCommand?.Execute().Subscribe();
                _lastInfoTappedToggle = false;
            }
            // 展开会隐藏本控件：清掉按压态，防止隐藏后路由异常的游离 release 再次切换歌词
            _infoPressed = false;
            _lastPressedEventArgs = null;
            vm.ToggleCompactModeCommand.Execute(System.Reactive.Unit.Default).Subscribe();
        }
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
}
