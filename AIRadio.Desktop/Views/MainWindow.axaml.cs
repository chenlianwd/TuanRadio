using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AIRadio.Desktop.Models;
using ReactiveUI;
using Serilog;

namespace AIRadio.Desktop.Views;

public partial class MainWindow : Window, IDisposable
{
    private Button? _themeButton;
    private ViewModels.MainWindowViewModel? _activeVm;
    private Border? _avatarBorder;
    private IDisposable? _searchDebounceSub;
    private IDisposable? _themeSub;
    private Action<string, string>? _djVisualCueHandler;

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
                    if (_djVisualCueHandler != null)
                    {
                        _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                        _activeVm.DjVisualCue -= _djVisualCueHandler;
                    }
                    _themeSub?.Dispose();
                    _searchDebounceSub?.Dispose();
                }

                _activeVm = vm;
                _djVisualCueHandler = OnDjVisualCue;
                vm.ChatVM.DjVisualCue += _djVisualCueHandler;
                vm.DjVisualCue += _djVisualCueHandler;
                _themeSub = vm.WhenAnyValue(x => x.IsDarkMode).Subscribe(isDark => UpdateThemeButtons(isDark));
                UpdateThemeButtons(vm.IsDarkMode);

                // Keep search explicit. Auto-search can leave the UI waiting on a
                // partial keyword while the user is still typing.
                _searchDebounceSub?.Dispose();
                _searchDebounceSub = null;
            }
        };
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
        _themeSub?.Dispose();
        _searchDebounceSub?.Dispose();
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
