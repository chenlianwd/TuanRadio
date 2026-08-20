using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private Action<string, string>? _djVisualCueHandler;
    private IDisposable? _compactModeSub;
    private WindowBoundsSnapshot? _standardBounds;

    private readonly record struct WindowBoundsSnapshot(double Width, double Height, PixelPoint Position, WindowState State);

    public MainWindow()
    {
        InitializeComponent();
        _avatarBorder = this.FindControl<Border>("AvatarBorder");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                if (_activeVm != null)
                {
                    _compactModeSub?.Dispose();
                    _compactModeSub = null;
                    if (_djVisualCueHandler != null)
                    {
                        _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                        _activeVm.DjVisualCue -= _djVisualCueHandler;
                    }
                }

                _activeVm = vm;
                _djVisualCueHandler = OnDjVisualCue;
                vm.ChatVM.DjVisualCue += _djVisualCueHandler;
                vm.DjVisualCue += _djVisualCueHandler;

                // 简洁模式切换时收缩/还原窗口尺寸；订阅先于 InitializeAsync 的模式恢复
                _compactModeSub = vm.WhenAnyValue(x => x.IsCompactMode)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(ApplyCompactMode);

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
            if (_activeVm is { SettingsVM.CompactModeTopmost: true })
                Topmost = true;
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

    private async void OnDjVisualCue(string expression, string motion)
    {
        if (_avatarBorder is Border border)
        {
            await Animations.PlayBounceAsync(border);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _compactModeSub?.Dispose();
        if (_activeVm != null)
        {
            if (_djVisualCueHandler != null)
            {
                _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                _activeVm.DjVisualCue -= _djVisualCueHandler;
            }
        }
    }
}
