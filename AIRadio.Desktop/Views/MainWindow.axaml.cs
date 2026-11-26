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
    private IDisposable? _playingSub;
    private CancellationTokenSource? _glowCts;
    private CancellationTokenSource? _spinCts;
    private volatile float _ttsTargetLevel;
    private double _spinTargetDegreesPerSecond;
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
        PropertyChanged += OnWindowVisualStateChanged;

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
                    _playingSub?.Dispose();
                    _playingSub = null;
                    StopGlow();
                    StopSpin(resetAngle: true);
                    if (_djVisualCueHandler != null)
                    {
                        _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                        _activeVm.DjVisualCue -= _djVisualCueHandler;
                    }
                    if (_ttsLevelHandler != null)
                    {
                        _activeVm.ChatVM.TtsLevelChanged -= _ttsLevelHandler;
                    }
                    _activeVm.RequestHideWindow -= OnRequestHideWindow;
                    _activeVm.RequestExitWindow -= OnRequestExitWindow;
                }

                _activeVm = vm;
                vm.RequestHideWindow += OnRequestHideWindow;
                vm.RequestExitWindow += OnRequestExitWindow;
                _djVisualCueHandler = OnDjVisualCue;
                vm.ChatVM.DjVisualCue += _djVisualCueHandler;
                vm.DjVisualCue += _djVisualCueHandler;
                _ttsLevelHandler = OnTtsLevelChanged;
                vm.ChatVM.TtsLevelChanged += _ttsLevelHandler;

                // 说话中徽章辉光随 TTS 播放链真实音量起伏；黑胶盘按 30°/s（说话）与 15°/s（播歌）无缝双速运转
                _speakingSub = vm.ChatVM.WhenAnyValue(x => x.IsSpeaking)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => UpdateAvatarVisualLoops());

                _playingSub = vm.PlayerVM.WhenAnyValue(x => x.IsPlaying)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => UpdateAvatarVisualLoops());

                // 简洁模式切换时收缩/还原窗口尺寸并同步视觉活跃门控；订阅先于 InitializeAsync 的模式恢复
                _compactModeSub = vm.WhenAnyValue(x => x.IsCompactMode)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(compact =>
                    {
                        ApplyCompactMode(compact);
                        UpdateAvatarVisualLoops();
                    });

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

    public bool IsExplicitExit { get; set; }

    private void OnRequestHideWindow() => Hide();

    private void OnRequestExitWindow()
    {
        IsExplicitExit = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (IsExplicitExit)
        {
            base.OnClosing(e);
            return;
        }

        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            var action = vm.SettingsVM.CloseAction;
            var prompted = vm.SettingsVM.HasPromptedCloseAction;

            if (action == "exit")
            {
                base.OnClosing(e);
                return;
            }

            if (action == "tray" && prompted)
            {
                e.Cancel = true;
                vm.CloseOverlays();
                Hide();
                return;
            }

            // 首次关闭或设置为每次询问：弹出确认浮层（若处于简洁模式先切回标准模式以展示浮层）
            if (vm.IsCompactMode)
                vm.IsCompactMode = false;
            e.Cancel = true;
            vm.CloseDialogIsTray = action != "exit";
            vm.CloseDialogIsExit = action == "exit";
            vm.CloseDialogRemember = !prompted;
            vm.CloseOverlays();
            vm.IsCloseConfirmOpen = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    // 播放线程调用：只写目标值，UI 侧由辉光循环轮询消费，不触碰控件
    internal void OnTtsLevelChanged(float level) => _ttsTargetLevel = level;

    /// <summary>视觉活跃门控：窗口可见、未最小化且处于标准模式时才运转黑胶与辉光动效。</summary>
    internal bool IsAvatarVisualActive
        => IsVisible && WindowState != WindowState.Minimized && (_activeVm == null || !_activeVm.IsCompactMode);

    private void OnWindowVisualStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
            UpdateAvatarVisualLoops();
    }

    private void UpdateAvatarVisualLoops()
    {
        var speaking = _activeVm?.ChatVM.IsSpeaking ?? false;
        var playing = _activeVm?.PlayerVM.IsPlaying ?? false;
        var visualActive = IsAvatarVisualActive;

        // 1. TTS 音量辉光仅在说话且视觉活跃时运行
        if (speaking && visualActive)
            StartGlow();
        else
            StopGlow();

        // 2. 黑胶单一角速度状态机：说话 30°/s、播歌 15°/s、空闲 0°/s；
        //    从 30°/s 切回 15°/s 时无缝降速不跳零，仅彻底停转（targetSpeed == 0）时回正 Angle = 0；
        //    隐藏/最小化/简洁模式仅暂停循环不回正，恢复可见后平滑续转。
        var targetSpeed = speaking ? 30.0 : playing ? 15.0 : 0.0;
        _spinTargetDegreesPerSecond = targetSpeed;

        if (targetSpeed <= 0)
        {
            StopSpin(resetAngle: true);
        }
        else if (!visualActive)
        {
            StopSpin(resetAngle: false);
        }
        else
        {
            EnsureSpinRunning();
        }
    }

    private void StartGlow()
    {
        if (_avatarBorder is not Border badge)
            return;
        if (_glowCts is { IsCancellationRequested: false })
            return;
        _glowCts?.Dispose();
        _glowCts = new CancellationTokenSource();
        _ = RunGlowAsync(badge, _glowCts.Token);
    }

    private void StopGlow()
    {
        _glowCts?.Cancel();
        _glowCts?.Dispose();
        _glowCts = null;
        if (_avatarBorder is Border badge)
            badge.Opacity = 1;
    }

    // 黑胶唱片旋转（电唱机隐喻）：
    // 旋转挂在内层 AvatarDisc 的 RotateTransform 上，与外层徽章的辉光（Opacity）、
    // 弹跳（TranslateTransform）各占各的通道互不干扰；按 _spinTargetDegreesPerSecond 双速平滑运转
    private void EnsureSpinRunning()
    {
        if (_avatarDisc is not Grid disc || disc.RenderTransform is not RotateTransform spin)
            return;
        if (_spinCts is { IsCancellationRequested: false })
            return;
        _spinCts?.Dispose();
        _spinCts = new CancellationTokenSource();
        _ = RunSpinAsync(spin, _spinCts.Token);
    }

    private void StopSpin(bool resetAngle)
    {
        _spinCts?.Cancel();
        _spinCts?.Dispose();
        _spinCts = null;
        // 停转回正：闲置态标签字母若停在任意角度会歪着，观感像缺陷
        if (resetAngle && _avatarDisc is Grid { RenderTransform: RotateTransform spinAfter })
            spinAfter.Angle = 0;
    }

    private async Task RunSpinAsync(RotateTransform spin, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var speed = _spinTargetDegreesPerSecond;
                if (speed > 0)
                    spin.Angle = (spin.Angle + speed * 0.033) % 360;
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
        try
        {
            while (!token.IsCancellationRequested)
            {
                var isLowGlowScene = (_activeVm?.IsSleepRadioMode ?? false) || (_activeVm?.IsFocusRadioMode ?? false);
                var minOpacity = isLowGlowScene ? 0.35f : 0.55f;
                var maxOpacity = isLowGlowScene ? 0.70f : 1.0f;
                var target = (float)Math.Sqrt(_ttsTargetLevel);
                _glowCurrent += (target - _glowCurrent) * 0.35f;
                badge.Opacity = minOpacity + (maxOpacity - minOpacity) * Math.Clamp(_glowCurrent, 0f, 1f);
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

    private void OnSleepTimerOptionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Button.OnClick 先触发 Click 事件再执行 Command；若同步 Hide() 会拆离 Flyout
        // 导致 Button.Command 绑定失效而跳过 SetSleepTimerCommand。推迟到当前点击管线结束后收起。
        Dispatcher.UIThread.Post(() =>
        {
            if (SleepTimerButton?.Flyout is { IsOpen: true } flyout)
                flyout.Hide();
        });
    }

    private void OnWebRemoteSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WebRemoteButton?.Flyout is { IsOpen: true } flyout)
                flyout.Hide();
        });
    }

    public void Dispose()
    {
        PropertyChanged -= OnWindowVisualStateChanged;
        _compactModeSub?.Dispose();
        _compactTopmostSub?.Dispose();
        _speakingSub?.Dispose();
        _playingSub?.Dispose();
        foreach (var timer in _iconReassertTimers) timer.Stop();
        _iconReassertTimers.Clear();
        StopGlow();
        StopSpin(resetAngle: false);
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
            _activeVm.RequestHideWindow -= OnRequestHideWindow;
            _activeVm.RequestExitWindow -= OnRequestExitWindow;
        }
    }
}
