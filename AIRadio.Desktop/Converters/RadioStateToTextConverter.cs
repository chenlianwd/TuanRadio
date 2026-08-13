using System;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;

namespace AIRadio.Desktop.Converters;

/// <summary>RadioState → 状态显示文本（spec §5.2.4）。</summary>
public class RadioStateToTextConverter : IValueConverter
{
    public static readonly RadioStateToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RadioState s ? s switch
        {
            RadioState.Idle => "AIRADIO FM",
            RadioState.Curating => "CURATING",
            RadioState.Searching => "SEARCHING",
            RadioState.Speaking => "SPEAKING",
            RadioState.Playing => "ON AIR",
            RadioState.Error => "ERROR",
            _ => "AIRADIO FM"
        } : "AIRADIO FM";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
