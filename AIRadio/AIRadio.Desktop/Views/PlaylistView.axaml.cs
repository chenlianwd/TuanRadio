using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AIRadio.Desktop.ViewModels;
using System;
using System.Globalization;
using System.Linq;

namespace AIRadio.Desktop.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        Resources["InvertBoolConverter"] = new InvertBoolValueConverter();
        InitializeComponent();
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlaylistViewModel vm) return;

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

        if (files.Count > 0)
        {
            vm.AddFiles(files.Select(f => f.TryGetLocalPath()!).Where(p => p != null).ToArray());
        }
    }
}

public class InvertBoolValueConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
