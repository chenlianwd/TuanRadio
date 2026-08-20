using System;
using System.Collections.Generic;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace AIRadio.Desktop.Converters;

/// <summary>
/// RadioState → 状态色刷（spec §5.2.4）。优先取 Themes/Colors.axaml 的语义 token
/// （StatePlayingColor 等），资源不可用时（设计器/单测环境）退回内置表。缓存静态实例避免 GC（spec H18）。
/// </summary>
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

        if (TryFindStateBrush(state, isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light, out var brush))
            return brush;

        return (isDarkMode ? DarkBrushes : LightBrushes)[state];
    }

    private static bool TryFindStateBrush(RadioState state, ThemeVariant theme, out IBrush? brush)
    {
        brush = null;
        if (Application.Current?.TryGetResource($"State{state}Color", theme, out var resource) != true)
            return false;

        brush = resource switch
        {
            Color color => new SolidColorBrush(color),
            IBrush b => b,
            _ => null
        };
        return brush != null;
    }
}
