using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Data.Converters;
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

    public MainWindow()
    {
        InitializeComponent();
        StartClock();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.ChatVM.Live2DCommand += OnLive2DCommand;
                vm.Live2DCommand += OnLive2DCommand;
                vm.WhenAnyValue(x => x.IsDarkMode).Subscribe(isDark => UpdateThemeButtons(isDark));
            }
        };
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
        }
        else
        {
            if (_darkButton != null) { _darkButton.Background = new SolidColorBrush(Colors.Transparent); _darkButton.Foreground = new SolidColorBrush(Color.Parse("#FF666666")); }
            if (_lightButton != null) { _lightButton.Background = new SolidColorBrush(Color.Parse("#FF171722")); _lightButton.Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")); }
            Background = new SolidColorBrush(Color.Parse("#FFF5F5F5"));
        }
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
