using System;
using System.Threading.Tasks;
using AIRadio.Desktop.Views;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 任务栏占位方块治理（2026-10-26 诊断）：任务栏在窗口可见后向窗口线程同步拉取按钮
// 图标，启动期 UI 线程卡顿（调试器符号加载/冷启动 IO）会让拉取超时→灰色占位方块
// 且任务栏不重试，直到最小化/还原。修复=可见后延时重发新 WindowIcon 实例触发
// WM_SETICON 与任务栏重绘提示；复现/修法验证见当日对照实验（stall/stallnudge）。
public class MainWindowTaskbarIconReassertTests
{
    [AvaloniaFact]
    public void ReassertTaskbarIcon_ReplacesIconWithFreshInstance()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            var original = window.Icon;

            window.ReassertTaskbarIcon();

            Assert.NotNull(window.Icon);
            Assert.NotSame(original, window.Icon);

            // 可重复调用：每次都是新实例（引用比较短路是重发失效的变异点）
            var first = window.Icon;
            window.ReassertTaskbarIcon();
            Assert.NotSame(first, window.Icon);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Opened_SchedulesDelayedIconReassert()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Assert.NotNull(window.Icon);
            var original = window.Icon;

            // 1.5s 档定时器应替换图标实例；给 6s 预算防 CI 慢机
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
            while (ReferenceEquals(window.Icon, original))
            {
                Assert.True(DateTime.UtcNow < deadline, "1.5s 重发档未在 6s 预算内触发");
                await Task.Delay(100);
            }

            Assert.NotSame(original, window.Icon);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Dispose_StopsPendingReassertTimers()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        MainWindow? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            window.Close(); // OnClosed→Dispose：挂起的重发定时器必须被停掉
            var atClose = window.Icon;

            // 等过 1.5s 档窗口（headless await 期间 Dispatcher 照常泵，未停的定时器会触发替换）
            await Task.Delay(2000);

            Assert.NotNull(atClose);
            Assert.Same(atClose, window.Icon);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }

    // 配置事实锚定（零时长成本替代 6s 档轮询）：两档语义=短档速愈 + 长档兜住慢启动
    // （调试器符号加载可达数秒）。静默删掉长档会让长卡顿场景回退到"须手点两下"。
    [Fact]
    public void IconReassertDelays_KeepsFastAndLongStallCoverage()
    {
        Assert.Equal(2, MainWindow.IconReassertDelays.Length);
        Assert.InRange(MainWindow.IconReassertDelays[0].TotalSeconds, 0.5, 2.0);
        Assert.InRange(MainWindow.IconReassertDelays[1].TotalSeconds, 4.0, 15.0);
    }
}
