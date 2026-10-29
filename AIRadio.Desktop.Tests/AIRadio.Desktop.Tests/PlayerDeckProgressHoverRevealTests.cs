using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 进度条同位显影锚定（2026-10-29 用户反馈常显进度条突兀、丢失以前的简洁观感）：
// 静止态收敛为发丝线——滑条整体降不透明度、拇指与两侧时间文本隐去；悬停播放区
// （真实指针管线驱动 DeckRoot:pointerover，沿祖先链激活）或滑条获焦时 150ms 淡入
// 完整形态。纯样式层变换、布局零位移，静止态轨道照常命中可点按拖动。
// 双向变异锚定：删显影样式 → hover 用例红；删/改静止态样式 → rest 用例红。
// FluentTheme 必须先于 MainWindow 构造挂载（同 PlayerDeckProgressVisibilityTests）。
public class PlayerDeckProgressHoverRevealTests
{
    private sealed class Fixture : IDisposable
    {
        public MainWindowViewModel Vm = null!;
        public Window Window = null!;
        public FluentTheme Theme = null!;
        public PlayerDeck Deck = null!;
        public Slider Slider = null!;
        public Thumb Thumb = null!;
        public TextBlock[] TimeTexts = null!;
        private IScheduler? _originalScheduler;

        public static Fixture Build()
        {
            var f = new Fixture
            {
                _originalScheduler = RxApp.MainThreadScheduler,
                Vm = ChatAreaMicButtonTests.CreateViewModel(),
                Theme = new FluentTheme(),
            };
            RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
            Avalonia.Application.Current!.Styles.Add(f.Theme);
            try
            {
                f.Window = new MainWindow { DataContext = f.Vm, Width = 943, Height = 893 };
                f.Window.Show();
                // headless 下布局要泵一次 dispatcher 才执行（不泵时 Bounds 为 0）
                Dispatcher.UIThread.RunJobs();
                f.Deck = f.Window.FindDescendantOfType<PlayerDeck>()
                    ?? throw new InvalidOperationException("PlayerDeck not found");
                f.Slider = f.Deck.FindControl<Slider>("ProgressSlider")
                    ?? throw new InvalidOperationException("ProgressSlider not found");
                f.Thumb = f.Slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()
                    ?? throw new InvalidOperationException("slider thumb not found");
                f.TimeTexts = f.Deck.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.Classes.Contains("progressTimeText")).ToArray();
                return f;
            }
            catch
            {
                f.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Window?.Close();
            Avalonia.Application.Current!.Styles.Remove(Theme);
            Vm.Dispose();
            if (_originalScheduler is not null) RxApp.MainThreadScheduler = _originalScheduler;
        }
    }

    // 过渡收敛等待：显/隐双向各 150ms 过渡，等 300ms 覆盖启动期首次应用与显隐切换
    private static async Task Settle()
    {
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Progress_QuietAtRest_AndRowNotClipped()
    {
        using var f = Fixture.Build();
        await Settle();

        // 静止态：滑条降不透明度（发丝线感）、拇指与时间文本隐去
        Assert.True(f.Slider.Opacity is > 0.05 and < 0.95,
            $"rest opacity {f.Slider.Opacity} expected dimmed hairline (style removed?)");
        Assert.True(f.Thumb.Opacity < 0.05,
            $"thumb visible at rest: {f.Thumb.Opacity}");
        Assert.Equal(2, f.TimeTexts.Length);
        Assert.All(f.TimeTexts, t => Assert.True(t.Opacity < 0.05,
            $"time text visible at rest: {t.Opacity}"));

        // 行高随发丝线设计回收（136→124 + 间距收紧）后进度行不可再度被裁剪
        var row = Assert.IsType<Grid>(f.Slider.Parent);
        var rowBottom = row.TranslatePoint(
            new Point(row.Bounds.Width, row.Bounds.Height), f.Deck);
        Assert.NotNull(rowBottom);
        Assert.True(rowBottom.Value.Y <= f.Deck.Bounds.Height + 0.5,
            $"progress row bottom {rowBottom.Value.Y:F1} exceeds deck allocation " +
            $"{f.Deck.Bounds.Height:F1} (row clipped, progress bar invisible)");
    }

    [AvaloniaFact]
    public async Task Progress_RevealsOnDeckHover_AndRetreatsWhenPointerLeaves()
    {
        using var f = Fixture.Build();
        await Settle();

        // 真实指针悬停 deck 底部（发丝线区域），沿祖先链点亮 DeckRoot:pointerover
        var p = f.Deck.TranslatePoint(
            new Point(f.Deck.Bounds.Width / 2, f.Deck.Bounds.Height - 12), f.Window)!.Value;
        f.Window.MouseMove(p);
        Dispatcher.UIThread.RunJobs();
        await Settle();

        Assert.True(f.Slider.Opacity > 0.95, $"slider not revealed: {f.Slider.Opacity}");
        Assert.True(f.Thumb.Opacity > 0.95, $"thumb not revealed: {f.Thumb.Opacity}");
        Assert.All(f.TimeTexts, t => Assert.True(t.Opacity > 0.95,
            $"time text not revealed: {t.Opacity}"));

        // 指针离开 deck（落在标题栏上方空白）→ 回落发丝线
        f.Window.MouseMove(new Point(40, 40));
        Dispatcher.UIThread.RunJobs();
        await Settle();

        Assert.True(f.Slider.Opacity is > 0.05 and < 0.95,
            $"opacity after pointer leaves: {f.Slider.Opacity} expected back to hairline");
        Assert.True(f.Thumb.Opacity < 0.05,
            $"thumb still visible after pointer leaves: {f.Thumb.Opacity}");
    }

    [AvaloniaFact]
    public async Task Progress_RevealsOnSliderFocus()
    {
        using var f = Fixture.Build();
        await Settle();

        Assert.True(f.Slider.Focus(), "ProgressSlider not focusable");
        Dispatcher.UIThread.RunJobs();
        await Settle();

        // 键盘可达性：Tab 到滑条即显影滑条与拇指（时间文本为兄弟节点不跟焦，不在此断言）
        Assert.True(f.Slider.Opacity > 0.95, $"slider not revealed on focus: {f.Slider.Opacity}");
        Assert.True(f.Thumb.Opacity > 0.95, $"thumb not revealed on focus: {f.Thumb.Opacity}");
    }
}
