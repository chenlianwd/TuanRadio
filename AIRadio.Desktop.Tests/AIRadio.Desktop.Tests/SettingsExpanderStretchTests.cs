using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 可选音源三块 Expander 拉满内容列的锚定：Fluent Expander ControlTheme 默认
// HorizontalAlignment=Left 按内容自适应宽度——折叠态宽=标题宽，展开态宽=
// max(标题, 内容最小期望宽)，展开瞬间整体变宽，头部右缘 chevron 随之平移
// （2026-11-04 用户报"展开前后三角按钮位置变化"），且三块折叠态宽度随标题
// 长短不齐。锚定三点：Expander 宽==父列宽、折叠/展开两态 chevron X 恒定、
// 三块等宽。YouTube 块受实验源可见性门控，不可见时不布局（Bounds 为 0），
// 跳过但要求至少两块被断言防全跳假绿。
public class SettingsExpanderStretchTests
{
    [AvaloniaFact]
    public void OptionalSourceExpanders_StretchColumnAndChevronStaysPut()
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
            vm.SettingsVM.TabIndex = 1;
            Dispatcher.UIThread.RunJobs();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var specs = new (Expander Expander, Func<MainWindowViewModel, bool> Get, Action<MainWindowViewModel, bool> Set)[]
            {
                (view.FindControl<Expander>("OpenSubsonicExpander") ?? throw new InvalidOperationException("OpenSubsonicExpander not found"),
                    v => v.SettingsVM.IsOpenSubsonicExpanded, (v, x) => v.SettingsVM.IsOpenSubsonicExpanded = x),
                (view.FindControl<Expander>("AudiusExpander") ?? throw new InvalidOperationException("AudiusExpander not found"),
                    v => v.SettingsVM.IsAudiusExpanded, (v, x) => v.SettingsVM.IsAudiusExpanded = x),
                (view.FindControl<Expander>("YtdlpExpander") ?? throw new InvalidOperationException("YtdlpExpander not found"),
                    v => v.SettingsVM.IsYtdlpExpanded, (v, x) => v.SettingsVM.IsYtdlpExpanded = x),
            };

            var widths = new List<double>();
            var asserted = 0;
            foreach (var (expander, get, set) in specs)
            {
                if (!expander.IsVisible)
                    continue;
                asserted++;

                // 拉满父列（父为"音源账号"内层 StackPanel，Stretch 拉满内容列宽）
                var parentWidth = ((Control)expander.Parent!).Bounds.Width;
                Assert.True(expander.Bounds.Width > 0, $"{expander.Name} not laid out");
                Assert.Equal(parentWidth, expander.Bounds.Width, 0.5);

                // 折叠态 chevron 位置 → 展开 → chevron X 必须纹丝不动（宽度也不变）
                var collapsedX = Chevron(expander).Bounds.X;

                set(vm, true);
                Assert.True(get(vm));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expander.Bounds.Width, ((Control)expander.Parent!).Bounds.Width, 0.5);
                var expandedX = Chevron(expander).Bounds.X;
                Assert.Equal(collapsedX, expandedX, 0.5);

                set(vm, false);
                Assert.False(get(vm));
                Dispatcher.UIThread.RunJobs();

                widths.Add(expander.Bounds.Width);
            }

            Assert.True(asserted >= 2, $"expected >=2 visible expanders, got {asserted}");
            // 三块等宽（折叠态标题长短不再影响宽度）
            Assert.All(widths, w => Assert.Equal(widths[0], w, 0.5));
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private static Border Chevron(Expander expander) =>
        expander.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "ExpandCollapseChevronBorder")
        ?? throw new InvalidOperationException("ExpandCollapseChevronBorder not found");
}
