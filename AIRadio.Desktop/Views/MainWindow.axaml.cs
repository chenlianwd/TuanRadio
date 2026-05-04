using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI;
using Serilog;

namespace AIRadio.Desktop.Views;

public partial class MainWindow : Window
{
    private long _lastLipSyncTick;
    private System.Threading.Timer? _ttsLipTimer;
    private bool _isTtsLipSync;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.ChatVM.Live2DCommand += OnLive2DCommand;
                vm.Live2DCommand += OnLive2DCommand;
                vm.SpectrumVM.SpectrumReceived += OnSpectrumReceived;

                // TTS lip sync: when TTS plays, trigger mouth animation
                vm.PlayerVM.AudioService.TtsStateChanged.Subscribe(ttsPlaying =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (ttsPlaying)
                            StartTtsLipSync();
                        else
                            StopTtsLipSync();
                    });
                });
            }
        };
    }

    private void StartTtsLipSync()
    {
        _isTtsLipSync = true;
        _ttsLipTimer = new System.Threading.Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_isTtsLipSync) return;
                var now = Environment.TickCount64;
                if (now - _lastLipSyncTick < 66) return;
                _lastLipSyncTick = now;

                // Generate talking spectrum pattern
                var data = new float[16];
                var time = now / 1000.0;
                for (int i = 0; i < 16; i++)
                {
                    var baseLevel = i < 4 ? 0.4f : 0.15f; // bass-heavy for speech
                    data[i] = (float)(baseLevel + 0.2 * Math.Sin(time * 12 + i * 0.5));
                    data[i] = Math.Clamp(data[i], 0f, 1f);
                }
                try
                {
                    var json = "[" + string.Join(",", data.Select(v => v.ToString("F3"))) + "]";
                    Live2DWebView?.ExecuteScriptAsync($"updateLipSync({json})");
                }
                catch { }
            });
        }, null, 0, 66);
    }

    private void StopTtsLipSync()
    {
        _isTtsLipSync = false;
        _ttsLipTimer?.Dispose();
        _ttsLipTimer = null;
    }

    private async void OnSpectrumReceived(float[] data)
    {
        // Throttle to ~15fps for lip sync
        var now = Environment.TickCount64;
        if (now - _lastLipSyncTick < 66) return;
        _lastLipSyncTick = now;

        try
        {
            var json = "[" + string.Join(",", data.Select(v => v.ToString("F3"))) + "]";
            await Live2DWebView.ExecuteScriptAsync($"updateLipSync({json})");
        }
        catch { }
    }

    private async void OnLive2DCommand(string expression, string motion)
    {
        try
        {
            if (!string.IsNullOrEmpty(expression))
                await Live2DWebView.ExecuteScriptAsync($"setExpression('{expression}')");
            if (!string.IsNullOrEmpty(motion))
                await Live2DWebView.ExecuteScriptAsync($"playMotion('{motion}')");
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "Failed to send Live2D command");
        }
    }

    private void OnOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.IsSettingsOpen = false;
        }
    }

    private void OnCharacterOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.IsCharacterPickerOpen = false;
        }
    }

    private void OnCharacterSelected(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: Models.CharacterProfile character } &&
            DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.SelectCharacterCommand.Execute(character);
        }
    }
}
