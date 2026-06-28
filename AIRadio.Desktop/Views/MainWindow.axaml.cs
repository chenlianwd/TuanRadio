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

public partial class MainWindow : Window
{
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

    private void UpdateThemeButtons(bool isDark)
    {
        _themeButton ??= this.FindControl<Button>("ThemeButton");

        if (isDark)
        {
            if (_themeButton != null) { _themeButton.Background = new SolidColorBrush(Color.Parse("#FF171722")); _themeButton.Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")); }
            Background = new SolidColorBrush(Color.Parse("#FF08080B"));
            SetThemeColors(
                "#FF030305", "#FF050507", "#F0131320", "#CC1B1B2A", "#F008080D",
                "#E91B1B2A", "#F007070A", "#E9161623", "#66111113", "#F0222234",
                "#33494B66");
            SetShellTextForeground("#FFEDEDF5");
            ApplyChatMessageTheme(isDark);
        }
        else
        {
            if (_themeButton != null) { _themeButton.Background = new SolidColorBrush(Color.Parse("#FFFFFFFF")); _themeButton.Foreground = new SolidColorBrush(Color.Parse("#FF111118")); }
            Background = new SolidColorBrush(Color.Parse("#FFF5F1FF"));
            SetThemeColors(
                "#FFF5F1FF", "#FFE9E2F7", "#F8FFFFFF", "#EDEBE5FF", "#F7F8F6FF",
                "#ECEEE9FF", "#F4F3F8FF", "#F8FFFFFF", "#99EEEAF5", "#EAE7F3FF",
                "#664E4862");
            SetShellTextForeground("#FF17171F");
            ApplyChatMessageTheme(isDark);
        }
    }

    private void ApplyChatMessageTheme(bool isDark)
    {
        var bubbleBrush = new SolidColorBrush(Color.Parse(isDark ? "#EE15151E" : "#FFF0ECF7"));
        var bubbleBorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#223C3C48" : "#FFD9D1E8"));
        var messageBrush = new SolidColorBrush(Color.Parse(isDark ? "#FFF0EEF8" : "#FF474255"));
        var senderBrush = new SolidColorBrush(Color.Parse(isDark ? "#FFA783FF" : "#FF7A68A4"));

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (Math.Abs(border.MaxWidth - 380) > 0.1 || border.Child is not TextBlock message)
                continue;

            border.Background = bubbleBrush;
            border.BorderBrush = bubbleBorderBrush;
            border.BorderThickness = new Thickness(1);
            message.Foreground = messageBrush;

            if (border.GetVisualParent() is StackPanel panel)
            {
                foreach (var sender in panel.Children.OfType<TextBlock>())
                    sender.Foreground = senderBrush;
            }
        }
    }

    private void SetShellTextForeground(string color)
    {
        if (this.FindControl<Border>("ShellCard") is not Border shell) return;

        var brush = new SolidColorBrush(Color.Parse(color));
        foreach (var text in shell.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.GetVisualAncestors().OfType<Border>().Any(IsDarkMessageBubble))
                continue;
            text.Foreground = brush;
        }
    }

    private static bool IsDarkMessageBubble(Border border)
        => border.Child is TextBlock && Math.Abs(border.MaxWidth - 380) <= 0.1;

    private void FillDotFields()
    {
        var line = string.Join("  ", Enumerable.Repeat(".", 74));
        var field = string.Join(Environment.NewLine, Enumerable.Repeat(line, 20));
        if (this.FindControl<TextBlock>("ClockDots") is { } clockDots)
            clockDots.Text = field;
        if (this.FindControl<TextBlock>("RoomDots") is { } roomDots)
            roomDots.Text = field;
    }

    private void SetThemeColors(
        string root,
        string title,
        string shell,
        string header,
        string clock,
        string deck,
        string queue,
        string room,
        string live,
        string footer,
        string border)
    {
        SetBackground("RootGrid", root);
        SetBackground("TitleBar", title);
        SetBackground("ShellCard", shell);
        SetBorderBrush("ShellCard", border);
        SetBackground("BrandHeader", header);
        SetBackground("ClockStage", clock);
        SetBackground("PlayerDeck", deck);
        SetBackground("QueueStrip", queue);
        SetBackground("RadioRoom", room);
        SetBackground("LiveStrip", live);
        SetBackground("InputDeck", deck);
        SetBackground("FooterBar", footer);
    }

    private void SetBackground(string name, string color)
    {
        var brush = new SolidColorBrush(Color.Parse(color));
        if (this.FindControl<Control>(name) is Border border)
            border.Background = brush;
        else if (this.FindControl<Control>(name) is Panel panel)
            panel.Background = brush;
    }

    private void SetBorderBrush(string name, string color)
    {
        if (this.FindControl<Border>(name) is Border border)
            border.BorderBrush = new SolidColorBrush(Color.Parse(color));
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

    private void OnSearchTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        // Triggers WhenAnyValue in the debounced subscription in DataContextChanged
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("音频文件")
                {
                    Patterns = ["*.mp3", "*.flac", "*.wav", "*.ogg", "*.m4a", "*.wma", "*.aac"]
                }
            ]
        });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();

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
