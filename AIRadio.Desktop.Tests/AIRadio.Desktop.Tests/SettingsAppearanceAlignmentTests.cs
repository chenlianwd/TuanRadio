using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 外观页控件列右缘对齐锚定：配色主题/频谱样式两行的 ComboBox 与三行 ToggleSwitch
// 必须与内容列右缘对齐。Fluent ControlTheme 对 ComboBox 默认 HorizontalAlignment=Left，
// 定宽列（*,180）内不显式 Stretch 时按内容宽度靠左，右缘与开关行错位（2026-10-27
// 用户截图实测）。真实 MainWindow + 真实 FluentTheme 下的布局断言；主题必须先于
// MainWindow 构造挂到 Application.Styles——构造后再挂时 ToggleSwitch 模板应用异常
// （PART_MovingKnobs 缺失，原因未深究，实证有效的顺序即此处锚定的顺序）。
public class SettingsAppearanceAlignmentTests
{
    [AvaloniaFact]
    public void AppearancePage_ComboBoxAndToggleSwitch_RightEdgesAlign()
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
            vm.IsSettingsOpen = true;
            vm.SettingsVM.TabIndex = 3;
            // 弹层在 Show 后才可见，headless 下该子树的布局要泵一次 dispatcher 才执行
            // （不泵时 Bounds 仍为 0，直接读会假失败）
            Dispatcher.UIThread.RunJobs();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var page = view.FindControl<StackPanel>("PageAppearance")
                ?? throw new InvalidOperationException("PageAppearance not found");

            // 五个控件行：Grid 直挂 PageAppearance 或挂其子 StackPanel（外观页两组布局）
            var rows = page.GetVisualDescendants().OfType<Grid>()
                .Where(g => ReferenceEquals(g.Parent, page) || (g.Parent is StackPanel sp && ReferenceEquals(sp.Parent, page)))
                .ToList();
            Assert.Equal(5, rows.Count);

            var rightEdges = new List<double>();
            foreach (var row in rows)
            {
                var combo = row.Children.OfType<ComboBox>().FirstOrDefault();
                var sw = row.Children.OfType<ToggleSwitch>().FirstOrDefault();
                var control = (Control?)(combo ?? (Control?)sw)
                    ?? throw new InvalidOperationException("row without ComboBox/ToggleSwitch");
                Assert.True(control.Bounds.Width > 0, $"{control.GetType().Name} not laid out");
                rightEdges.Add(control.Bounds.Right);
            }

            // 五行控件右缘一致，且对齐到行（=内容列）右缘
            Assert.All(rightEdges, r => Assert.Equal(rightEdges[0], r, 0.5));
            Assert.Equal(rows[0].Bounds.Right, rightEdges[0], 0.5);
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
