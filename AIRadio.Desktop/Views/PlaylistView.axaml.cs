using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        Resources["TabBgConverter"] = new TabBackgroundConverter();
        Resources["TabBgActiveConverter"] = new TabBackgroundActiveConverter();
        Resources["TabFgConverter"] = new TabForegroundConverter();
        Resources["TabFgActiveConverter"] = new TabForegroundActiveConverter();
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

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is PlaylistViewModel vm)
        {
            vm.SearchCommand.Execute().Subscribe();
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

// Tab: 播放列表按钮背景 — active=transparent, inactive=dark
public class TabBackgroundConverter : IValueConverter
{
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#FF2A2A2A"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isSearchMode ? (isSearchMode ? InactiveBg : ActiveBg) : ActiveBg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Tab: 搜索按钮背景
public class TabBackgroundActiveConverter : IValueConverter
{
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#FF2A2A2A"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isSearchMode ? (isSearchMode ? ActiveBg : InactiveBg) : InactiveBg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Tab: 播放列表按钮文字 — active=white, inactive=gray
public class TabForegroundConverter : IValueConverter
{
    private static readonly IBrush ActiveFg = Brushes.White;
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.Parse("#FF808080"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isSearchMode ? (isSearchMode ? InactiveFg : ActiveFg) : ActiveFg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Tab: 搜索按钮文字
public class TabForegroundActiveConverter : IValueConverter
{
    private static readonly IBrush ActiveFg = Brushes.White;
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.Parse("#FF808080"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isSearchMode ? (isSearchMode ? ActiveFg : InactiveFg) : InactiveFg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
