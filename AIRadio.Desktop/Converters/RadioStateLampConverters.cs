using System;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;

namespace AIRadio.Desktop.Converters;

/// <summary>RadioState → 状态灯是否点亮：Idle 熄灭（乳白熄灯态），其余状态点亮（StatusBar ON AIR 灯箱）。</summary>
public class RadioStateLampLitConverter : IValueConverter
{
    public static readonly RadioStateLampLitConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RadioState s && s != RadioState.Idle;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>RadioState → 状态灯是否呼吸：直播态（播出/主持说话）辉光呼吸，策划/搜索/错误恒亮不闪。</summary>
public class RadioStateLampBreathingConverter : IValueConverter
{
    public static readonly RadioStateLampBreathingConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RadioState.Playing or RadioState.Speaking;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
