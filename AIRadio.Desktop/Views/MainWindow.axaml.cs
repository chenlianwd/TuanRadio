using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Serilog;

namespace AIRadio.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.ChatVM.Live2DCommand += OnLive2DCommand;
                vm.Live2DCommand += OnLive2DCommand;
            }
        };
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Handled by PlayerView slider bindings
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
}