using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 配色主题（accent palette）运行时切换。薄荷绿是基座（Themes/Colors.axaml 本体），
/// 其余主题以叠加字典遮蔽：常驻 ResourceDictionary 挂在 app.Resources.MergedDictionaries
/// 尾部（Avalonia 合并字典后置优先），变体字典原地清空重填触发 DynamicResource 全局刷新
/// （与 AppLanguageUi 常驻字典重填同一条通知链，机制前提见 AccentPaletteMechanismTests）。
/// 键名沿用薄荷绿时期的字面值命名（如 C_FF56F5C4），键只是 ID、值按主题重映射。
/// </summary>
public static class AccentPalettes
{
    public const string MintId = "mint";
    public const string AmberId = "amber";
    public const string SynthwaveId = "synthwave";
    public const string IceId = "ice";

    /// <summary>全部主题 Id（含基座薄荷绿）；设置页选项表顺序即此表顺序。</summary>
    public static readonly IReadOnlyList<string> AllIds = new[] { MintId, AmberId, SynthwaveId, IceId };

    private static readonly Dictionary<string, ResourceDictionary> PaletteCache = new();
    private static ResourceDictionary? _overlay;

    /// <summary>未知/空值一律回落薄荷绿（设置文件手改坏值、旧版本升级场景）。</summary>
    public static string Normalize(string? id)
        => id is AmberId or SynthwaveId or IceId ? id : MintId;

    /// <summary>
    /// 切换主题：薄荷绿=清空叠加层回落基座，其余主题=加载 Themes/Palettes/{Name}.axaml
    /// 逐变体重填；随后同步 FluentTheme 系统控件 Accent。幂等，可重复调用；
    /// app 为 null（headless 无宿主）时静默跳过。
    /// </summary>
    public static void Apply(Application? app, string? paletteId)
    {
        if (app is null)
            return;
        var id = Normalize(paletteId);
        // 先加载后清空：加载失败时叠加层与 Fluent Accent 保持原主题完整（原子切换），
        // 不留"token 已回落薄荷绿、系统控件残留旧强调色"的半切换窗口
        ResourceDictionary? palette = null;
        if (id != MintId)
            palette = LoadPalette(id);
        var overlay = EnsureOverlay(app);
        var dark = GetVariantDictionary(overlay, ThemeVariant.Dark);
        var light = GetVariantDictionary(overlay, ThemeVariant.Light);
        dark.Clear();
        light.Clear();
        if (palette != null)
        {
            CopyVariant(palette, ThemeVariant.Dark, dark);
            CopyVariant(palette, ThemeVariant.Light, light);
        }
        SyncFluentAccents(app);
    }

    /// <summary>加载并缓存调色板定义（只读源，Apply 只从中复制条目）。</summary>
    internal static ResourceDictionary LoadPalette(string id)
    {
        if (PaletteCache.TryGetValue(id, out var cached))
            return cached;
        var name = id switch
        {
            AmberId => "Amber",
            SynthwaveId => "Synthwave",
            IceId => "Ice",
            _ => throw new InvalidOperationException($"No palette file for id '{id}'"),
        };
        var palette = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://AIRadio/Themes/Palettes/{name}.axaml"));
        PaletteCache[id] = palette;
        return palette;
    }

    /// <summary>ThemeDictionaries 的值类型是 IThemeVariantProvider，实际存的都是 ResourceDictionary。</summary>
    private static ResourceDictionary GetVariantDictionary(ResourceDictionary host, ThemeVariant variant)
        => (ResourceDictionary)host.ThemeDictionaries[variant];

    private static void CopyVariant(ResourceDictionary palette, ThemeVariant variant, ResourceDictionary target)
    {
        if (!palette.ThemeDictionaries.TryGetValue(variant, out var source) || source is not ResourceDictionary dict)
            return;
        foreach (var pair in dict)
            target[pair.Key] = pair.Value;
    }

    private static ResourceDictionary EnsureOverlay(Application app)
    {
        // headless 测试每个用例新建 Application：缓存的叠加层不属于当前 app 时重建，
        // 避免把上一个会话的字典挂进新宿主
        if (_overlay is null || !app.Resources.MergedDictionaries.Contains(_overlay))
        {
            var overlay = new ResourceDictionary();
            overlay.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary();
            overlay.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary();
            app.Resources.MergedDictionaries.Add(overlay);
            _overlay = overlay;
        }
        return _overlay;
    }

    /// <summary>FluentTheme.Palettes 里的系统控件强调色（Slider 等）跟随各变体的实心强调键
    /// C_FF56F5C4——薄荷绿 Light 档该键为 #178A65，与 App.axaml 初始值逐字一致（AccentGreen
    /// 在 Light 是更深一档的文本绿 #0F6B57，取它会与初始值漂移）。切回薄荷绿即按基座还原。</summary>
    private static void SyncFluentAccents(Application app)
    {
        foreach (var style in app.Styles)
        {
            if (style is not FluentTheme fluent)
                continue;
            SetFluentAccent(fluent, app, ThemeVariant.Dark);
            SetFluentAccent(fluent, app, ThemeVariant.Light);
        }
    }

    private static void SetFluentAccent(FluentTheme fluent, Application app, ThemeVariant variant)
    {
        if (!app.TryGetResource("C_FF56F5C4", variant, out var value) || value is not Color accent)
            return;
        if (fluent.Palettes.TryGetValue(variant, out var palette))
            palette.Accent = accent;
    }
}
