using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
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

    // 点徽章即切换主播（2026-11-03 用户裁决，右侧独立切主播按钮已删）：
    // 真实指针点徽章开弹层、再点关（第二次经全屏 dismiss 层关闭，殊途同归）；
    // 全窗口唯一绑定 ToggleCharacterPickerCommand 的按钮是徽章——旧按钮回潮即红
    // 点徽章即切换主播（2026-11-03 用户裁决，右侧独立切主播按钮已删）。
    // 不用真实指针：探针实证 headless 下 MainWindow（SystemDecorations=None）阻断指针
    // 路由、补回装饰后 click 仍不触发（普通 Window 同手法正常）——按钮→命令的触发是
    // 框架行为（实机日常验证），此锚聚焦三段各自可验的接缝：徽章绑定/唯一绑定者/命令切换语义
    [AvaloniaFact]
    public async Task AvatarButton_BoundToPickerToggle_AndReplacesOldHeaderButton()
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

            var badgeButton = window.FindControl<Button>("AvatarButton")
                ?? throw new InvalidOperationException("AvatarButton not found");
            Assert.Same(vm.ToggleCharacterPickerCommand, badgeButton.Command);

            // 独立切主播按钮已删——若回潮则出现第二个绑定者
            var toggles = window.GetVisualDescendants().OfType<Button>()
                .Where(b => ReferenceEquals(b.Command, vm.ToggleCharacterPickerCommand)).ToList();
            Assert.Same(badgeButton, Assert.Single(toggles));

            // 先让 VM 启动链沉淀（初始角色应用会复位弹层标志），再直驱验证切换语义
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            await vm.ToggleCharacterPickerCommand.Execute().FirstAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsCharacterPickerOpen);
            await vm.ToggleCharacterPickerCommand.Execute().FirstAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsCharacterPickerOpen);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 黑胶唱片头像：中心标签用主播专属识别色、字母随主播切换——颜色↔主播映射的头部锚。
    // 钉死 Dark 档（转换器双档色表按 ActualThemeVariant 分流，主题感知断言需确定性）
    [AvaloniaFact]
    public async Task VinylBadge_LabelColorAndLetterFollowCharacter()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            // headless 无主题字典：注入沟纹/反光点消费的颜色键，DynamicResource 才可解析
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["C_FFEDEDF5"] = Color.FromUInt32(0xFFEDEDF5)
            });
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var label = window.FindControl<Ellipse>("AvatarLabel")
                ?? throw new InvalidOperationException("AvatarLabel not found");
            var letter = window.FindControl<TextBlock>("AvatarLetter")
                ?? throw new InvalidOperationException("AvatarLetter not found");
            // 黑胶质感锚（2026-11-03 用户反馈"看不出是黑胶唱片"）：外缘高光+三圈沟纹
            // = 盘面上 ≥4 个描边环（标签 Fill 无描边不计入）；退化回素圈在此大声失败
            var disc = window.FindControl<Grid>("AvatarDisc")
                ?? throw new InvalidOperationException("AvatarDisc not found");
            Assert.True(disc.Children.OfType<Ellipse>().Count(e => e.Stroke != null) >= 4,
                $"黑胶沟纹退化：描边环仅 {disc.Children.OfType<Ellipse>().Count(e => e.Stroke != null)} 个");

            Assert.Equal(Color.FromUInt32(0xFFE8A33D), Assert.IsAssignableFrom<ISolidColorBrush>(label.Fill).Color);
            // Dark 档标签是亮色，字母配深墨
            Assert.Equal(Color.FromUInt32(0xFF14141C), Assert.IsAssignableFrom<ISolidColorBrush>(letter.Foreground).Color);
            Assert.Equal("L", letter.Text);

            await vm.SelectCharacterCommand.Execute(vm.Characters[1]).FirstAsync(); // Aster
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Color.FromUInt32(0xFF9D7BE8), Assert.IsAssignableFrom<ISolidColorBrush>(label.Fill).Color);
            Assert.Equal("A", letter.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            Application.Current.RequestedThemeVariant = originalTheme;
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 选主播弹层首字母用主播专属识别色（盲审低危补锚）：删 Foreground 绑定即红。
    // 需 FluentTheme：headless 裸跑时 ItemsControl 无模板不物化条目（滚动条测试同坑）
    [AvaloniaFact]
    public void CharacterPicker_FirstLetterUsesHostAccent()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var fluent = new Avalonia.Themes.Fluent.FluentTheme();
        Application.Current.Styles.Add(fluent);
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            // 先让 VM 启动链沉淀：初始角色应用（SwitchCharacter）会顺手关弹层标志，
            // 设标志跑赢启动续体会被复位（探针实证）
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            vm.IsCharacterPickerOpen = true;
            Dispatcher.UIThread.RunJobs();

            var picker = window.GetVisualDescendants().OfType<CharacterPicker>().Single();
            // 弹层条目 = 加粗首字母 + 常规名（共 6 个条目，取第一个 = Lumen）；首字母必须是专属色而非继承前景
            var boldLetter = picker.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.FontWeight == FontWeight.Bold && t.Text?.Length == 1);
            var expected = AIRadio.Desktop.Converters.CharacterAccentConverter.GetAccent(vm.Characters[0]);
            Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(boldLetter.Foreground).Color);
            Assert.Equal("L", boldLetter.Text);
        }
        finally
        {
            window?.Close();
            Application.Current.Styles.Remove(fluent);
            vm.Dispose();
            Application.Current.RequestedThemeVariant = originalTheme;
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 说话期黑胶唱片旋转（电唱机隐喻）、停止后回正——闲置态字母停在任意角度会歪着像缺陷
    [AvaloniaFact]
    public async Task DiscSpinsWhileSpeaking_ResetsUprightWhenDone()
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

            var disc = window.FindControl<Grid>("AvatarDisc")
                ?? throw new InvalidOperationException("AvatarDisc not found");
            var spin = Assert.IsType<RotateTransform>(disc.RenderTransform);
            var border = window.FindControl<Border>("AvatarBorder") ?? throw new InvalidOperationException("AvatarBorder not found");
            // 真实几何锚：旋转 180° 时盘心 (17,17) 映射到外框 AvatarBorder 必须原地不动（自转而非公转甩出框外）
            Assert.Equal(RelativePoint.Center, disc.RenderTransformOrigin);
            Assert.Equal(0, spin.CenterX);
            Assert.Equal(0, spin.CenterY);
            Assert.Equal(0, spin.Angle);
            spin.Angle = 180;
            Dispatcher.UIThread.RunJobs();
            var toBorder = disc.TransformToVisual(border)!.Value;
            var mappedCenter = toBorder.Transform(new Point(17, 17));
            var mappedTopLeft = toBorder.Transform(new Point(0, 0));
            Assert.Equal(17, mappedCenter.X, 1);
            Assert.Equal(17, mappedCenter.Y, 1);
            Assert.Equal(34, mappedTopLeft.X, 1);
            Assert.Equal(34, mappedTopLeft.Y, 1);
            spin.Angle = 0;

            vm.ChatVM.IsSpeaking = true;
            Dispatcher.UIThread.RunJobs();
            var deadline = DateTimeOffset.Now.AddSeconds(2);
            var sawSpin = false;
            while (DateTimeOffset.Now < deadline)
            {
                if (spin.Angle > 0)
                {
                    sawSpin = true;
                    break;
                }
                await Task.Delay(8);
            }
            Assert.True(sawSpin, $"disc never spun, angle={spin.Angle}");

            vm.ChatVM.IsSpeaking = false;
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(80); // 让已取消循环的收尾 continuation 跑完
            Assert.Equal(0, spin.Angle);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // 锚定设置按钮的实心齿轮剪影（单 Path、Fill 填充、无 Stroke）——两代旧图标在此暴露：
    // 调音台滑杆（单体 Stroke 线稿）与描边圆环+独立齿块（Panel 组合，被用户读成太阳）。
    [AvaloniaFact]
    public void BrandHeader_SettingsButton_ShowsCompositeGearGlyph()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            // headless 无主题字典：注入齿轮消费的颜色键，DynamicResource 才可解析
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["C_FFEDEDF5"] = Avalonia.Media.Color.FromUInt32(0xFFEDEDF5)
            });
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.FindControl<Button>("SettingsButton")
                ?? throw new InvalidOperationException("SettingsButton not found");

            // 实心剪影：填充成体、无描边（线稿 Stroke 齿轮 15px 下糊、分离齿块像太阳）
            var gear = Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content);
            Assert.NotNull(gear.Fill);
            Assert.Null(gear.Stroke);
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

    // 锚定辉光事件链的两级生产接线：AudioService 播放线程峰值 → ChatVM 转发（构造
    // 时 += TtsLevelChanged）→ MainWindow 订阅消费（DataContextChanged 时 +=）。
    // 直驱 MainWindow.OnTtsLevelChanged 的测试测不到这两级 +=，任一被误删则
    // 辉光恒基线不动而测试全绿——本测试以 mock.Raise 走完整链路补上该缺口。
    [AvaloniaFact]
    public async Task SpeakingBadge_GlowWiredThroughFullEventChain()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out var audio);
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

            // Moq 在调用线程同步触发事件（等效播放线程直调），订阅链只写 volatile 字段
            audio.Raise(x => x.TtsLevelChanged += null, 1.0f);
            var deadline = DateTimeOffset.Now.AddSeconds(2);
            var sawBright = false;
            while (DateTimeOffset.Now < deadline)
            {
                if (badge.Opacity > 0.95)
                {
                    sawBright = true;
                    break;
                }
                await Task.Delay(8);
            }
            Assert.True(sawBright, $"glow event chain broken, opacity={badge.Opacity}");
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
