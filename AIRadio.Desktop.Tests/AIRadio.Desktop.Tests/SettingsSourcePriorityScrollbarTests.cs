using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 音源优先级按钮被 overlay 滚动条遮挡的回归锚（2026-11-03 用户报"音源调整优先级的
// 按钮被滚动条挡住无法调整"）：Avalonia 11 Fluent 的 ScrollViewer 滚动条悬浮于内容
// 之上——宽 16px、不占布局空间（headless 探针定谳：ScrollContentPresenter 恒满宽，
// 竖向 ScrollBar 叠在视口右缘 16px 命中区），内容贴右缘的可点控件会被滚动条吃掉点击。
// 修复=SettingsView 滚动内容 Grid 统一右让 16px。真实 MainWindow + 真实 FluentTheme
// + 真实轻量 Broker（LocalLibrary+OpenSubsonic 两行 → 4 个 ↑/↓ 按钮物化），
// 断言竖向滚动条可见时按钮整体位于其左缘左侧。
public class SettingsSourcePriorityScrollbarTests
{
    [AvaloniaFact]
    public void MusicProviderPriorityButtons_ClearOfOverlayScrollbar()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var broker = new MusicSourceBroker(
            new LocalLibraryProvider(Path.Combine(dir, "library.json")),
            new OpenSubsonicProvider(new Mock<ISecureStorage>().Object));
        var vm = ChatAreaMicButtonTests.CreateViewModel(out _, null, broker);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            // 压到 600（覆写 XAML 的 MinHeight=700 本地值）迫使音乐页大幅溢出视口：
            // 原先 700 下限下两源内容恰好塞满（实测余量个位数 px），字体度量/DPI 漂移
            // 即翻转为不溢出（2026-11-03 实机踩坑，干净 HEAD 稳定红）；600 余量 ≈75px 抗漂移。
            // 竖向滚动条必须可见，否则锚空转——下方可见性断言大声失败而非静默通过
            window = new MainWindow { DataContext = vm, Width = 700, MinHeight = 600, Height = 600 };
            window.Show();
            vm.IsSettingsOpen = true;
            vm.SettingsVM.TabIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // 滚动条可见性比首次布局晚一拍（探针实证：首泵 bar 仍隐形 bounds=0）
            Dispatcher.UIThread.RunJobs();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var viewer = view.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var bar = viewer.GetVisualDescendants().OfType<ScrollBar>()
                .Single(b => b.Orientation == Orientation.Vertical);
            Assert.True(bar.IsVisible, "音乐页应溢出视口使竖向滚动条可见（窗口尺寸假设漂移时需调尺寸）");

            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Content as string is "↑" or "↓")
                .ToList();
            Assert.True(buttons.Count >= 2, $"音源优先级 ↑/↓ 按钮未物化（找到 {buttons.Count} 个）");
            Assert.All(buttons, b => Assert.True(b.Bounds.Width > 0, "按钮未布局"));

            // 统一换算到窗口空间比较：按钮右缘必须不越过滚动条左缘（修复前按钮贴内容
            // 右缘 → 右缘落进 bar 命中区即红）。TransformToVisual 返回的是控件自身
            // 局部坐标→目标空间的矩阵，须变换 Rect(0,0,w,h)——用 Bounds（父空间矩形）
            // 会把控件自身偏移叠加两次（探针实证：bar 报告位=真实位+自身 338 偏移）
            Rect InWindow(Control c)
                => c.TransformToVisual(window) is { } m
                    ? new Rect(c.Bounds.Size).TransformToAABB(m)
                    : c.Bounds;
            var barLeft = InWindow(bar).Left;
            foreach (var b in buttons)
            {
                var rect = InWindow(b);
                Assert.True(rect.Right <= barLeft + 0.5, $"按钮进入 overlay 滚动条命中区：{rect}，barLeft={barLeft}");
            }
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
