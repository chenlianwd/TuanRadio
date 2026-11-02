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
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 进度条同位显影锚定（2026-10-29 用户反馈常显进度条突兀、丢失以前的简洁观感；
// 2026-11-02 用户反馈静止态发丝线中段像"缺口"且太大——Fluent Slider 轨道是
// 「已播|拇指|未播」三段拼的，拇指占位 20px 下没有轨道，隐藏拇指即裸露断口）：
// 静止态收敛为发丝线——滑条整体降不透明度、两侧时间文本隐去，调谐针+轨道补线
// 常显（复古收音机刻度指针语义，拇指不再参与显隐）；悬停播放区（真实指针管线
// 驱动 DeckRoot:pointerover，沿祖先链激活）或滑条获焦时 150ms 淡入完整形态。
// 纯样式层变换、布局零位移，静止态轨道照常命中可点按拖动。
// 双向变异锚定：删显影样式 → hover 用例红；删/改静止态样式 → rest 用例红；
// 去补线（断口复辟）或颜色跟随丢失（基础/pointerover/pressed 任一态样式被删，
// Fluent 拇指色系压过或继承色泄漏）→ 无缝/跟随/pressed 用例红。
// FluentTheme 必须先于 MainWindow 构造挂载（同
// PlayerDeckProgressVisibilityTests）。
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

        // 静止态：滑条降不透明度（发丝线感）、调谐针常显、时间文本隐去；
        // 补线与未播段无缝（断口已填平，不再裸露 20px 空槽）
        Assert.True(f.Slider.Opacity is > 0.05 and < 0.95,
            $"rest opacity {f.Slider.Opacity} expected dimmed hairline (style removed?)");
        Assert.True(f.Thumb.Opacity > 0.95,
            $"thumb hidden at rest: {f.Thumb.Opacity} (needle must stay visible)");
        PlayerDeckVolumeSliderRevealTests.AssertPatchSeamless("progress", f.Slider, f.Thumb);
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
        // 调谐针常显后恒真保底（防"恢复静止隐藏但不恢复显影"的半截变异，同 focus 用例）
        Assert.True(f.Thumb.Opacity > 0.95, $"thumb not revealed: {f.Thumb.Opacity}");
        Assert.All(f.TimeTexts, t => Assert.True(t.Opacity > 0.95,
            $"time text not revealed: {t.Opacity}"));

        // 指针离开 deck（落在标题栏上方空白）→ 回落发丝线（调谐针常显不回落）
        f.Window.MouseMove(new Point(40, 40));
        Dispatcher.UIThread.RunJobs();
        await Settle();

        Assert.True(f.Slider.Opacity is > 0.05 and < 0.95,
            $"opacity after pointer leaves: {f.Slider.Opacity} expected back to hairline");
        Assert.True(f.Thumb.Opacity > 0.95,
            $"thumb hidden after pointer leaves: {f.Thumb.Opacity} (needle must stay visible)");
    }

    [AvaloniaFact]
    public async Task Progress_RevealsOnSliderFocus()
    {
        using var f = Fixture.Build();
        await Settle();

        Assert.True(f.Slider.Focus(), "ProgressSlider not focusable");
        Dispatcher.UIThread.RunJobs();
        await Settle();

        // 键盘可达性：Tab 到滑条即显影滑条（时间文本为兄弟节点不跟焦，不在此断言；
        // 调谐针常显，拇指不透明度断言退化为恒真保底）
        Assert.True(f.Slider.Opacity > 0.95, $"slider not revealed on focus: {f.Slider.Opacity}");
        Assert.True(f.Thumb.Opacity > 0.95, $"thumb not revealed on focus: {f.Thumb.Opacity}");
    }

    [AvaloniaFact]
    public async Task Progress_PatchFollowsSliderPointerOver_AndRetreats()
    {
        using var f = Fixture.Build();
        await Settle();

        // 补线颜色跟随滑条自身 :pointerover（与两侧未播段同一触发源）：悬停滑条本体
        // 时两者同步换 PointerOver 色——若 Fluent ControlTheme 的拇指白系压过
        // UserControl 样式，补线会偏离未播段色而红
        var sliderPoint = f.Slider.TranslatePoint(
            new Point(f.Slider.Bounds.Width / 2, f.Slider.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(sliderPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        // 前提锚定：相对色断言（补线==未播段）在"没悬停上→双双停基础色"时会恒绿
        // 退化，先断言 :pointerover 真已激活再比色
        Assert.True(f.Slider.IsPointerOver,
            "slider not pointer-over: color assertions below would degrade to rest-vs-rest");
        PlayerDeckVolumeSliderRevealTests.AssertPatchSeamless("progress-hover", f.Slider, f.Thumb);

        // 指针离开 → 双双回落基础色且保持一致
        f.Window.MouseMove(new Point(40, 40));
        Dispatcher.UIThread.RunJobs();
        await Settle();
        PlayerDeckVolumeSliderRevealTests.AssertPatchSeamless("progress-rest", f.Slider, f.Thumb);
    }

    [AvaloniaFact]
    public async Task Progress_PatchFollowsSliderPressed()
    {
        using var f = Fixture.Build();
        await Settle();

        // 拖动（按下）态：补线须与两侧未播段同步换 Pressed 色——Slider:pressed 是
        // 三条状态拇指样式中唯一无锚的一条，删它则拖动中补线视觉脱节无人拦截。
        // IsMoveToFitEnabled 默认关，点按轨道不 seek（StartSeek/EndSeek 落 mock 吸收）。
        var sliderPoint = f.Slider.TranslatePoint(
            new Point(f.Slider.Bounds.Width / 2, f.Slider.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(sliderPoint);
        Dispatcher.UIThread.RunJobs();
        f.Window.MouseDown(sliderPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        // 前提锚定（同 pointerover 用例）：SliderTrackFillPressed 与基础 SliderTrackFill
        // 同值（BaseMediumLow），若 :pressed 未激活则双侧同停 PointerOver 色而恒绿退化
        Assert.True(f.Slider.Classes.Contains(":pressed"),
            "slider not pressed: color assertion below would degrade to pointerover-vs-pointerover");
        PlayerDeckVolumeSliderRevealTests.AssertPatchSeamless("progress-pressed", f.Slider, f.Thumb);

        // 收尾：释放按压防拖动捕获残留影响后续用例
        f.Window.MouseUp(sliderPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
