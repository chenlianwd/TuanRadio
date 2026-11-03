using System;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;

namespace AIRadio.Desktop.Converters;

/// <summary>RadioState → 状态显示文本（spec §5.2.4；Idle 灯箱熄灯态：字母常驻、背光熄灭）。</summary>
public class RadioStateToTextConverter : IValueConverter
{
    public static readonly RadioStateToTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RadioState s ? s switch
        {
            RadioState.Idle => "ON AIR",
            RadioState.Curating => "CURATING",
            RadioState.Searching => "SEARCHING",
            RadioState.Speaking => "SPEAKING",
            RadioState.Playing => "ON AIR",
            RadioState.Error => "ERROR",
            _ => "ON AIR"
        } : "ON AIR";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
