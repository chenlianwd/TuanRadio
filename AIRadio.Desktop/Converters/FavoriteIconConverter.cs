using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Data;

namespace AIRadio.Desktop.Converters;

// Favorite icon: ♥ if true (IsFavorite), ♡ if false
public class FavoriteIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool isFav && isFav ? "♥" : "♡";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
