using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AIRadio.Desktop.ViewModels;

namespace AIRadio.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnMusicProviderToggle(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is SettingsViewModel vm &&
                sender is ToggleSwitch { DataContext: MusicProviderOption option } toggle)
            {
                // Click 与 TwoWay 源更新的先后顺序不应决定持久化结果。
                option.Enabled = toggle.IsChecked == true;
                await vm.ApplyMusicProviderOptionsAsync();
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not save music source selection"); }
    }

    private async void OnMusicProviderUp(object? sender, RoutedEventArgs e)
        => await MoveMusicProviderAsync(sender, -1);

    private async void OnMusicProviderDown(object? sender, RoutedEventArgs e)
        => await MoveMusicProviderAsync(sender, 1);

    private async System.Threading.Tasks.Task MoveMusicProviderAsync(object? sender, int direction)
    {
        try
        {
            if (DataContext is SettingsViewModel vm &&
                sender is Control { DataContext: MusicProviderOption option })
                await vm.MoveMusicProviderAsync(option.Id, direction);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Could not save music source priority"); }
    }
}
