using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Styling;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 应用外观主题模式：
/// - <see cref="System"/>：跟随操作系统深浅色偏好（Avalonia ThemeVariant.Default）；
/// - <see cref="Dark"/>：深色主题（ThemeVariant.Dark）；
/// - <see cref="Light"/>：浅色主题（ThemeVariant.Light）。
/// </summary>
public static class ThemeModes
{
    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    public static readonly IReadOnlyList<string> All = new[] { System, Dark, Light };

    /// <summary>规范化主题模式字符串；空值或非法值一律回退为跟随系统（system）。</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return System;

        return mode.Trim().ToLowerInvariant() switch
        {
            Dark => Dark,
            Light => Light,
            _ => System,
        };
    }

    /// <summary>转换为 Avalonia 的 ThemeVariant。</summary>
    public static ThemeVariant ToThemeVariant(string? mode) =>
        Normalize(mode) switch
        {
            Dark => ThemeVariant.Dark,
            Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };

    /// <summary>
    /// 解析当前实际生效的是否为深色。
    /// 强制深色返回 true；强制浅色返回 false；跟随系统时以 Application.ActualThemeVariant 为准。
    /// </summary>
    public static bool ResolveIsDarkMode(Application? app, string? mode)
    {
        var normalized = Normalize(mode);
        if (normalized == Dark) return true;
        if (normalized == Light) return false;

        if (app != null)
        {
            return app.ActualThemeVariant != ThemeVariant.Light;
        }

        return true;
    }
}
