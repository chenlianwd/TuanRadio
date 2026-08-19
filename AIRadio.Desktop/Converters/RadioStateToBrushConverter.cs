using System;
using System.Collections.Generic;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AIRadio.Desktop.Converters;

/// <summary>RadioState → 状态色刷（spec §5.2.4）。缓存静态实例避免 GC（spec H18）。</summary>
public class RadioStateToBrushConverter : IMultiValueConverter
{
    public static readonly RadioStateToBrushConverter Instance = new();

    private static readonly IReadOnlyDictionary<RadioState, SolidColorBrush> DarkBrushes =
        new Dictionary<RadioState, SolidColorBrush>
        {
            [RadioState.Idle] = new(Color.Parse("#FFB5B5C8")),
            [RadioState.Curating] = new(Color.Parse("#FFA783FF")),
            [RadioState.Searching] = new(Color.Parse("#FFFFC86B")),
            [RadioState.Speaking] = new(Color.Parse("#FFB9FFE8")),
            [RadioState.Playing] = new(Color.Parse("#FF56F5C4")),
            [RadioState.Error] = new(Color.Parse("#FFFF4444"))
        };

    private static readonly IReadOnlyDictionary<RadioState, SolidColorBrush> LightBrushes =
        new Dictionary<RadioState, SolidColorBrush>
        {
            [RadioState.Idle] = new(Color.Parse("#FF5F5968")),
            [RadioState.Curating] = new(Color.Parse("#FF7546AB")),
            [RadioState.Searching] = new(Color.Parse("#FF936000")),
            [RadioState.Speaking] = new(Color.Parse("#FF23654F")),
            [RadioState.Playing] = new(Color.Parse("#FF0E7B58")),
            [RadioState.Error] = new(Color.Parse("#FFC62828"))
        };

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = values.Count > 0 && values[0] is RadioState radioState ? radioState : RadioState.Idle;
        var isDarkMode = values.Count <= 1 || values[1] is not bool isDark || isDark;
        return (isDarkMode ? DarkBrushes : LightBrushes)[state];
    }
}
