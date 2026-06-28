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
        // Note: InvertBoolValueConverter is functionally equivalent to MainWindow's InverseBoolConverter.
        // Consider consolidating into a shared Converters/ directory (H14).
        Resources["InvertBoolConverter"] = new InvertBoolValueConverter();
        Resources["TabBgPlaylistConverter"] = new TabBgConverter(0);
        Resources["TabFgPlaylistConverter"] = new TabFgConverter(0);
        Resources["TabBgFavoritesConverter"] = new TabBgConverter(1);
        Resources["TabFgFavoritesConverter"] = new TabFgConverter(1);
        Resources["TabBgSearchConverter"] = new TabBgConverter(2);
        Resources["TabFgSearchConverter"] = new TabFgConverter(2);
        Resources["TabVisibleConverter"] = new TabVisibleConverter();
        Resources["FavoriteIconConverter"] = new FavoriteIconConverter();
        InitializeComponent();
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlaylistViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var paths = await FilePickerHelper.PickAudioFilesAsync(topLevel);
        if (paths.Length > 0)
        {
            vm.AddFiles(paths);
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

// Tab button background: active when TabIndex matches tabIndex parameter
public class TabBgConverter : IValueConverter
{
    private readonly int _tabIndex;
    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#FF2A2A2A"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    public TabBgConverter(int tabIndex) => _tabIndex = tabIndex;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int tabIndex && tabIndex == _tabIndex ? ActiveBg : InactiveBg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Tab button foreground: active when TabIndex matches tabIndex parameter
public class TabFgConverter : IValueConverter
{
    private readonly int _tabIndex;
    private static readonly IBrush ActiveFg = Brushes.White;
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.Parse("#FF808080"));

    public TabFgConverter(int tabIndex) => _tabIndex = tabIndex;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int tabIndex && tabIndex == _tabIndex ? ActiveFg : InactiveFg;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Tab content visibility: visible when TabIndex equals parameter (0=列表, 1=收藏, 2=搜索)
public class TabVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int tabIndex && parameter is string paramStr && int.TryParse(paramStr, out int param))
            return tabIndex == param;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

// Favorite icon: ♥ if true (IsFavorite), ♡ if false
public class FavoriteIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isFav && isFav ? "♥" : "♡";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
