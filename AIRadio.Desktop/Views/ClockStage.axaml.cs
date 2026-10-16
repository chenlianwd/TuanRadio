using System;
using System.Linq;
using System.Reactive;
using AIRadio.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

/// <summary>时钟舞台：时钟（绑 VM.Now）+ ClockDots 装饰 + Starfield 自订阅 SpectrumVM（spec §5.5）。</summary>
public partial class ClockStage : UserControl
{
    // 歌词中栏宽度契约：固定 540 在默认 760 宽窗口会把两侧频谱列（*）压到 ~68px，
    // 32 根柱被钳到 2px 下限成细条；按舞台可用宽 55% 自适应、540 封顶，
    // 下限兜底极窄窗口。两次尺寸变化之间保持定值——逐行歌词长度变化
    // 依旧不带动频谱（原固定宽的防抖语义保留）。
    private const double LyricsPanelMaxWidth = 540;
    private const double LyricsPanelMinWidth = 220;
    private const double LyricsLayerHorizontalMargin = 48; // LyricsLayer 左右 Margin 24
    private const double LyricsPanelWidthRatio = 0.55;

    private IDisposable? _starfieldVisSub;
    private Action<float[]>? _spectrumHandler;
    private SpectrumViewModel? _spectrumSource;

    public ClockStage()
    {
        InitializeComponent();
        FillClockDots();
        DataContextChanged += OnDataContextChanged;
        AddHandler(Gestures.TappedEvent, OnStageTapped, RoutingStrategies.Bubble);
        StageRoot.SizeChanged += OnStageSizeChanged;
    }

    private void OnStageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (LyricsPanel is not { } panel)
            return;
        var available = e.NewSize.Width - LyricsLayerHorizontalMargin;
        panel.Width = Math.Clamp(available * LyricsPanelWidthRatio, LyricsPanelMinWidth, LyricsPanelMaxWidth);
    }

    private void FillClockDots()
    {
        var line = string.Join("  ", Enumerable.Repeat(".", 74));
        var field = string.Join(Environment.NewLine, Enumerable.Repeat(line, 20));
        if (ClockDots is { } dots) dots.Text = field;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _starfieldVisSub?.Dispose();
        if (_spectrumSource != null && _spectrumHandler != null)
            _spectrumSource.SpectrumReceived -= _spectrumHandler;

        _spectrumSource = null;
        _spectrumHandler = null;

        if (DataContext is MainWindowViewModel vm)
        {
            _spectrumHandler = data => Starfield?.PushSpectrum(data);
            _spectrumSource = vm.SpectrumVM;
            _spectrumSource.SpectrumReceived += _spectrumHandler;

            _starfieldVisSub = vm.SettingsVM.WhenAnyValue(x => x.EnableStarfield)
                .Subscribe(v => { if (Starfield != null) Starfield.IsVisible = v; });
        }
    }

    /// <summary>
    /// 点击舞台分区域路由（与简洁模式单曲区"单触点按切歌词"同一手势语义）：
    /// 中栏（时钟/歌词面板）切换歌词模式（复用 ToggleLyricsModeCommand 与记忆，无在播曲目静默）；
    /// 两侧频谱条循环频谱样式（频谱/歌词两种图层下的实例均可点）。
    /// 空白背景与环境指示器不属于任何区域，点击无动作。
    /// internal 供 headless 测试直驱（真实指针无法合成，见 PlayerDeckFeedbackButtonTests 注）。
    /// </summary>
    private void OnStageTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && DataContext is MainWindowViewModel vm)
            RouteStageTap(source, vm);
    }

    internal void RouteStageTap(Visual source, MainWindowViewModel vm)
    {
        if ((ClockPanel is { } clock && IsWithin(source, clock)) ||
            (LyricsPanel is { } lyrics && IsWithin(source, lyrics)))
        {
            if (vm.HasCurrentTrack)
                vm.ToggleLyricsModeCommand.Execute(Unit.Default).Subscribe();
        }
        else if (IsSpectrumTap(source))
        {
            vm.CycleSpectrumStyleCommand.Execute(Unit.Default).Subscribe();
        }
    }

    private bool IsSpectrumTap(Visual source)
        => (ClockSpectrumLeft is { } left1 && IsWithin(source, left1))
        || (ClockSpectrumRight is { } right1 && IsWithin(source, right1))
        || (LyricsSpectrumLeft is { } left2 && IsWithin(source, left2))
        || (LyricsSpectrumRight is { } right2 && IsWithin(source, right2));

    private static bool IsWithin(Visual source, Visual ancestor)
        => ReferenceEquals(source, ancestor) || source.GetVisualAncestors().Contains(ancestor);
}
