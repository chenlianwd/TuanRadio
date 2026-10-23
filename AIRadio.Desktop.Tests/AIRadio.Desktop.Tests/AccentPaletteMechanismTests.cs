using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 调色板机制的 Avalonia 11.3.9 行为实证（写于实现之前，三项前提逐一锚定）：
/// 1) 后置合并字典的 ThemeDictionaries 按变体遮蔽先置字典——"叠加层"优先级前提；
/// 2) 叠加层变体字典为空时回落先置字典——"薄荷绿=清空叠加层"实现前提；
/// 3) 常驻叠加层变体字典原地清空重填后，Application 级资源解析读到新值——
///    运行时切换传播前提（DynamicResource 消费者的刷新与 AppLanguageUi 常驻字典
///    原地重填同一条通知链，生产环境已验证，这里锚定资源侧事实）。
/// </summary>
public class AccentPaletteMechanismTests
{
    private static ResourceDictionary Themed(ThemeVariant variant, params (string Key, Color Value)[] entries)
    {
        var dict = new ResourceDictionary();
        var variantDict = new ResourceDictionary();
        foreach (var (key, value) in entries)
            variantDict[key] = value;
        dict.ThemeDictionaries[variant] = variantDict;
        return dict;
    }

    [Fact]
    public void LaterMergedThemeDictionary_ShadowsEarlier_PerVariant()
    {
        var host = new ResourceDictionary();
        host.MergedDictionaries.Add(Themed(ThemeVariant.Dark, ("K", Colors.Blue)));
        host.MergedDictionaries.Add(Themed(ThemeVariant.Dark, ("K", Colors.Green)));

        Assert.True(host.TryGetResource("K", ThemeVariant.Dark, out var dark));
        Assert.Equal(Colors.Green, Assert.IsType<Color>(dark));
    }

    [Fact]
    public void LaterMergedThemeDictionary_ShadowsOnlyItsOwnVariant()
    {
        var host = new ResourceDictionary();
        host.MergedDictionaries.Add(Themed(ThemeVariant.Dark, ("K", Colors.Blue))
            .Also(later => later.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary { ["K"] = Colors.White }));
        host.MergedDictionaries.Add(Themed(ThemeVariant.Dark, ("K", Colors.Green)));

        Assert.True(host.TryGetResource("K", ThemeVariant.Light, out var light));
        Assert.Equal(Colors.White, Assert.IsType<Color>(light));
        Assert.True(host.TryGetResource("K", ThemeVariant.Dark, out var dark));
        Assert.Equal(Colors.Green, Assert.IsType<Color>(dark));
    }

    [Fact]
    public void EmptyLaterThemeDictionary_FallsThroughToEarlier()
    {
        var host = new ResourceDictionary();
        host.MergedDictionaries.Add(Themed(ThemeVariant.Dark, ("K", Colors.Blue)));
        host.MergedDictionaries.Add(new ResourceDictionary());

        Assert.True(host.TryGetResource("K", ThemeVariant.Dark, out var dark));
        Assert.Equal(Colors.Blue, Assert.IsType<Color>(dark));
    }

    [AvaloniaFact]
    public void ResidentOverlayRefill_UpdatesAppResourceLookup()
    {
        var app = Application.Current!;
        var overlay = new ResourceDictionary();
        var darkDict = new ResourceDictionary { ["AccentPaletteMechanismK"] = Colors.Blue };
        overlay.ThemeDictionaries[ThemeVariant.Dark] = darkDict;
        app.Resources.MergedDictionaries.Add(overlay);
        try
        {
            Assert.True(app.TryGetResource("AccentPaletteMechanismK", ThemeVariant.Dark, out var first));
            Assert.Equal(Colors.Blue, Assert.IsType<Color>(first));

            darkDict.Clear();
            darkDict["AccentPaletteMechanismK"] = Colors.Green;

            Assert.True(app.TryGetResource("AccentPaletteMechanismK", ThemeVariant.Dark, out var second));
            Assert.Equal(Colors.Green, Assert.IsType<Color>(second));
        }
        finally
        {
            app.Resources.MergedDictionaries.Remove(overlay);
        }
    }
}

file static class DictionaryTestExtensions
{
    /// <summary>对刚构造的字典做附加初始化后返回自身（测试内联用）。</summary>
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
