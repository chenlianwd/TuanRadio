using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

public partial class MainWindow : Window, IDisposable
{
    private const double CompactWidth = 384;
    private const double CompactHeight = 100;
    private const double StandardMinWidth = 700;
    private const double StandardMinHeight = 700;

    private ViewModels.MainWindowViewModel? _activeVm;
    private Border? _avatarBorder;
    private Grid? _avatarDisc;
    private Action<string, string>? _djVisualCueHandler;
    private Action<float>? _ttsLevelHandler;
    private IDisposable? _speakingSub;
    private CancellationTokenSource? _glowCts;
    private CancellationTokenSource? _spinCts;
    private volatile float _ttsTargetLevel;
    private float _glowCurrent;
    private IDisposable? _compactModeSub;
    private IDisposable? _compactTopmostSub;
    private WindowBoundsSnapshot? _standardBounds;
    private readonly List<DispatcherTimer> _iconReassertTimers = [];

    // 任务栏按钮图标在窗口可见后才向窗口线程同步拉取；启动期 UI 线程若被卡住
    // （调试器符号加载、冷启动 IO），拉取超时后任务栏画灰色占位方块且不会重试，
    // 用户须最小化/还原才能恢复。可见后延时两次重发图标（新 WindowIcon 实例触发
    // WM_SETICON 与 Avalonia 自带的任务栏重绘提示）即可免点击自愈；图标内容相同，
    // 正常启动下重发无视觉变化。internal 供测试锚定两档（短档速愈+长档兜慢启动）。
    internal static readonly TimeSpan[] IconReassertDelays =
    [
        TimeSpan.FromSeconds(1.5),
        TimeSpan.FromSeconds(6)
    ];

    private readonly record struct WindowBoundsSnapshot(double Width, double Height, PixelPoint Position, WindowState State);

    public MainWindow()
    {
        InitializeComponent();
        InputFocusRelease.Attach(this);
        _avatarBorder = this.FindControl<Border>("AvatarBorder");
        _avatarDisc = this.FindControl<Grid>("AvatarDisc");
        Opened += OnOpenedScheduleTaskbarIconReassert;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                if (_activeVm != null)
                {
                    _compactModeSub?.Dispose();
                    _compactModeSub = null;
                    _compactTopmostSub?.Dispose();
                    _compactTopmostSub = null;
                    _speakingSub?.Dispose();
                    _speakingSub = null;
                    StopGlow();
                    StopSpin();
                    if (_djVisualCueHandler != null)
                    {
                        _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                        _activeVm.DjVisualCue -= _djVisualCueHandler;
                    }
                    if (_ttsLevelHandler != null)
                    {
                        _activeVm.ChatVM.TtsLevelChanged -= _ttsLevelHandler;
                    }
                }

                _activeVm = vm;
                _djVisualCueHandler = OnDjVisualCue;
                vm.ChatVM.DjVisualCue += _djVisualCueHandler;
                vm.DjVisualCue += _djVisualCueHandler;
                _ttsLevelHandler = OnTtsLevelChanged;
                vm.ChatVM.TtsLevelChanged += _ttsLevelHandler;

                // 说话中徽章辉光随 TTS 播放链真实音量起伏；停止说话复位常亮
                _speakingSub = vm.ChatVM.WhenAnyValue(x => x.IsSpeaking)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(OnSpeakingChanged);

                // 简洁模式切换时收缩/还原窗口尺寸；订阅先于 InitializeAsync 的模式恢复
                _compactModeSub = vm.WhenAnyValue(x => x.IsCompactMode)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(ApplyCompactMode);

                // 简洁模式下置顶开关变化时实时生效（标准模式不置顶）
                _compactTopmostSub = vm.SettingsVM.WhenAnyValue(x => x.CompactModeTopmost)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(topmost =>
                    {
                        if (vm.IsCompactMode)
                            Topmost = topmost;
                    });

                // Keep search explicit. Auto-search can leave the UI waiting on a
                // partial keyword while the user is still typing.
            }
        };
    }

    private void ApplyCompactMode(bool compact)
    {
        if (compact)
        {
            // 保存标准模式边界以便还原；最大化状态先回到正常态才能收缩
            _standardBounds = new WindowBoundsSnapshot(Width, Height, Position, WindowState);
            WindowState = WindowState.Normal;
            MinWidth = CompactWidth;
            MinHeight = CompactHeight;
            Width = CompactWidth;
            Height = CompactHeight;
            Topmost = _activeVm is { SettingsVM.CompactModeTopmost: true };
        }
        else
        {
            Topmost = false;
            MinWidth = StandardMinWidth;
            MinHeight = StandardMinHeight;
            if (_standardBounds is { } bounds)
            {
                Width = bounds.Width;
                Height = bounds.Height;
                Position = bounds.Position;
                if (bounds.State == WindowState.Maximized)
                    WindowState = WindowState.Maximized;
            }
        }
    }

    private void OnOpenedScheduleTaskbarIconReassert(object? sender, EventArgs e)
    {
        Opened -= OnOpenedScheduleTaskbarIconReassert;
        foreach (var delay in IconReassertDelays)
        {
            var timer = new DispatcherTimer { Interval = delay };
            var pending = true;
            timer.Tick += (_, _) =>
            {
                if (!pending) return;
                pending = false;
                timer.Stop();
                ReassertTaskbarIcon();
            };
            timer.Start();
            _iconReassertTimers.Add(timer);
        }
    }

    // internal 供 headless 测试直驱（同 OnDjVisualCue 手法）；Avalonia 属性值按引用
    // 比较，新实例必然穿透 styled property 的相等短路、重发到平台层
    internal void ReassertTaskbarIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://AIRadio/Assets/airadio.ico"));
            Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Taskbar icon reassert failed");
        }
    }

    private void OnDismissOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.CloseOverlays();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ViewModels.MainWindowViewModel vm)
        {
            // 简洁模式下 Esc 优先用于还原标准模式
            if (vm.IsCompactMode)
                vm.ToggleCompactModeCommand.Execute(Unit.Default).Subscribe();
            else
                vm.CloseOverlays();
            e.Handled = true;
        }
    }

    private bool _bouncing;

    // internal 供 headless 测试直驱（事件订阅接线由 DataContextChanged 建立，同 ClockStage.RouteStageTap 手法）
    internal async void OnDjVisualCue(string expression, string motion)
    {
        if (_bouncing) return; // 串场/TTS 分段触发快于动画时长时避免多段 bounce 并发交错闪烁
        if (_avatarBorder is Border border)
        {
            try
            {
                _bouncing = true;
                await Animations.PlayBounceAsync(border);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "DJ avatar bounce animation failed");
            }
            finally
            {
                _bouncing = false;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    // 播放线程调用：只写目标值，UI 侧由辉光循环轮询消费，不触碰控件
    internal void OnTtsLevelChanged(float level) => _ttsTargetLevel = level;

    private void OnSpeakingChanged(bool speaking)
    {
        if (speaking)
        {
            StartGlow();
            StartSpin();
        }
        else
        {
            StopGlow();
            StopSpin();
        }
    }

    private void StartGlow()
    {
        if (_avatarBorder is not Border badge)
            return;
        _glowCts?.Cancel(); // 串场分段 true→false→true 快速翻转时避免双循环交错写
        _glowCts = new CancellationTokenSource();
        _ = RunGlowAsync(badge, _glowCts.Token);
    }

    private void StopGlow()
    {
        _glowCts?.Cancel();
        if (_avatarBorder is Border badge)
            badge.Opacity = 1;
    }

    // 说话期黑胶唱片旋转（电唱机隐喻，2026-11-03 用户裁决的头像方案 A）：
    // 旋转挂在内层 AvatarDisc 的 RotateTransform 上，与外层徽章的辉光（Opacity）、
    // 弹跳（TranslateTransform）各占各的通道互不干扰；45°/s 慢转与辉光同 33ms 节拍
    private void StartSpin()
    {
        if (_avatarDisc is not Grid disc || disc.RenderTransform is not RotateTransform spin)
            return;
        _spinCts?.Cancel();
        _spinCts = new CancellationTokenSource();
        _ = RunSpinAsync(spin, _spinCts.Token);
    }

    private void StopSpin()
    {
        _spinCts?.Cancel();
        // 停转回正：闲置态标签字母若停在任意角度会歪着，观感像缺陷
        if (_avatarDisc is Grid { RenderTransform: RotateTransform spinAfter })
            spinAfter.Angle = 0;
    }

    private static async Task RunSpinAsync(RotateTransform spin, CancellationToken token)
    {
        // 30°/s（12s/圈）：网易云黑胶约 18°/s 的慢转观感，34px 小盘稍快以保旋转可辨
        const double degreesPerSecond = 30;
        try
        {
            while (!token.IsCancellationRequested)
            {
                spin.Angle = (spin.Angle + degreesPerSecond * 0.033) % 360;
                await Task.Delay(33, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止后的角度回正由 StopSpin 负责
        }
        catch (Exception ex)
        {
            // 循环任务被丢弃（_ =）：非取消异常不捕获会成为 unobserved task exception
            Serilog.Log.Debug(ex, "TTS disc spin loop failed");
        }
    }

    // 说话期徽章辉光：目标取播放链实时峰值（γ=0.5 抬高弱信号的可感对比），30fps
    // 线性趋近——有声抬向 1、句间停顿落回 0.55 基线，徽章随 DJ 真实音量呼吸而非常亮
    private async Task RunGlowAsync(Border badge, CancellationToken token)
    {
        const float minOpacity = 0.55f;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var target = (float)Math.Sqrt(_ttsTargetLevel);
                _glowCurrent += (target - _glowCurrent) * 0.35f;
                badge.Opacity = minOpacity + (1 - minOpacity) * Math.Clamp(_glowCurrent, 0f, 1f);
                await Task.Delay(33, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止说话的透明度复位由 StopGlow 负责
        }
        catch (Exception ex)
        {
            // 循环任务被丢弃（_ =）：非取消异常不捕获会成为 unobserved task exception
            // 静默杀死辉光，至少留一条日志可查
            Serilog.Log.Debug(ex, "TTS badge glow loop failed");
        }
    }

    public void Dispose()
    {
        _compactModeSub?.Dispose();
        _compactTopmostSub?.Dispose();
        _speakingSub?.Dispose();
        foreach (var timer in _iconReassertTimers) timer.Stop();
        _iconReassertTimers.Clear();
        StopGlow();
        StopSpin();
        if (_activeVm != null)
        {
            if (_djVisualCueHandler != null)
            {
                _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                _activeVm.DjVisualCue -= _djVisualCueHandler;
            }
            if (_ttsLevelHandler != null)
            {
                _activeVm.ChatVM.TtsLevelChanged -= _ttsLevelHandler;
            }
        }
    }
}
