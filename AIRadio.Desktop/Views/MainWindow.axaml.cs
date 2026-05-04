using System;
using System.Collections.Specialized;
using System.Linq;
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

public partial class MainWindow : Window
{
    private System.Timers.Timer? _clockTimer;
    private Button? _darkButton;
    private Button? _lightButton;
    private ViewModels.MainWindowViewModel? _activeVm;

    public MainWindow()
    {
        InitializeComponent();
        StartClock();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                if (_activeVm != null)
                    _activeVm.ChatVM.Messages.CollectionChanged -= OnChatMessagesChanged;

                _activeVm = vm;
                vm.ChatVM.Live2DCommand += OnLive2DCommand;
                vm.Live2DCommand += OnLive2DCommand;
                vm.WhenAnyValue(x => x.IsDarkMode).Subscribe(isDark => UpdateThemeButtons(isDark));
                vm.ChatVM.Messages.CollectionChanged += OnChatMessagesChanged;
                UpdateThemeButtons(vm.IsDarkMode);
            }
        };
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<ScrollViewer>("ChatScrollViewer")?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdateThemeButtons(bool isDark)
    {
        if (_darkButton == null) _darkButton = this.FindControl<Button>("DarkButton");
        if (_lightButton == null) _lightButton = this.FindControl<Button>("LightButton");

        if (isDark)
        {
            if (_darkButton != null) { _darkButton.Background = new SolidColorBrush(Color.Parse("#FF171722")); _darkButton.Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")); }
            if (_lightButton != null) { _lightButton.Background = new SolidColorBrush(Colors.Transparent); _lightButton.Foreground = new SolidColorBrush(Color.Parse("#FF666666")); }
            Background = new SolidColorBrush(Color.Parse("#FF08080B"));
            SetThemeColors(
                "#FF030305", "#FF050507", "#F0131320", "#CC1B1B2A", "#F008080D",
                "#E91B1B2A", "#F007070A", "#E9161623", "#66111113", "#F0222234",
                "#33494B66");
            SetShellTextForeground("#FFEDEDF5");
        }
        else
        {
            if (_darkButton != null) { _darkButton.Background = new SolidColorBrush(Colors.Transparent); _darkButton.Foreground = new SolidColorBrush(Color.Parse("#FF666666")); }
            if (_lightButton != null) { _lightButton.Background = new SolidColorBrush(Color.Parse("#FFFFFFFF")); _lightButton.Foreground = new SolidColorBrush(Color.Parse("#FF111118")); }
            Background = new SolidColorBrush(Color.Parse("#FFF5F1FF"));
            SetThemeColors(
                "#FFF5F1FF", "#FFE9E2F7", "#F8FFFFFF", "#EDEBE5FF", "#F7F8F6FF",
                "#ECEEE9FF", "#F4F3F8FF", "#F8FFFFFF", "#99EEEAF5", "#EAE7F3FF",
                "#664E4862");
            SetShellTextForeground("#FF17171F");
        }
    }

    private void SetShellTextForeground(string color)
    {
        if (this.FindControl<Border>("ShellCard") is not Border shell) return;

        var brush = new SolidColorBrush(Color.Parse(color));
        foreach (var text in shell.GetVisualDescendants().OfType<TextBlock>())
        {
            text.Foreground = brush;
        }
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
        catch { }
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

    private async void OnLive2DCommand(string expression, string motion)
    {
        if (this.Find<Border>("AvatarBorder") is Border border)
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

            if (this.Find<Border>("AvatarBorder") is Border border &&
                this.Find<TextBlock>("AvatarLetter") is TextBlock letter)
            {
                _ = Animations.PlayCharacterSwitchAsync(border, letter, character.DisplayName,
                    () => vm.SelectCharacterCommand.Execute(character));
            }
            else
            {
                vm.SelectCharacterCommand.Execute(character);
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

    public void Dispose()
    {
        _clockTimer?.Stop();
        _clockTimer?.Dispose();
    }
}
