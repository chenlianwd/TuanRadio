using System.Collections.Generic;
using AIRadio.Desktop.Converters;
using AIRadio.Desktop.Models;
using Avalonia.Media;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 主播专属识别色映射（黑胶唱片头像标签 + 选主播弹层首字母共用）。
/// Light 加深变体是 WCAG 门禁的一部分：六色须同时满足
/// 弹层近白底文本 ≥4.5 / Light 盘面 #E2DAEC 图形 ≥3.0 / 白墨字母 ≥4.5。
/// </summary>
public class CharacterAccentConverterTests
{
    // 四套配色主题下选主播弹层底色的 Light 档（基座+三叠加层，全近白；按最不利联检，去 alpha 的 RRGGBB）
    private static readonly string[] LightPopupBackgrounds =
        ["FFFCFF", "FFFBF1", "FDFAFF", "FAFCFE"];

    private const string LightDiscBackground = "FFE2DAEC";
    private const string LightInk = "FFFFFF";

    [Theory]
    [InlineData("haru", 0xFFE8A33D, 0xFF9C6400)]   // Lumen 晨光金
    [InlineData("hiyori", 0xFF9D7BE8, 0xFF7B4FCB)] // Aster 夜幕紫
    [InlineData("mao", 0xFF7F9BB3, 0xFF46647E)]    // Noir 蓝灰钢
    [InlineData("mark", 0xFF43B8C9, 0xFF0E7D8F)]   // Atlas 青碧
    [InlineData("natori", 0xFFE87B9E, 0xFFC2477A)] // Sonnet 玫瑰
    [InlineData("ren", 0xFFE0644F, 0xFFC2372B)]    // Vega 炽红
    public void KnownHosts_MapToOwnAccent_BothVariants(string id, uint dark, uint light)
    {
        var profile = new CharacterProfile { Id = id };
        Assert.Equal(Color.FromUInt32(dark), CharacterAccentConverter.GetAccent(profile, isLight: false));
        Assert.Equal(Color.FromUInt32(light), CharacterAccentConverter.GetAccent(profile, isLight: true));
    }

    [Fact]
    public void UnknownOrNull_FallsBackToNeutral_BothVariants()
    {
        var stranger = new CharacterProfile { Id = "someone-new" };
        Assert.Equal(Color.FromUInt32(0xFFB5B5C8), CharacterAccentConverter.GetAccent(stranger, isLight: false));
        Assert.Equal(Color.FromUInt32(0xFF6A6478), CharacterAccentConverter.GetAccent(stranger, isLight: true));
        Assert.Equal(Color.FromUInt32(0xFFB5B5C8), CharacterAccentConverter.GetAccent(null, isLight: false));
        Assert.Equal(Color.FromUInt32(0xFF6A6478), CharacterAccentConverter.GetAccent(null, isLight: true));
    }

    [Fact]
    public void AllPresetHosts_HaveOwnAccent_NoFallback()
    {
        // 预设主播全员必须有专属色——新增主播漏配色只会静默回落灰，此锚大声失败
        Assert.All(CharacterProfile.Presets, p =>
        {
            Assert.NotEqual(Color.FromUInt32(0xFFB5B5C8), CharacterAccentConverter.GetAccent(p, isLight: false));
            Assert.NotEqual(Color.FromUInt32(0xFF6A6478), CharacterAccentConverter.GetAccent(p, isLight: true));
        });
    }

    /// <summary>Light 加深变体的对比度门禁（2026-11-03 盲审中危转化）：三约束全过才准入。</summary>
    [Fact]
    public void LightAccents_PassContrastGate_PopupText_DiscGraphic_WhiteInk()
    {
        foreach (var profile in CharacterProfile.Presets)
        {
            var accent = CharacterAccentConverter.GetAccent(profile, isLight: true);
            var hex = $"{accent.R:X2}{accent.G:X2}{accent.B:X2}";

            foreach (var bg in LightPopupBackgrounds)
                Assert.True(Ratio(hex, bg) >= 4.5,
                    $"{profile.Id} Light #{hex} 对弹层底 #{bg} 文本对比 {Ratio(hex, bg):F2} < 4.5");
            Assert.True(Ratio(hex, LightDiscBackground) >= 3.0,
                $"{profile.Id} Light #{hex} 对盘面图形对比 {Ratio(hex, LightDiscBackground):F2} < 3.0");
            Assert.True(Ratio(LightInk, hex) >= 4.5,
                $"白墨对 {profile.Id} Light #{hex} 对比 {Ratio(LightInk, hex):F2} < 4.5");
        }
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var v = int.Parse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double Ratio(string a, string b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
