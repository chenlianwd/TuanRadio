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
    private IDisposable? _starfieldVisSub;
    private Action<float[]>? _spectrumHandler;
    private SpectrumViewModel? _spectrumSource;

    public ClockStage()
    {
        InitializeComponent();
        FillClockDots();
        DataContextChanged += OnDataContextChanged;
        AddHandler(Gestures.TappedEvent, OnStageTapped, RoutingStrategies.Bubble);
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
    /// 点击舞台切换 时钟↔歌词（与简洁模式单曲区"单触点按切歌词"同一手势语义，
    /// 与标题栏按钮共用 ToggleLyricsModeCommand 与记忆）。环境指示器（悬停 Tooltip）
    /// 区域点击不触发；无在播曲目不进入空歌词层。
    /// </summary>
    private void OnStageTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasCurrentTrack)
            return;

        if (e.Source is Visual source && EnvironmentIndicators is { } indicators
            && IsWithin(source, indicators))
            return;

        vm.ToggleLyricsModeCommand.Execute(Unit.Default).Subscribe();
    }

    private static bool IsWithin(Visual source, Visual ancestor)
        => ReferenceEquals(source, ancestor) || source.GetVisualAncestors().Contains(ancestor);
}
