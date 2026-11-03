using System;
using System.Collections.Generic;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace AIRadio.Desktop.Converters;

/// <summary>
/// 主播 → 专属识别色（黑胶唱片头像的中心标签/选主播弹层首字母共用）。
/// 识别色是主播的品牌身份色（同色相、双档明度），不随四套配色主题叠加层重映射：
/// Dark 档用亮标签配深墨首字母，Light 档用加深标签配白墨首字母——六色 Light 变体
/// 均经程序化 WCAG 预检（弹层近白底文本 ≥4.5 / Light 盘面图形 ≥3.0 / 白墨字母 ≥4.5，
/// 最薄 4.53:1，色值以 CharacterAccentConverterTests 的对比度门禁为准）。
/// </summary>
public class CharacterAccentConverter : IValueConverter
{
    public static readonly CharacterAccentConverter Instance = new();

    private const string InkParameter = "ink";

    private static readonly IReadOnlyDictionary<string, Color> DarkAccents =
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["haru"] = Color.Parse("#FFE8A33D"),    // Lumen 晨光金
            ["hiyori"] = Color.Parse("#FF9D7BE8"),  // Aster 夜幕紫
            ["mao"] = Color.Parse("#FF7F9BB3"),     // Noir 蓝灰钢
            ["mark"] = Color.Parse("#FF43B8C9"),    // Atlas 青碧
            ["natori"] = Color.Parse("#FFE87B9E"),  // Sonnet 玫瑰
            ["ren"] = Color.Parse("#FFE0644F")      // Vega 炽红
        };

    // Light 加深变体：约束见类注释（node 脚本三约束联检产出，手抄色值勿凭眼调）
    private static readonly IReadOnlyDictionary<string, Color> LightAccents =
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["haru"] = Color.Parse("#FF9C6400"),
            ["hiyori"] = Color.Parse("#FF7B4FCB"),
            ["mao"] = Color.Parse("#FF46647E"),
            ["mark"] = Color.Parse("#FF0E7D8F"),
            ["natori"] = Color.Parse("#FFC2477A"),
            ["ren"] = Color.Parse("#FFC2372B")
        };

    private static readonly Color DarkFallback = Color.Parse("#FFB5B5C8");
    private static readonly Color LightFallback = Color.Parse("#FF6A6478");

    // 标签上的首字母墨色：Dark 深墨（亮标签上）/ Light 白墨（深标签上）
    private static readonly Color DarkInk = Color.Parse("#FF14141C");
    private static readonly Color LightInk = Color.Parse("#FFFFFFFF");

    // 缓存静态实例避免 GC（spec H18，同 RadioStateToBrushConverter）；调用频率低但零成本对齐先例
    private static readonly ImmutableSolidColorBrush DarkFallbackBrush = new(DarkFallback);
    private static readonly ImmutableSolidColorBrush LightFallbackBrush = new(LightFallback);
    private static readonly ImmutableSolidColorBrush DarkInkBrush = new(DarkInk);
    private static readonly ImmutableSolidColorBrush LightInkBrush = new(LightInk);
    private static readonly IReadOnlyDictionary<string, ImmutableSolidColorBrush> DarkAccentBrushes =
        BrushCache(DarkAccents);
    private static readonly IReadOnlyDictionary<string, ImmutableSolidColorBrush> LightAccentBrushes =
        BrushCache(LightAccents);

    private static Dictionary<string, ImmutableSolidColorBrush> BrushCache(IReadOnlyDictionary<string, Color> source)
    {
        var cache = new Dictionary<string, ImmutableSolidColorBrush>(StringComparer.Ordinal);
        foreach (var (id, color) in source)
            cache[id] = new ImmutableSolidColorBrush(color);
        return cache;
    }

    public static Color GetAccent(CharacterProfile? profile, bool isLight = false)
    {
        var table = isLight ? LightAccents : DarkAccents;
        return profile != null && table.TryGetValue(profile.Id, out var color) ? color
            : isLight ? LightFallback : DarkFallback;
    }

    public static Color GetInk(bool isLight = false) => isLight ? LightInk : DarkInk;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isLight = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        if (parameter as string == InkParameter)
            return isLight ? LightInkBrush : DarkInkBrush;
        if (value is not CharacterProfile profile)
            return isLight ? LightFallbackBrush : DarkFallbackBrush;
        var cache = isLight ? LightAccentBrushes : DarkAccentBrushes;
        return cache.TryGetValue(profile.Id, out var brush)
            ? brush
            : isLight ? LightFallbackBrush : DarkFallbackBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
