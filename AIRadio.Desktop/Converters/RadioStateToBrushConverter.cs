using System;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AIRadio.Desktop.Converters;

/// <summary>RadioState → 状态色刷（spec §5.2.4）。缓存静态实例避免 GC（spec H18）。</summary>
public class RadioStateToBrushConverter : IValueConverter
{
    public static readonly RadioStateToBrushConverter Instance = new();

    private static readonly SolidColorBrush Idle = new(Color.Parse("#FFB5B5C8"));
    private static readonly SolidColorBrush Curating = new(Color.Parse("#FFA783FF"));
    private static readonly SolidColorBrush Searching = new(Color.Parse("#FFFFC86B"));
    private static readonly SolidColorBrush Speaking = new(Color.Parse("#FFB9FFE8"));
    private static readonly SolidColorBrush Playing = new(Color.Parse("#FF56F5C4"));
    private static readonly SolidColorBrush Error = new(Color.Parse("#FFFF4444"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RadioState s ? s switch
        {
            RadioState.Idle => Idle,
            RadioState.Curating => Curating,
            RadioState.Searching => Searching,
            RadioState.Speaking => Speaking,
            RadioState.Playing => Playing,
            RadioState.Error => Error,
            _ => Idle
        } : Idle;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
