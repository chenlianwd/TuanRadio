using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AIRadio.Desktop.Converters;

/// <summary>布尔反相器。合并自 MainWindow.InverseBoolConverter 与 PlaylistView.InvertBoolValueConverter。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
