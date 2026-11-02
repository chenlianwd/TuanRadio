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
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 竖条调谐针拇指 + 音量滑条同位显影锚定（2026-10-29 用户反馈 Fluent 默认 20×20
// 实心圆点在发丝线轨道上突兀；2026-11-02 用户反馈静止态发丝线中段像"缺口"且
// 太大——Fluent Slider 轨道是「已播|拇指|未播」三段拼的，拇指占位 20px 下没有
// 轨道，隐藏拇指即裸露断口）：两滑条拇指整替模板为「20×20 透明根（保命中区，
// Fluent 命中区=Thumb 控件尺寸，由 Slider 模板像素行决定）+ 轨道补线（填平拇指
// 占位断口，高度绑 SliderTrackThemeHeight、颜色经 TemplateBinding 走
// Thumb.Foreground，由 UserControl 样式接管为 TrackFill 系并随 pointerover/
// pressed 与两侧未播段同步——通道必须走 Foreground：Thumb.Background 被 Slider
// 模板内显式 {DynamicResource SliderThumbBackground}（Fluent 出厂 accent 蓝，测
// 试夹具下即蓝）占用，且该模板内特性赋值压不过 UserControl 样式 Setter（2026-
// 11-02 实测，勿按"样式更近必压过"复活 Background 通道）；Foreground 全状态零
// 占用者，样式恒压过继承值）+ 居中 4×14 圆角竖条（AccentGreen，与
// 滑条填充同 token；左上三连徽标是 C_FF56F5C4，Dark 与三个调色板下与
// AccentGreen 同值、薄荷绿 Light 档微差）」，调谐针常显（复古收音机刻度指针
// 语义，不再参与显隐）；音量滑条静止态收敛发丝线，悬停音量区（VolumeArea:
// pointerover，仅 VOL/滑条/百分比、资料库按钮在簇外；面板透明底使文本与间隙
// 也可命中防回落闪烁）或滑条获焦（拖动指针捕获期 :pointerover 丢失的兜底）
// 显影，音量数值由百分比文本常显兜底。
// 双向变异锚定：删 VolumeArea 显影样式 → hover 用例红；删/改静止态样式 → rest
// 用例红；显影触发回退成 DeckRoot（整 deck 点亮）→ 局部化用例红；拇指模板样式
// 丢失（Fluent 圆点复辟）→ 形态用例红；去补线（断口复辟）/补线颜色跟随丢失
// （Fluent 拇指白系压过）→ 无缝用例红；触发簇扩到资料库按钮 → 按钮负向用例红；
// 面板透明底丢失 → 文本悬停保持用例红。FluentTheme 必须先于 MainWindow 构造挂载
// （同 PlayerDeckProgressHoverRevealTests）；Colors.axaml 挂 window.Resources 并
// 锁 Dark 变体供 AccentGreen 解析（同 CompactPlayerVolumeButtonTests）。
public class PlayerDeckVolumeSliderRevealTests
{
    private sealed class Fixture : IDisposable
    {
        public MainWindowViewModel Vm = null!;
        public Window Window = null!;
        public FluentTheme Theme = null!;
        public PlayerDeck Deck = null!;
        public Slider VolumeSlider = null!;
        public Thumb VolumeThumb = null!;
        public Slider ProgressSlider = null!;
        public Thumb ProgressThumb = null!;
        public TextBlock VolumePercentText = null!;
        public Button LibraryButton = null!;
        public TextBlock VolLabel = null!;
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
                f.Window = new MainWindow { DataContext = f.Vm, Width = 943, Height = 893,
                    RequestedThemeVariant = ThemeVariant.Dark };
                f.Window.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                    new Uri("avares://AIRadio/Themes/Colors.axaml")));
                f.Window.Show();
                // headless 下布局要泵一次 dispatcher 才执行（不泵时 Bounds 为 0）
                Dispatcher.UIThread.RunJobs();
                f.Deck = f.Window.FindDescendantOfType<PlayerDeck>()
                    ?? throw new InvalidOperationException("PlayerDeck not found");
                f.VolumeSlider = f.Deck.FindControl<Slider>("VolumeSlider")
                    ?? throw new InvalidOperationException("VolumeSlider not found");
                f.VolumeThumb = f.VolumeSlider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()
                    ?? throw new InvalidOperationException("volume thumb not found");
                f.ProgressSlider = f.Deck.FindControl<Slider>("ProgressSlider")
                    ?? throw new InvalidOperationException("ProgressSlider not found");
                f.ProgressThumb = f.ProgressSlider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()
                    ?? throw new InvalidOperationException("progress thumb not found");
                f.VolumePercentText = f.Deck.FindControl<TextBlock>("VolumePercentText")
                    ?? throw new InvalidOperationException("VolumePercentText not found");
                f.LibraryButton = f.Deck.FindControl<Button>("LibraryButton")
                    ?? throw new InvalidOperationException("LibraryButton not found");
                f.VolLabel = (f.VolumeSlider.Parent as StackPanel)?.Children
                    .OfType<TextBlock>().FirstOrDefault(t => t.Text == "VOL")
                    ?? throw new InvalidOperationException("VOL label not found in VolumeArea");
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

    // 拇指模板声明序 Border：[0]=透明根、[1]=轨道补线、[2]=调谐针（供两测试文件共用）
    internal static Border GetThumbPatch(Thumb thumb) =>
        thumb.GetVisualDescendants().OfType<Border>().ToList() is { Count: 3 } borders
            ? borders[1]
            : throw new InvalidOperationException(
                $"thumb template expected 3 borders (root/patch/needle), got " +
                $"{thumb.GetVisualDescendants().OfType<Border>().Count()} (patch removed?)");

    // 未播段（IncreaseButton）模板内的 TrackBackground：补线无缝断言的对照物
    internal static Border GetRestTrack(Slider slider) =>
        slider.GetVisualDescendants().OfType<RepeatButton>()
            .FirstOrDefault(r => r.Name == "PART_IncreaseButton") is { } inc
        ? inc.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "TrackBackground")
            ?? throw new InvalidOperationException("TrackBackground not found in IncreaseButton")
        : throw new InvalidOperationException("PART_IncreaseButton not found");

    // 补线与两侧未播段「无缝」：同高、同色、垂直中心对齐、横向铺满拇指占位——
    // 去补线（断口复辟）或补线颜色跟随丢失（Fluent 拇指白系压过 UserControl 样式）均红
    internal static void AssertPatchSeamless(string sliderName, Slider slider, Thumb thumb)
    {
        var patch = GetThumbPatch(thumb);
        var rest = GetRestTrack(slider);

        Assert.True(patch.Bounds.Width >= thumb.Bounds.Width - 0.5,
            $"{sliderName}: patch width {patch.Bounds.Width:F1} must span the thumb " +
            $"{thumb.Bounds.Width:F1} (gap not bridged?)");
        Assert.True(Math.Abs(patch.Bounds.Height - rest.Bounds.Height) < 0.5,
            $"{sliderName}: patch height {patch.Bounds.Height:F1} != track {rest.Bounds.Height:F1}");
        Assert.True(Math.Abs(patch.Bounds.Center.Y - rest.Bounds.Center.Y) < 0.5,
            $"{sliderName}: patch center Y {patch.Bounds.Center.Y:F1} misaligned with track " +
            $"{rest.Bounds.Center.Y:F1} (double-line seam)");

        var patchColor = (Assert.IsAssignableFrom<ISolidColorBrush>(patch.Background)).Color;
        var restColor = (Assert.IsAssignableFrom<ISolidColorBrush>(rest.Background)).Color;
        Assert.True(patchColor == restColor,
            $"{sliderName}: patch {patchColor} != rest {restColor} (patch not following track fill?)");
    }

    [AvaloniaFact]
    public async Task BothSliders_UseNeedleThumb_FullHitAreaKept()
    {
        using var f = Fixture.Build();
        await Settle();

        var progressFill = Assert.IsAssignableFrom<ISolidColorBrush>(f.ProgressSlider.Foreground);
        var volumeFill = Assert.IsAssignableFrom<ISolidColorBrush>(f.VolumeSlider.Foreground);
        // 填充统一到 AccentGreen token：调色板切换时两滑条填充/拇指/音量百分比永远同色，
        // Light 档不再与 Fluent Accent(#178A65) 双绿错配（左上徽标是 C_FF56F5C4，薄荷绿 Light 微差）
        Assert.Equal(progressFill.Color, volumeFill.Color);

        foreach (var (slider, thumb) in new[] { (f.ProgressSlider, f.ProgressThumb), (f.VolumeSlider, f.VolumeThumb) })
        {
            // Fluent 命中区度量不因换模板缩水：Thumb 控件仍是 20×20 像素行
            Assert.True(thumb.Bounds.Width is >= 19.5 and <= 20.5,
                $"{thumb.Bounds.Width} expected Fluent 20px hit target (template shrank it?)");
            Assert.True(thumb.Bounds.Height is >= 19.5 and <= 20.5,
                $"{thumb.Bounds.Height} expected Fluent 20px hit target (template shrank it?)");

            // 模板根透明铺满（非空背景才可命中，同频谱命中层结论）+ 补线填断口 + 居中竖条
            var borders = thumb.GetVisualDescendants().OfType<Border>().ToList();
            Assert.Equal(3, borders.Count);
            var (root, patch, bar) = (borders[0], borders[1], borders[2]);
            Assert.NotNull(root.Background);
            Assert.True(root.Bounds.Width >= 19.5 && root.Bounds.Height >= 19.5,
                $"root {root.Bounds.Width}x{root.Bounds.Height} must fill the thumb");
            Assert.Equal(4, bar.Width);
            Assert.Equal(14, bar.Height);
            Assert.Equal(new CornerRadius(2), bar.CornerRadius);
            Assert.True(Math.Abs(bar.Bounds.Center.X - root.Bounds.Center.X) < 0.5,
                "needle bar not centered");
            var barBrush = Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background);
            Assert.Equal(progressFill.Color, barBrush.Color);

            // 常显调谐针：静止态拇指不隐藏（复古收音机刻度指针语义）
            Assert.True(thumb.Opacity > 0.95,
                $"thumb hidden at rest: {thumb.Opacity} (needle must stay visible)");
            // 补线与未播段无缝（静止基础态）
            AssertPatchSeamless(slider.Name ?? "slider", slider, thumb);
        }
    }

    [AvaloniaFact]
    public async Task Volume_QuietAtRest_PercentTextStaysVisible()
    {
        using var f = Fixture.Build();
        await Settle();

        // 静止态：滑条降不透明度（发丝线感）、调谐针常显（补线已无缝，见形态用例）；
        // 音量数值文本常显兜底不跟显影
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"rest opacity {f.VolumeSlider.Opacity} expected dimmed hairline (style removed?)");
        Assert.True(f.VolumeThumb.Opacity > 0.95,
            $"volume thumb hidden at rest: {f.VolumeThumb.Opacity} (needle must stay visible)");
        Assert.True(f.VolumePercentText.Opacity > 0.95,
            $"percent text must stay visible: {f.VolumePercentText.Opacity}");
    }

    [AvaloniaFact]
    public async Task Volume_RevealsOnVolumeAreaHover_Only_AndRetreats()
    {
        using var f = Fixture.Build();
        await Settle();

        // 悬停 deck 底部进度行（发丝线区域）：进度条随 DeckRoot:pointerover 显影，
        // 但音量滑条保持发丝线——显影触发是 VolumeArea 局部化的，不随整 deck 点亮；
        // 调谐针常显后显影主体=滑条不透明度，局部化负向由滑条+补线未跟亮双锚
        var deckPoint = f.Deck.TranslatePoint(
            new Point(f.Deck.Bounds.Width / 2, f.Deck.Bounds.Height - 12), f.Window)!.Value;
        f.Window.MouseMove(deckPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.ProgressThumb.Opacity > 0.95, "progress not revealed on deck hover");
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"volume slider revealed by deck hover: {f.VolumeSlider.Opacity} " +
            "(slider reveal trigger no longer localized to VolumeArea?)");
        AssertPatchSeamless("volume", f.VolumeSlider, f.VolumeThumb);

        // 悬停资料库按钮（与音量区同簇但显影触发簇外）→ 音量滑条仍保持发丝线
        var libraryPoint = f.LibraryButton.TranslatePoint(
            new Point(f.LibraryButton.Bounds.Width / 2, f.LibraryButton.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(libraryPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"volume slider revealed by library button hover: {f.VolumeSlider.Opacity} " +
            "(VolumeArea cluster swallowed the library button?)");
        AssertPatchSeamless("volume", f.VolumeSlider, f.VolumeThumb);

        // 悬停音量滑条本体（VolumeArea 内）→ 显影完整形态；补线随滑条 :pointerover
        // 与两侧未播段同步换 PointerOver 色（状态拇指样式无 Name 限定，若被收窄成
        // ProgressSlider 专用，进度侧用例照绿而音量侧脱节——此处锚定音量侧）
        var volumePoint = f.VolumeSlider.TranslatePoint(
            new Point(f.VolumeSlider.Bounds.Width / 2, f.VolumeSlider.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(volumePoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity > 0.95, $"volume not revealed: {f.VolumeSlider.Opacity}");
        // 调谐针常显后恒真保底（防"恢复静止隐藏但不恢复显影"的半截变异，同 focus 用例）
        Assert.True(f.VolumeThumb.Opacity > 0.95, $"volume thumb not revealed: {f.VolumeThumb.Opacity}");
        AssertPatchSeamless("volume-hover", f.VolumeSlider, f.VolumeThumb);

        // 悬停划到 VOL 文本（无背景不自身命中，靠 VolumeArea 透明底接住）→ 保持显影
        var volPoint = f.VolLabel.TranslatePoint(
            new Point(f.VolLabel.Bounds.Width / 2, f.VolLabel.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(volPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity > 0.95,
            $"volume retreated over VOL label: {f.VolumeSlider.Opacity} " +
            "(VolumeArea transparent hit layer missing?)");

        // 指针离开（落在标题栏上方空白）→ 回落发丝线（调谐针常显不回落）
        f.Window.MouseMove(new Point(40, 40));
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"opacity after pointer leaves: {f.VolumeSlider.Opacity} expected back to hairline");
        Assert.True(f.VolumeThumb.Opacity > 0.95,
            $"volume thumb hidden after pointer leaves: {f.VolumeThumb.Opacity} " +
            "(needle must stay visible)");
    }

    [AvaloniaFact]
    public async Task Volume_RevealsOnSliderFocus()
    {
        using var f = Fixture.Build();
        await Settle();

        Assert.True(f.VolumeSlider.Focus(), "VolumeSlider not focusable");
        Dispatcher.UIThread.RunJobs();
        await Settle();

        // 键盘可达 + 拖动兜底：滑条获焦即显影（拖动中指针捕获丢 :pointerover，靠焦点维持）
        Assert.True(f.VolumeSlider.Opacity > 0.95, $"volume not revealed on focus: {f.VolumeSlider.Opacity}");
        Assert.True(f.VolumeThumb.Opacity > 0.95, $"volume thumb not revealed on focus: {f.VolumeThumb.Opacity}");
    }
}
