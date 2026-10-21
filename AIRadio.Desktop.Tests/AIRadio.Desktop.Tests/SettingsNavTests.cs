using System;
using System.Reactive.Concurrency;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 设置页侧边导航接线：TabIndex（VM）→ 面板 IsVisible / 导航按钮 Classes.active（View）
// 的绑定链端到端锚定。弹层保持隐藏（IsSettingsOpen=false）——绑定激活在逻辑树 attach、
// 与 IsVisible 无关，且隐藏子树不布局可规避 headless 无主题时 ToggleSwitch 的布局异常
// （同 InputFocusReleaseTests 手法）。断言的是绑定求值结果（属性值），非真实可见性。
public class SettingsNavTests
{
    [AvaloniaFact]
    public void TabIndex_FlipsPageVisibilityAndNavActiveClass()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Assert.False(vm.IsSettingsOpen);

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            string[] pageNames = { "PageAi", "PageMusic", "PageHost", "PageAppearance", "PageProfile" };
            string[] navNames = { "NavAi", "NavMusic", "NavHost", "NavAppearance", "NavProfile" };
            var pages = new StackPanel[pageNames.Length];
            var navs = new Button[navNames.Length];
            for (var i = 0; i < pageNames.Length; i++)
            {
                pages[i] = view.FindControl<StackPanel>(pageNames[i])
                    ?? throw new InvalidOperationException($"{pageNames[i]} not found");
                navs[i] = view.FindControl<Button>(navNames[i])
                    ?? throw new InvalidOperationException($"{navNames[i]} not found");
            }

            void AssertOnly(int index)
            {
                Assert.Equal(index, vm.SettingsVM.TabIndex);
                for (var i = 0; i < pages.Length; i++)
                {
                    Assert.Equal(i == index, pages[i].IsVisible);
                    Assert.Equal(i == index, navs[i].Classes.Contains("active"));
                }
            }

            // 默认 AI 服务页
            AssertOnly(0);

            // VM 直改 → 视图绑定跟随
            vm.SettingsVM.TabIndex = 2;
            AssertOnly(2);

            // 命令 → TabIndex → 绑定链端到端
            vm.SettingsVM.ShowProfileTabCommand.Execute().Subscribe();
            AssertOnly(4);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
