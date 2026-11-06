using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 系统托盘服务：右下角通知区域图标常驻、双击唤出窗口、右键播控与彻底退出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IAudioService _audioService;
    private readonly MainWindowViewModel _mainVm;
    private Views.MainWindow? _window;
    private TrayIcon? _trayIcon;
    private IDisposable? _trackSub;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _exitItem;
    private readonly Action _onLanguageChanged;
    private bool _disposed;

    public TrayIconService(IAudioService audioService, MainWindowViewModel mainVm)
    {
        _audioService = audioService;
        _mainVm = mainVm;
        _onLanguageChanged = RefreshLocalizedMenu;
        AppLanguage.Changed += _onLanguageChanged;
    }

    public void TryAttach(Views.MainWindow window)
    {
        if (_disposed || _trayIcon != null)
            return;

        _window = window;

        try
        {
            var iconUri = new Uri("avares://AIRadio/Assets/airadio.ico");
            var windowIcon = new WindowIcon(AssetLoader.Open(iconUri));

            _trayIcon = new TrayIcon
            {
                Icon = windowIcon,
                ToolTipText = "TuanRadio FM",
                IsVisible = true
            };

            var menu = new NativeMenu();

            _showItem = new NativeMenuItem(AppLanguage.T("显示主界面", "Show Main Window"));
            _showItem.Click += (_, _) => RestoreWindow();
            menu.Items.Add(_showItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            _playPauseItem = new NativeMenuItem(AppLanguage.T("播放 / 暂停", "Play / Pause"));
            _playPauseItem.Click += (_, _) => _mainVm.PlayerVM.PlayCommand.Execute().Subscribe();
            menu.Items.Add(_playPauseItem);

            _nextItem = new NativeMenuItem(AppLanguage.T("下一首", "Next Track"));
            _nextItem.Click += (_, _) => _mainVm.PlayerVM.NextCommand.Execute().Subscribe();
            menu.Items.Add(_nextItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            _exitItem = new NativeMenuItem(AppLanguage.T("退出 TuanRadio", "Exit TuanRadio"));
            _exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(_exitItem);

            _trayIcon.Menu = menu;
            _trayIcon.Clicked += (_, _) => RestoreWindow();

            if (Application.Current != null)
            {
                var icons = TrayIcon.GetIcons(Application.Current);
                icons?.Add(_trayIcon);
            }

            _trackSub = _audioService.TrackChanged
                .Subscribe(track => UpdateToolTip(track));

            Log.Information("TrayIcon attached successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to attach TrayIcon");
        }
    }

    public void RestoreWindow()
    {
        if (_window == null)
            return;

        if (!_window.IsVisible)
            _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    public void ExitApplication()
    {
        if (_window != null)
        {
            _window.IsExplicitExit = true;
            _window.Close();
        }
        else
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private void UpdateToolTip(Track? track)
    {
        if (_trayIcon == null)
            return;

        _trayIcon.ToolTipText = track != null
            ? $"TuanRadio FM - {track.Title} · {track.Artist}"
            : "TuanRadio FM";
    }

    private void RefreshLocalizedMenu()
    {
        if (_showItem != null)
            _showItem.Header = AppLanguage.T("显示主界面", "Show Main Window");
        if (_playPauseItem != null)
            _playPauseItem.Header = AppLanguage.T("播放 / 暂停", "Play / Pause");
        if (_nextItem != null)
            _nextItem.Header = AppLanguage.T("下一首", "Next Track");
        if (_exitItem != null)
            _exitItem.Header = AppLanguage.T("退出 TuanRadio", "Exit TuanRadio");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        AppLanguage.Changed -= _onLanguageChanged;
        _trackSub?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            if (Application.Current != null)
            {
                try
                {
                    var icons = TrayIcon.GetIcons(Application.Current);
                    icons?.Remove(_trayIcon);
                }
                catch { }
            }
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        Log.Information("TrayIcon disposed");
    }
}
