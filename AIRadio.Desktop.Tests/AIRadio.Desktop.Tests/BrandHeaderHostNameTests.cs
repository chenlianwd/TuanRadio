using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

// 回归：BrandHeader 圆徽章只显示主播名首字母，脱离选择器弹层后无任何上下文可读性
// （用户反馈"看不懂"）。徽章旁需显示主播全名，且随主持人切换实时更新；
// 首字母徽章（DJ 说话弹跳动效的锚点）保持不变。
public class BrandHeaderHostNameTests
{
    [AvaloniaFact]
    public async Task BrandHeader_ShowsHostFullNameNextToInitial_DefaultLumen()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var initial = window.FindControl<TextBlock>("AvatarLetter")
                ?? throw new InvalidOperationException("AvatarLetter not found");
            var hostName = window.FindControl<TextBlock>("HostNameText")
                ?? throw new InvalidOperationException("HostNameText not found");

            Assert.Equal("L", initial.Text);
            Assert.Equal("Lumen", hostName.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public async Task BrandHeader_HostNameFollowsCharacterSwitch()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.SelectCharacterCommand.Execute(vm.Characters[1]).FirstAsync(); // Aster
            Dispatcher.UIThread.RunJobs();

            var initial = window.FindControl<TextBlock>("AvatarLetter")
                ?? throw new InvalidOperationException("AvatarLetter not found");
            var hostName = window.FindControl<TextBlock>("HostNameText")
                ?? throw new InvalidOperationException("HostNameText not found");

            Assert.Equal("A", initial.Text);
            Assert.Equal("Aster", hostName.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 回归：DJ 说话的可感知反馈。旧实现是透明度闪烁（深色徽章在深色头栏上不可见，
    // 用户从未感知到）；现要求说话中徽章与主播名进入 speaking 样式类（描圈+点亮）。
    [AvaloniaFact]
    public async Task BrandHeader_SpeakingState_LightsBadgeAndHostName()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var badge = window.FindControl<Border>("AvatarBorder")
                ?? throw new InvalidOperationException("AvatarBorder not found");
            var hostName = window.FindControl<TextBlock>("HostNameText")
                ?? throw new InvalidOperationException("HostNameText not found");

            Assert.DoesNotContain("speaking", badge.Classes);
            Assert.DoesNotContain("speaking", hostName.Classes);

            vm.ChatVM.IsSpeaking = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("speaking", badge.Classes);
            Assert.Contains("speaking", hostName.Classes);

            vm.ChatVM.IsSpeaking = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("speaking", badge.Classes);
            Assert.DoesNotContain("speaking", hostName.Classes);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 锚定 DjVisualCue → OnDjVisualCue → PlayBounceAsync 的真实接线，且弹跳必须是
    // 位移（TranslateTransform.Y < 0）而非透明度闪烁——旧实现复活会在此暴露。
    [AvaloniaFact]
    public async Task DjVisualCue_PlaysRealTranslateBounce()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var badge = window.FindControl<Border>("AvatarBorder")
                ?? throw new InvalidOperationException("AvatarBorder not found");

            // DjVisualCue 是 event 外部不可 Invoke，经 internal handler 直驱（同 ClockStage.RouteStageTap 手法）
            ((MainWindow)window).OnDjVisualCue("smile", "wave");

            var deadline = DateTimeOffset.Now.AddSeconds(2);
            var sawDisplacement = false;
            while (DateTimeOffset.Now < deadline)
            {
                var y = (badge.RenderTransform as TranslateTransform)?.Y ?? 0;
                if (y < -0.5)
                {
                    sawDisplacement = true;
                    break;
                }
                Assert.Equal(1.0, badge.Opacity);
                await Task.Delay(8);
            }
            Assert.True(sawDisplacement);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 说话徽章辉光随 TTS 播放链真实音量：IsSpeaking 置位即开辉光循环（首帧同步落基线），
    // 透明度跟随峰值目标起伏，停止说话立即复位 1.0。旧"常亮无生命感"实现复活会在此暴露
    // （IsSpeaking 期间 Opacity 恒 1 即失败）。
    [AvaloniaFact]
    public async Task SpeakingBadge_GlowFollowsTtsLevel_AndResetsWhenDone()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var badge = window.FindControl<Border>("AvatarBorder")
                ?? throw new InvalidOperationException("AvatarBorder not found");

            vm.ChatVM.IsSpeaking = true;
            Dispatcher.UIThread.RunJobs();
            // 辉光循环同步首帧：说话一开始就落到基线（而非停留在 1）
            Assert.True(badge.Opacity < 1.0);

            ((MainWindow)window).OnTtsLevelChanged(1.0f);
            var brightDeadline = DateTimeOffset.Now.AddSeconds(2);
            var sawBright = false;
            while (DateTimeOffset.Now < brightDeadline)
            {
                if (badge.Opacity > 0.95)
                {
                    sawBright = true;
                    break;
                }
                await Task.Delay(8);
            }
            Assert.True(sawBright, $"glow never brightened, opacity={badge.Opacity}");

            ((MainWindow)window).OnTtsLevelChanged(0f);
            var dimDeadline = DateTimeOffset.Now.AddSeconds(2);
            var sawDim = false;
            while (DateTimeOffset.Now < dimDeadline)
            {
                if (badge.Opacity < 0.75)
                {
                    sawDim = true;
                    break;
                }
                await Task.Delay(8);
            }
            Assert.True(sawDim, $"glow never dimmed, opacity={badge.Opacity}");

            vm.ChatVM.IsSpeaking = false;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(80); // 让已取消循环的收尾 continuation 跑完
            Assert.Equal(1.0, badge.Opacity);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 非说话期间的峰值事件不得驱动徽章（辉光只在 IsSpeaking 期间运行）
    [AvaloniaFact]
    public async Task SpeakingBadge_IgnoresTtsLevel_WhenNotSpeaking()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var badge = window.FindControl<Border>("AvatarBorder")
                ?? throw new InvalidOperationException("AvatarBorder not found");

            ((MainWindow)window).OnTtsLevelChanged(1.0f);
            await Task.Delay(150);

            Assert.Equal(1.0, badge.Opacity);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
