using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI;
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
