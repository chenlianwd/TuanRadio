using System;
using System.Linq;
using System.Reactive.Concurrency;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 主界面播放区进度条可见性锚定：PlayerDeck 三行内容（控制行/反馈行/进度行）自反馈行
// 加入后总高超出主网格固定行高（64,168,88,…），进度行整段被 ClipToBounds 裁出可视区
// ——XAML 与 seek 处理链一直在，纯布局不可见（2026-10-28 用户截图实测：简洁模式有
// 进度可拖、主界面无进度）。此测试锚定进度行必须完整落在 PlayerDeck 的行高分配内，
// 防行高再次与内容脱节。FluentTheme 必须先于 MainWindow 构造挂载（同
// SettingsAppearanceAlignmentTests 的顺序锚定）。
public class PlayerDeckProgressVisibilityTests
{
    [AvaloniaFact]
    public void MainWindow_PlayerDeck_ProgressRowWithinDeckRowAllocation()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            window.Show();
            // headless 下布局要泵一次 dispatcher 才执行（不泵时 Bounds 为 0，直接读会假失败）
            Dispatcher.UIThread.RunJobs();

            var deck = window.FindDescendantOfType<PlayerDeck>()
                ?? throw new InvalidOperationException("PlayerDeck not found");
            var slider = deck.FindControl<Slider>("ProgressSlider")
                ?? throw new InvalidOperationException("ProgressSlider not found");
            Assert.True(slider.Bounds.Width > 0, "progress slider not laid out");

            // 进度行 = 滑条所在行 Grid（含左端当前位置/右端总时长文本），整行底缘
            // 换算到 PlayerDeck 坐标系后必须在行高分配内——超出即被裁剪不可见。
            // 裁剪者是 deck 根 Border（ClipToBounds，内容 Border 铺满 UserControl），
            // 行与 deck 之间无其他裁剪容器，故与 deck 边界比较即与裁剪边界比较
            var row = Assert.IsType<Grid>(slider.Parent);
            var rowTop = row.TranslatePoint(new Avalonia.Point(0, 0), deck);
            var rowBottom = row.TranslatePoint(
                new Avalonia.Point(row.Bounds.Width, row.Bounds.Height), deck);
            Assert.NotNull(rowTop);
            Assert.NotNull(rowBottom);
            Assert.True(rowBottom.Value.Y <= deck.Bounds.Height + 0.5,
                $"progress row bottom {rowBottom.Value.Y:F1} exceeds deck allocation " +
                $"{deck.Bounds.Height:F1} (row clipped, progress bar invisible)");

            // 拇指渲染位置锚定：Fluent Slider 模板是 15/thumb20/15 像素行（自然高 50），
            // 受限高度下像素行不缩放、拇指顶锚下坠出行带（实测 9px 贴底错位）。
            // 拇指必须完整落在进度行带内——防模板行为变化或行内资源覆写丢失
            var thumb = slider.GetVisualDescendants()
                .OfType<Avalonia.Controls.Primitives.Thumb>().FirstOrDefault()
                ?? throw new InvalidOperationException("slider thumb not found");
            var thumbTop = thumb.TranslatePoint(new Avalonia.Point(0, 0), deck);
            var thumbBottom = thumb.TranslatePoint(
                new Avalonia.Point(thumb.Bounds.Width, thumb.Bounds.Height), deck);
            Assert.NotNull(thumbTop);
            Assert.NotNull(thumbBottom);
            Assert.True(thumbTop.Value.Y >= rowTop.Value.Y - 0.5 &&
                        thumbBottom.Value.Y <= rowBottom.Value.Y + 0.5,
                $"slider thumb {thumbTop.Value.Y:F1}..{thumbBottom.Value.Y:F1} escapes " +
                $"progress row band {rowTop.Value.Y:F1}..{rowBottom.Value.Y:F1}");
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
