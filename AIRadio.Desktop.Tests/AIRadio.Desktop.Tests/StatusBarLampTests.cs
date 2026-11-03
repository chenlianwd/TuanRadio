using System;
using System.Reactive.Concurrency;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// StatusBar ON AIR 灯箱回归：灯随 RadioState 派生翻转——Idle 熄灯（字母常驻），
/// 直播态（Playing/Speaking）呼吸辉光，非直播点亮态（Searching/Error 等）恒亮不闪。
/// </summary>
public class StatusBarLampTests
{
    /// <summary>fixture 家族约定：真实 VM + 关窗 + Dispose + 还原调度器（同 ChatAreaMicButtonTests）。</summary>
    private static void RunWithStatusBar(Action<StatusBar, MainWindowViewModel> body)
    {
        var original = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        Window? window = null;
        MainWindowViewModel? vm = null;
        try
        {
            vm = ChatAreaMicButtonTests.CreateViewModel();
            var view = new StatusBar { DataContext = vm };
            window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            body(view, vm);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            RxApp.MainThreadScheduler = original;
        }
    }

    private static T Find<T>(StatusBar view, string name) where T : class
        => view.FindNameScope()?.Find<T>(name)
           ?? throw new InvalidOperationException($"{name} not found in StatusBar");

    [AvaloniaFact]
    public void Idle_LampOff_LettersStayOnSign()
    {
        RunWithStatusBar((view, _) =>
        {
            var halo = Find<Ellipse>(view, "StateLampHalo");
            var lamp = Find<Ellipse>(view, "StateLamp");
            var text = Find<TextBlock>(view, "StateText");

            Assert.False(halo.IsVisible);
            Assert.DoesNotContain("lit", lamp.Classes);
            Assert.DoesNotContain("breathing", halo.Classes);
            // 熄灯态灯泡降透明度显乳白玻璃感（锚基态样式 0.45：删基态样式则回默认 1 即红；
            // 与 Playing 的 lit=1 成对，两条合起来对两个样式各有捕获）
            Assert.Equal(0.45, lamp.Opacity, 2);
            // 熄灯态：灯箱字母常驻（真实 ON AIR 灯箱的背光熄灭语义），不再回显品牌字
            Assert.Equal("ON AIR", text.Text);
        });
    }

    [AvaloniaFact]
    public void Playing_LampLitAndBreathing()
    {
        RunWithStatusBar((view, vm) =>
        {
            vm.PlayerVM.IsPlaying = true;
            Dispatcher.UIThread.RunJobs();

            var halo = Find<Ellipse>(view, "StateLampHalo");
            var lamp = Find<Ellipse>(view, "StateLamp");
            var text = Find<TextBlock>(view, "StateText");

            Assert.True(halo.IsVisible);
            Assert.Contains("lit", lamp.Classes);
            Assert.Contains("breathing", halo.Classes);
            // 点亮态满亮（锚 .lit 样式=1：删 .lit 样式则停在基态 0.45 即红）
            Assert.Equal(1.0, lamp.Opacity, 2);
            Assert.Equal("ON AIR", text.Text);
        });
    }

    [AvaloniaFact]
    public void Speaking_BreathesLikeLive()
    {
        RunWithStatusBar((view, vm) =>
        {
            vm.ChatVM.IsSpeaking = true;
            Dispatcher.UIThread.RunJobs();

            var halo = Find<Ellipse>(view, "StateLampHalo");
            Assert.Contains("breathing", halo.Classes);
            Assert.Equal("SPEAKING", Find<TextBlock>(view, "StateText").Text);
        });
    }

    [AvaloniaTheory]
    [InlineData("curating")]
    [InlineData("searching")]
    [InlineData("error")]
    public void NonLiveActiveStates_LitWithoutBreathing(string mode)
    {
        RunWithStatusBar((view, vm) =>
        {
            if (mode == "curating") vm.ChatVM.IsProcessing = true;
            else if (mode == "searching") vm.PlaylistVM.IsSearching = true;
            else vm.ChatVM.HasFailure = true;
            Dispatcher.UIThread.RunJobs();

            var halo = Find<Ellipse>(view, "StateLampHalo");
            var lamp = Find<Ellipse>(view, "StateLamp");

            // 策划/搜索/错误是「点亮但不闪」：呼吸只属于直播态，闪烁会与错误告警混淆
            Assert.True(halo.IsVisible);
            Assert.Contains("lit", lamp.Classes);
            Assert.DoesNotContain("breathing", halo.Classes);
        });
    }
}
