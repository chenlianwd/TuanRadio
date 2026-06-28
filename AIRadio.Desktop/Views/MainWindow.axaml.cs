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

// Note: functionally equivalent to ChatView's MessageRoleToAlignmentConverter.
// Consider consolidating into a shared Converters/ directory (H15).
public class MessageAlignConverter : IValueConverter
{
    public static readonly MessageAlignConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is MessageRole role)
            return role == MessageRole.User ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        return Avalonia.Layout.HorizontalAlignment.Left;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => null;
}

public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is bool b ? !b : false;
}

public partial class MainWindow : Window, IDisposable
{
    // Must match MaxWidth in MainWindow.axaml chat message Border
    private const double ChatBubbleMaxWidth = 380;

    private System.Timers.Timer? _clockTimer;
    private Button? _themeButton;
    private ViewModels.MainWindowViewModel? _activeVm;
    private Border? _avatarBorder;
    private TextBlock? _avatarLetter;
    private Button? _micButton;
    private StarfieldView? _starfield;
    private Action<float[]>? _spectrumHandler;
    private IDisposable? _starfieldVisSub;
    private IDisposable? _searchDebounceSub;
    private IDisposable? _themeSub;
    private NotifyCollectionChangedEventHandler? _chatHandler;
    private Action<string, string>? _djVisualCueHandler;

    public MainWindow()
    {
        InitializeComponent();
        _avatarBorder = this.FindControl<Border>("AvatarBorder");
        _avatarLetter = this.FindControl<TextBlock>("AvatarLetter");
        _starfield = this.FindControl<StarfieldView>("Starfield");
        StartClock();
        FillDotFields();

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
                    if (_chatHandler != null)
                        _activeVm.ChatVM.Messages.CollectionChanged -= _chatHandler;
                    _themeSub?.Dispose();
                    _starfieldVisSub?.Dispose();
                    _searchDebounceSub?.Dispose();
                    if (_spectrumHandler != null)
                        _activeVm.SpectrumVM.SpectrumReceived -= _spectrumHandler;
                }

                _activeVm = vm;
                _djVisualCueHandler = OnDjVisualCue;
                vm.ChatVM.DjVisualCue += _djVisualCueHandler;
                vm.DjVisualCue += _djVisualCueHandler;
                vm.ChatVM.Messages.CollectionChanged -= _chatHandler;
                _chatHandler = OnChatMessagesChanged;
                vm.ChatVM.Messages.CollectionChanged += _chatHandler;
                _themeSub = vm.WhenAnyValue(x => x.IsDarkMode).Subscribe(isDark => UpdateThemeButtons(isDark));
                UpdateThemeButtons(vm.IsDarkMode);

                // Wire starfield: push spectrum data + bind visibility
                if (_spectrumHandler != null)
                    vm.SpectrumVM.SpectrumReceived -= _spectrumHandler;
                _spectrumHandler = data => _starfield?.PushSpectrum(data);
                vm.SpectrumVM.SpectrumReceived += _spectrumHandler;

                _starfieldVisSub?.Dispose();
                _starfieldVisSub = vm.SettingsVM.WhenAnyValue(x => x.EnableStarfield)
                    .Subscribe(v => { if (_starfield != null) _starfield.IsVisible = v; });

                // Keep search explicit. Auto-search can leave the UI waiting on a
                // partial keyword while the user is still typing.
                _searchDebounceSub?.Dispose();
                _searchDebounceSub = null;
            }
        };
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_activeVm != null)
                ApplyChatMessageTheme(_activeVm.IsDarkMode);
            this.FindControl<ScrollViewer>("ChatScrollViewer")?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void StartClock()
    {
        UpdateClock();
        _clockTimer = new System.Timers.Timer(1000);
        _clockTimer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateClock);
        _clockTimer.Start();
    }

    private void UpdateClock()
    {
        try
        {
            var now = DateTime.Now;
            if (this.FindControl<TextBlock>("ClockDisplay") is TextBlock clock)
                clock.Text = now.ToString("HH:mm");
            if (this.FindControl<TextBlock>("DayDisplay") is TextBlock day)
                day.Text = now.ToString("dddd");
            if (this.FindControl<TextBlock>("DateDisplay") is TextBlock date)
                date.Text = now.ToString("dd-MMM-yyyy").ToUpper();
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "UpdateClock failed"); }
    }

    private void OnProgressSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.PlayerVM.SeekTo(slider.Value);
        }
    }

    private void OnVolumeSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.PlayerVM.Volume = (float)slider.Value;
        }
    }

    private void OnChatInputKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.ChatVM.SendMessageCommand.Execute().Subscribe();
        }
    }

    private void OnMicPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            _micButton = sender as Button;
            if (_micButton != null)
            {
                _micButton.Background = new SolidColorBrush(Color.Parse("#FF56F5C4"));
                _micButton.Foreground = new SolidColorBrush(Color.Parse("#FF050507"));
            }
            (sender as Control)?.Focus();
            e.Pointer.Capture(sender as IInputElement);
            vm.ChatVM.BeginHoldToTalk();
            e.Handled = true;
        }
    }

    private void OnMicPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            ResetMicButton();
            vm.ChatVM.EndHoldToTalk();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnMicPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetMicButton();
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ChatVM.EndHoldToTalk();
    }

    private void ResetMicButton()
    {
        if (_micButton == null) return;
        _micButton.Background = new SolidColorBrush(Color.Parse("#33262835"));
        _micButton.Foreground = new SolidColorBrush(Color.Parse("#FFEDEDF5"));
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

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.PlaylistVM.SearchCommand.Execute().Subscribe();
        }
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var paths = await FilePickerHelper.PickAudioFilesAsync(topLevel);
        if (paths.Length == 0) return;

        vm.PlaylistVM.AddFiles(paths);
        vm.PlaylistVM.TabIndex = 0;
        vm.IsLibraryOpen = true;
    }

    private async void OnDjVisualCue(string expression, string motion)
    {
        if (_avatarBorder is Border border)
        {
            await Animations.PlayBounceAsync(border);
        }
    }

    private void OnCharacterSelected(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: Models.CharacterProfile character } &&
            DataContext is ViewModels.MainWindowViewModel vm)
        {
            if (character == vm.SelectedCharacter) return;

            if (_avatarBorder is Border border && _avatarLetter is TextBlock letter)
            {
                _ = Animations.PlayCharacterSwitchAsync(border, letter, character.DisplayName,
                    () => vm.SelectCharacterCommand.Execute(character).Subscribe());
            }
            else
            {
                vm.SelectCharacterCommand.Execute(character).Subscribe();
            }
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimizeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _clockTimer?.Stop();
        _clockTimer?.Dispose();
        _themeSub?.Dispose();
        _starfieldVisSub?.Dispose();
        _searchDebounceSub?.Dispose();
        if (_activeVm != null)
        {
            if (_djVisualCueHandler != null)
            {
                _activeVm.ChatVM.DjVisualCue -= _djVisualCueHandler;
                _activeVm.DjVisualCue -= _djVisualCueHandler;
            }
            if (_chatHandler != null)
                _activeVm.ChatVM.Messages.CollectionChanged -= _chatHandler;
            if (_spectrumHandler != null)
                _activeVm.SpectrumVM.SpectrumReceived -= _spectrumHandler;
        }
    }
}
