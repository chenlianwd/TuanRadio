using System;
using System.Reactive.Linq;
using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

/// <summary>播放器面板。seek/volume 释放与左侧三连电平条微动在此处理（spec §5.5）。</summary>
public partial class PlayerDeck : UserControl
{
    private MainWindowViewModel? _boundVm;
    private IDisposable? _playingSub;
    private readonly double[] _meterLevels = [1.0, 1.0, 1.0];
    private bool _meterAtBaseline = true;

    public PlayerDeck()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainWindowViewModel);
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as MainWindowViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);

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
        // 系统打断拖拽（窗口被激活事件抢占、触摸取消等）时 PointerReleased 不会送达，
        // 捕获丢失兜底结束拖拽，防 _isDragging 悬置把进度条 thumb 冻结在断点
        ProgressSlider.AddHandler(
            InputElement.PointerCaptureLostEvent,
            OnProgressSliderCaptureLost);
    }

    private void AttachViewModel(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_boundVm, vm))
            return;
        _playingSub?.Dispose();
        _playingSub = null;
        if (_boundVm != null)
            _boundVm.SpectrumVM.SpectrumReceived -= OnSpectrumReceived;
        _boundVm = vm;
        if (_boundVm != null)
        {
            _boundVm.SpectrumVM.SpectrumReceived += OnSpectrumReceived;
            // 暂停播放后音频服务可能立即停推频谱帧，显式监听 IsPlaying 归零复位，防止电平条冻结在半空
            _playingSub = _boundVm.PlayerVM.WhenAnyValue(x => x.IsPlaying)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(playing =>
                {
                    if (!playing)
                        ResetMeterToBaseline();
                });
        }
    }

    private void ResetMeterToBaseline()
    {
        if (_meterAtBaseline)
            return;
        _meterLevels[0] = 1.0;
        _meterLevels[1] = 1.0;
        _meterLevels[2] = 1.0;
        ApplyMeterScales();
        _meterAtBaseline = true;
    }

    private void OnSpectrumReceived(float[] data)
    {
        // 窗口最小化、隐藏托盘或切入简洁模式时零写入降耗
        if (!IsVisible || _boundVm is null || _boundVm.IsCompactMode ||
            TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized })
            return;

        var isPlaying = _boundVm.PlayerVM.IsPlaying;
        if (!isPlaying || data.Length == 0)
        {
            if (_meterAtBaseline)
                return;

            var settled = true;
            for (var i = 0; i < 3; i++)
            {
                _meterLevels[i] += (1.0 - _meterLevels[i]) * 0.35;
                if (Math.Abs(_meterLevels[i] - 1.0) > 0.02)
                    settled = false;
                else
                    _meterLevels[i] = 1.0;
            }
            ApplyMeterScales();
            _meterAtBaseline = settled;
            return;
        }

        _meterAtBaseline = false;
        var third = Math.Max(1, data.Length / 3);
        var low = AverageSlice(data, 0, third);
        var mid = AverageSlice(data, third, Math.Min(data.Length, third * 2));
        var high = AverageSlice(data, Math.Min(data.Length - 1, third * 2), data.Length);

        var targets0 = 0.48 + low * 0.92;
        var targets1 = 0.42 + mid * 0.72;
        var targets2 = 0.52 + high * 1.05;

        _meterLevels[0] += (targets0 - _meterLevels[0]) * 0.40;
        _meterLevels[1] += (targets1 - _meterLevels[1]) * 0.40;
        _meterLevels[2] += (targets2 - _meterLevels[2]) * 0.40;
        ApplyMeterScales();
    }

    private static double AverageSlice(float[] data, int start, int end)
    {
        if (end <= start)
            return 0;
        double sum = 0;
        for (var i = start; i < end; i++)
            sum += Math.Clamp(data[i], 0f, 1f);
        return sum / (end - start);
    }

    private void ApplyMeterScales()
    {
        if (MiniMeterBar0?.RenderTransform is ScaleTransform s0)
            s0.ScaleY = Math.Clamp(_meterLevels[0], 0.35, 1.55);
        if (MiniMeterBar1?.RenderTransform is ScaleTransform s1)
            s1.ScaleY = Math.Clamp(_meterLevels[1], 0.35, 1.05);
        if (MiniMeterBar2?.RenderTransform is ScaleTransform s2)
            s2.ScaleY = Math.Clamp(_meterLevels[2], 0.35, 1.85);
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

    private void OnProgressSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // 正常松开时捕获也会丢失并走到这里：EndSeek 幂等（重复 Seek 同值无害）
        if (DataContext is MainWindowViewModel vm)
            vm.PlayerVM.EndSeek(ProgressSlider.Value);
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is MainWindowViewModel vm)
            vm.PlayerVM.Volume = (float)slider.Value;
    }
}
