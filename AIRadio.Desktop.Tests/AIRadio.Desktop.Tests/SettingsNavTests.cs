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

    [AvaloniaFact]
    public void ProfileCard_NullServiceHidesCard_AndDirectPropertyDrivesVisibility()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var card = view.FindControl<Border>("ProfileCard")
                ?? throw new InvalidOperationException("ProfileCard not found");
            var digest = view.FindControl<TextBlock>("ProfileCardDigest")
                ?? throw new InvalidOperationException("ProfileCardDigest not found");

            // CreateViewModel 不注入画像服务（null）：切页命令可执行不抛，卡片保持隐藏
            vm.SettingsVM.ShowProfileTabCommand.Execute().Subscribe();
            Assert.False(vm.SettingsVM.ProfileHasCard);
            Assert.False(card.IsVisible);

            // 直赋 VM 属性驱动卡片可见与 digest 文本绑定（绑定激活在逻辑树 attach，与 IsVisible 无关）
            vm.SettingsVM.ProfileHasCard = true;
            vm.SettingsVM.ProfileHasDigest = true;
            vm.SettingsVM.ProfileDigestText = "测试归纳";
            Assert.True(card.IsVisible);
            Assert.Equal("测试归纳", digest.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void ProfileCard_StyleTagAndImpressionControls_BindToListProperties()
    {
        // 印象优先重排的新分区：风格标签/性格印象控件经 ItemsSource/文本绑定直连 VM——
        // 直赋属性断言绑定末端（隐藏弹层不布局，逻辑树 attach 即激活绑定）
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var styleTags = view.FindControl<ItemsControl>("ProfileStyleTags")
                ?? throw new InvalidOperationException("ProfileStyleTags not found");
            var impression = view.FindControl<TextBlock>("ProfileCardImpression")
                ?? throw new InvalidOperationException("ProfileCardImpression not found");
            var personaTraits = view.FindControl<ItemsControl>("ProfilePersonaTraits")
                ?? throw new InvalidOperationException("ProfilePersonaTraits not found");
            var impressionTraits = view.FindControl<ItemsControl>("ProfileImpressionTraits")
                ?? throw new InvalidOperationException("ProfileImpressionTraits not found");
            var topArtists = view.FindControl<TextBlock>("ProfileTopArtistsText")
                ?? throw new InvalidOperationException("ProfileTopArtistsText not found");

            vm.SettingsVM.ProfileHasCard = true;
            vm.SettingsVM.ProfileHasStyleTags = true;
            vm.SettingsVM.ProfileStyleTagItems = new List<string> { "华语流行", "深夜" };
            vm.SettingsVM.ProfileHasImpression = true;
            vm.SettingsVM.ProfileImpressionText = "直接爽快";
            vm.SettingsVM.ProfileHasPersonaTraits = true;
            vm.SettingsVM.ProfilePersonaTraitItems = new List<string> { "爱完整听完" };
            vm.SettingsVM.ProfileHasImpressionTraits = true;
            vm.SettingsVM.ProfileImpressionTraitItems = new List<string> { "直接" };
            vm.SettingsVM.ProfileTopArtistsDetail = "周杰伦 ×32";

            Assert.True(styleTags.IsVisible);
            Assert.Equal(2, styleTags.Items.Count());
            Assert.True(impression.IsVisible);
            Assert.Equal("直接爽快", impression.Text);
            Assert.True(personaTraits.IsVisible);
            var trait = Assert.Single(personaTraits.Items);
            Assert.Equal("爱完整听完", trait);
            // 印象特质芯片与歌手次数 Tooltip（本批唯一 ToolTip.Tip 新用法）同构锚定
            Assert.True(impressionTraits.IsVisible);
            Assert.Equal("直接", Assert.Single(impressionTraits.Items));
            Assert.Equal("周杰伦 ×32", ToolTip.GetTip(topArtists));
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
