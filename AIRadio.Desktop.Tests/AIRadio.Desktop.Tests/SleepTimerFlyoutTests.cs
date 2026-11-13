using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reflection;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 回归：SleepTimerButton.Flyout 选项按钮在 Click 事件内同步调用 Flyout.Hide() 会拆离浮层并使
// Button.Command 绑定失效，导致 SetSleepTimerCommand 从未执行、再次打开仍停留在“关闭定时”。
// 另锚定“定时结束后退出应用”及默认行为提示文案。
public class SleepTimerFlyoutTests
{
    private static readonly MethodInfo ButtonOnClick =
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Button.OnClick not found");

    [AvaloniaFact]
    public void ClickingOptionsInFlyout_ExecutesCommandAndPersistsSelectionOnReopen()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out var audio);
        var track = new Track { Title = "Test Song", SourceId = "netease" };
        audio.SetupGet(a => a.CurrentTrack).Returns(track);
        ((Subject<Track?>)audio.Object.TrackChanged).OnNext(track);

        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sleepBtn = window.FindControl<Button>("SleepTimerButton")
                ?? throw new InvalidOperationException("SleepTimerButton not found");
            var flyout = Assert.IsType<Flyout>(sleepBtn.Flyout);

            flyout.ShowAt(sleepBtn);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.IsType<StackPanel>(flyout.Content);
            var buttons = panel.Children.OfType<Button>().Where(b => b.Classes.Contains("timer-opt")).ToList();
            Assert.Equal(7, buttons.Count);

            var optOff = buttons[0];
            var opt15 = buttons[1];
            var optTrackEnd = buttons[6];
            Assert.Equal(0, Assert.IsType<int>(optOff.CommandParameter));
            Assert.Equal(15, Assert.IsType<int>(opt15.CommandParameter));
            Assert.Equal(-1, Assert.IsType<int>(optTrackEnd.CommandParameter));
            Assert.True(optTrackEnd.IsEnabled);

            // 初始：关闭定时选中
            Assert.Contains("selected", optOff.Classes);
            Assert.DoesNotContain("selected", opt15.Classes);

            // 点击 15 分钟：Flyout 收起且命令实际生效
            ButtonOnClick.Invoke(opt15, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(flyout.IsOpen);
            Assert.True(vm.IsSleepTimerActive);
            Assert.Equal(SleepTimerMode.Duration, vm.SleepTimerCurrentMode);
            Assert.Equal(15, vm.SleepTimerTargetMinutes);

            // 再次打开 Flyout：15 分钟保持选中态，头部显示剩余时间
            flyout.ShowAt(sleepBtn);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", opt15.Classes);
            Assert.DoesNotContain("selected", optOff.Classes);
            Assert.False(string.IsNullOrWhiteSpace(vm.SleepTimerDisplayStatus));

            // 切换到“播完当前曲后停止”
            ButtonOnClick.Invoke(optTrackEnd, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            Assert.Equal(SleepTimerMode.EndOfTrack, vm.SleepTimerCurrentMode);

            flyout.ShowAt(sleepBtn);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", optTrackEnd.Classes);
            Assert.DoesNotContain("selected", opt15.Classes);

            // 点击“关闭定时”恢复关闭态
            ButtonOnClick.Invoke(optOff, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(flyout.IsOpen);
            Assert.False(vm.IsSleepTimerActive);
            Assert.Equal(SleepTimerMode.Disabled, vm.SleepTimerCurrentMode);

            flyout.ShowAt(sleepBtn);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", optOff.Classes);
            Assert.DoesNotContain("selected", optTrackEnd.Classes);
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void CloseAppCheckboxAndHint_ArePresentAndBound()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            var strings = new ResourceDictionary();
            foreach (var pair in AppLanguage.GetStringTable("zh"))
                strings[pair.Key] = pair.Value;
            window.Resources.MergedDictionaries.Add(strings);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sleepBtn = window.FindControl<Button>("SleepTimerButton")
                ?? throw new InvalidOperationException("SleepTimerButton not found");
            var flyout = Assert.IsType<Flyout>(sleepBtn.Flyout);
            flyout.ShowAt(sleepBtn);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.IsType<StackPanel>(flyout.Content);
            var checkBox = panel.GetVisualDescendants().OfType<CheckBox>().Single();
            var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains(AppLanguage.GetStringTable("zh")["S_SleepTimerCloseApp"], texts);
            Assert.Contains(AppLanguage.GetStringTable("zh")["S_SleepTimerCloseAppHint"], texts);

            Assert.False(vm.SleepTimerCloseAppOnExpiry);
            checkBox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.SleepTimerCloseAppOnExpiry);
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
