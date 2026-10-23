using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>配色主题：Apply 语义 / 键集奇偶 / Fluent Accent 同步 /
/// WCAG 对比度门禁（文本 4.5:1、图形 3:1，与浅色主题历史审计同口径）/
/// 真实视图 DynamicResource 传播 / 设置持久化。</summary>
public class AccentPaletteTests
{
    private static ResourceDictionary LoadBaseColors()
        => (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://AIRadio/Themes/Colors.axaml"));

    private static ResourceDictionary GetVariant(ResourceDictionary dict, ThemeVariant variant)
        => (ResourceDictionary)dict.ThemeDictionaries[variant];

    private static void AttachBaseColors(Application app)
        => app.Resources.MergedDictionaries.Add(LoadBaseColors());

    [Theory]
    [InlineData(null, AccentPalettes.MintId)]
    [InlineData("", AccentPalettes.MintId)]
    [InlineData("bogus", AccentPalettes.MintId)]
    [InlineData("MINT", AccentPalettes.MintId)]
    [InlineData("amber", AccentPalettes.AmberId)]
    [InlineData("synthwave", AccentPalettes.SynthwaveId)]
    [InlineData("ice", AccentPalettes.IceId)]
    public void Normalize_FallsBackToMint_ForUnknownValues(string? input, string expected)
        => Assert.Equal(expected, AccentPalettes.Normalize(input));

    [AvaloniaFact]
    public void Apply_NonMintPalette_OverridesBasePerVariant_AndFallsThroughForUntouchedKeys()
    {
        var app = Application.Current!;
        AttachBaseColors(app);

        AccentPalettes.Apply(app, AccentPalettes.AmberId);

        Assert.True(app.TryGetResource("AccentGreen", ThemeVariant.Dark, out var darkAccent));
        Assert.Equal(Color.Parse("#FFFFC978"), darkAccent);
        Assert.True(app.TryGetResource("AccentGreen", ThemeVariant.Light, out var lightAccent));
        Assert.Equal(Color.Parse("#FF8A5A00"), lightAccent);
        // 琥珀不动策划态紫（子集语义：未声明的键回落基座）
        Assert.True(app.TryGetResource("StateCuratingColor", ThemeVariant.Dark, out var curating));
        Assert.Equal(Color.Parse("#FFA783FF"), curating);
        // 底色 tint 也被遮蔽
        Assert.True(app.TryGetResource("WindowBg", ThemeVariant.Dark, out var windowBg));
        Assert.Equal(Color.Parse("#FF0D0A07"), windowBg);
    }

    [AvaloniaFact]
    public void Apply_Synthwave_RemapsCuratingState_ToAvoidAccentCollision()
    {
        var app = Application.Current!;
        AttachBaseColors(app);

        AccentPalettes.Apply(app, AccentPalettes.SynthwaveId);

        Assert.True(app.TryGetResource("StateCuratingColor", ThemeVariant.Dark, out var curating));
        Assert.Equal(Color.Parse("#FFFF8CC8"), curating);
        Assert.True(app.TryGetResource("StateCuratingColor", ThemeVariant.Light, out var curatingLight));
        Assert.Equal(Color.Parse("#FFB03A6F"), curatingLight);
    }

    [AvaloniaFact]
    public void Apply_MintOrUnknown_ClearsOverlayAndRestoresBase()
    {
        var app = Application.Current!;
        AttachBaseColors(app);

        AccentPalettes.Apply(app, AccentPalettes.SynthwaveId);
        AccentPalettes.Apply(app, AccentPalettes.MintId);
        Assert.True(app.TryGetResource("AccentGreen", ThemeVariant.Dark, out var restored));
        Assert.Equal(Color.Parse("#FF56F5C4"), restored);

        AccentPalettes.Apply(app, AccentPalettes.IceId);
        AccentPalettes.Apply(app, "bogus");
        Assert.True(app.TryGetResource("AccentGreen", ThemeVariant.Dark, out var fallback));
        Assert.Equal(Color.Parse("#FF56F5C4"), fallback);
    }

    [AvaloniaFact]
    public void PaletteFiles_DefineBothVariants_WithIdenticalKeys_AllWithinBaseKeySet()
    {
        var baseKeys = VariantKeys(LoadBaseColors(), ThemeVariant.Dark);

        foreach (var id in new[] { AccentPalettes.AmberId, AccentPalettes.SynthwaveId, AccentPalettes.IceId })
        {
            var palette = AccentPalettes.LoadPalette(id);
            Assert.True(palette.ThemeDictionaries.ContainsKey(ThemeVariant.Dark), $"{id} 缺 Dark 变体");
            Assert.True(palette.ThemeDictionaries.ContainsKey(ThemeVariant.Light), $"{id} 缺 Light 变体");

            var darkKeys = VariantKeys(palette, ThemeVariant.Dark);
            var lightKeys = VariantKeys(palette, ThemeVariant.Light);
            Assert.True(darkKeys.SetEquals(lightKeys), $"{id} 的 Dark/Light 键集不一致：{DescribeDiff(darkKeys, lightKeys)}");
            Assert.True(darkKeys.IsSubsetOf(baseKeys), $"{id} 声明了基座不存在的键：{string.Join(",", darkKeys.Except(baseKeys))}");
        }
    }

    [AvaloniaFact]
    public void Apply_SyncsFluentPaletteAccents_PerVariant()
    {
        var app = Application.Current!;
        AttachBaseColors(app);
        var fluent = new FluentTheme();
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#FF56F5C4") };
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#FF178A65") };
        app.Styles.Add(fluent);
        try
        {
            AccentPalettes.Apply(app, AccentPalettes.IceId);
            Assert.Equal(Color.Parse("#FF6BD0FF"), fluent.Palettes[ThemeVariant.Dark].Accent);
            Assert.Equal(Color.Parse("#FF0E5A8A"), fluent.Palettes[ThemeVariant.Light].Accent);

            // 切回薄荷绿按基座资源还原（等价 App.axaml 初始值）
            AccentPalettes.Apply(app, AccentPalettes.MintId);
            Assert.Equal(Color.Parse("#FF56F5C4"), fluent.Palettes[ThemeVariant.Dark].Accent);
            Assert.Equal(Color.Parse("#FF178A65"), fluent.Palettes[ThemeVariant.Light].Accent);
        }
        finally
        {
            app.Styles.Remove(fluent);
        }
    }

    [AvaloniaFact]
    public void Apply_UpdatesDynamicResourceConsumer_InRealView()
    {
        var app = Application.Current!;
        AttachBaseColors(app);
        var titleBar = new TitleBar();
        var window = new Window
        {
            Content = titleBar,
            Width = 400,
            Height = 40,
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // TitleBar 的 10x10 圆点是全库最早的 AccentGreen 消费者之一
        var dot = titleBar.GetVisualDescendants().OfType<Border>().First(b => b.Width == 10 && b.Height == 10);
        Assert.Equal(Color.Parse("#FF56F5C4"), BrushColor(dot.Background));

        AccentPalettes.Apply(app, AccentPalettes.AmberId);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#FFFFC978"), BrushColor(dot.Background));
    }

    [AvaloniaFact]
    public void Apply_UpdatesSpectrumGradientStops_InRealView()
    {
        var app = Application.Current!;
        AttachBaseColors(app);
        // 频谱主渐变的 GradientStop 是"非可视对象上的 DynamicResource"（LinearGradientBrush
        // 经 StaticResource 注入 Renderer），锚定资源重填通知能到达这条边缘路径——
        // 若不刷新，切主题后频谱渐变停留旧色（静默、仅实机可见）
        var spectrum = new SpectrumView();
        var window = new Window
        {
            Content = spectrum,
            Width = 200,
            Height = 140,
            RequestedThemeVariant = ThemeVariant.Dark,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var brush = Assert.IsType<LinearGradientBrush>(spectrum.Resources["SpectrumPrimary"]);
        Assert.Equal(Color.Parse("#FFB9FFE8"), brush.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#FF56F5C4"), brush.GradientStops[1].Color);

        AccentPalettes.Apply(app, AccentPalettes.AmberId);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#FFFFE0B0"), brush.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#FFFFC978"), brush.GradientStops[1].Color);
    }

    [AvaloniaFact]
    public void AllPalettes_PassContrastGate_OnAuditedPairs()
    {
        foreach (var paletteId in AccentPalettes.AllIds)
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                var table = EffectiveTable(paletteId, variant);
                var bgs = BuildBackgrounds(table);
                foreach (var (fgKey, bgKey, minRatio, label) in AuditedPairs)
                {
                    var ratio = ContrastRatio(table[fgKey], bgs[bgKey]);
                    Assert.True(ratio >= minRatio,
                        $"{paletteId}/{variant.Key} {label}（{fgKey} on {bgKey}）对比度 {ratio:F2} 低于 {minRatio}");
                }
            }
        }
    }

    [Fact]
    public async Task AccentPalette_RoundTripsThroughSettings_AndNormalizesOnLoad()
    {
        var mockLlm = new Mock<ILLMService>();
        var mockStorage = new Mock<ISecureStorage>();
        mockStorage.Setup(x => x.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            using (var vm = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsFile))
            {
                vm.SelectedAccentPalette = AccentPalettes.SynthwaveId;
                // 切换即跟随保存（订阅异步落盘，轮询等待写入完成）
                await WaitForAsync(() => File.Exists(settingsFile));
                Assert.Contains("\"accent_palette\": \"synthwave\"", await File.ReadAllTextAsync(settingsFile));
            }

            using (var restored = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsFile))
            {
                await restored.LoadAsync();
                Assert.Equal(AccentPalettes.SynthwaveId, restored.SelectedAccentPalette);
            }

            // 坏值回落薄荷绿
            await File.WriteAllTextAsync(settingsFile, "{ \"accent_palette\": \"bogus\" }");
            using (var normalized = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsFile))
            {
                await normalized.LoadAsync();
                Assert.Equal(AccentPalettes.MintId, normalized.SelectedAccentPalette);
            }
        }
        finally
        {
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    [Fact]
    public void AccentPaletteOptions_ListAllThemesWithLocalizedNames()
    {
        var mockLlm = new Mock<ILLMService>();
        var mockStorage = new Mock<ISecureStorage>();
        var settingsFile = Path.Combine(Path.GetTempPath(), $"airadio-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var vm = new SettingsViewModel(mockLlm.Object, mockStorage.Object, settingsFile);
            Assert.Equal(new[] { "mint", "amber", "synthwave", "ice" }, vm.AccentPaletteOptions.Select(o => o.Id));
            Assert.Equal("薄荷绿", vm.AccentPaletteOptions[0].DisplayName);

            AppLanguage.Apply("en");
            Assert.Equal("Ice Blue", vm.AccentPaletteOptions[3].DisplayName);
        }
        finally
        {
            AppLanguage.Apply("zh");
            File.Delete(settingsFile);
            File.Delete(settingsFile + ".bak");
        }
    }

    private static Color BrushColor(IBrush? brush)
        => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "等待条件超时");
    }

    private static HashSet<string> VariantKeys(ResourceDictionary dict, ThemeVariant variant)
        => GetVariant(dict, variant).Keys.Cast<string>().ToHashSet(StringComparer.Ordinal);

    private static string DescribeDiff(HashSet<string> dark, HashSet<string> light)
        => $"only-dark=[{string.Join(",", dark.Except(light))}] only-light=[{string.Join(",", light.Except(dark))}]";

    /// <summary>叠加层语义的有效值表：基座变体字典 + 调色板变体字典覆盖。</summary>
    private static Dictionary<string, Color> EffectiveTable(string paletteId, ThemeVariant variant)
    {
        var table = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var pair in GetVariant(LoadBaseColors(), variant))
            table[(string)pair.Key] = (Color)pair.Value!;
        if (paletteId != AccentPalettes.MintId)
        {
            foreach (var pair in GetVariant(AccentPalettes.LoadPalette(paletteId), variant))
                table[(string)pair.Key] = (Color)pair.Value!;
        }
        return table;
    }

    /// <summary>审计色对：(前景键, 背景键, 阈值, 说明)。背景键指向 BuildBackgrounds 里的合成底。</summary>
    private static readonly (string Fg, string Bg, double Min, string Label)[] AuditedPairs =
    {
        ("AccentGreen", "shell", 4.5, "强调色文本"),
        ("AccentGreen", "window", 3.0, "强调色图形"),
        ("StatePlayingColor", "footer", 4.5, "播放态文本"),
        ("StateSpeakingColor", "footer", 4.5, "说话态文本"),
        ("StateCuratingColor", "footer", 4.5, "策划态文本"),
        // 搜索态在琥珀主题刻意回落基座黄（撞色可容忍的前提），且是全库余量最薄的
        // 文本对（Light 档 4.57~4.76），footer tint 调亮即破线，必须看护；
        // C_FFA783FF 除频谱副画刷外还是聊天发送者名（10px 最小正文），本批值变化最大的键
        ("StateSearchingColor", "footer", 4.5, "搜索态文本"),
        ("C_FFA783FF", "chat", 4.5, "发送者名文本"),
        ("C_FFB9FFE8", "chat", 4.5, "VOICE 族文本"),
        ("C_FFE4D9FF", "bubble", 4.5, "录音提示文本"),
        ("C_FFEDEDF5", "navChip", 4.5, "导航选中文字"),
    };

    private static Dictionary<string, Color> BuildBackgrounds(Dictionary<string, Color> table)
    {
        // 半透明底色一律按最不利情况合成到窗口底之上
        Color Over(string key) => CompositeOver(table[key], table["WindowBg"]);
        var overlayPanel = Over("C_FC0E0E15");
        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["window"] = table["WindowBg"],
            ["shell"] = Over("SurfaceShell"),
            ["footer"] = Over("SurfaceFooter"),
            ["chat"] = Over("C_E9161623"),
            ["bubble"] = Over("C_EE050507"),
            ["navChip"] = CompositeOver(table["C_3356F5C4"], overlayPanel),
        };
    }

    private static Color CompositeOver(Color fg, Color bg)
    {
        var a = fg.A / 255.0;
        return Color.FromArgb(255,
            (byte)Math.Round(fg.R * a + bg.R * (1 - a)),
            (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
            (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return la > lb ? (la + 0.05) / (lb + 0.05) : (lb + 0.05) / (la + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        double Lin(double channel) =>
            channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R / 255.0) + 0.7152 * Lin(c.G / 255.0) + 0.0722 * Lin(c.B / 255.0);
    }
}
