using System;
using System.Linq;
using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using ReactiveUI;

namespace AIRadio.Desktop.Views;

/// <summary>时钟舞台：时钟（绑 VM.Now）+ ClockDots 装饰 + Starfield 自订阅 SpectrumVM（spec §5.5）。</summary>
public partial class ClockStage : UserControl
{
    private IDisposable? _starfieldVisSub;
    private Action<float[]>? _spectrumHandler;

    public ClockStage()
    {
        InitializeComponent();
        FillClockDots();
        DataContextChanged += OnDataContextChanged;
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
        if (DataContext is MainWindowViewModel vm)
        {
            if (_spectrumHandler != null)
                vm.SpectrumVM.SpectrumReceived -= _spectrumHandler;
            _spectrumHandler = data => Starfield?.PushSpectrum(data);
            vm.SpectrumVM.SpectrumReceived += _spectrumHandler;

            _starfieldVisSub = vm.SettingsVM.WhenAnyValue(x => x.EnableStarfield)
                .Subscribe(v => { if (Starfield != null) Starfield.IsVisible = v; });
        }
    }
}
