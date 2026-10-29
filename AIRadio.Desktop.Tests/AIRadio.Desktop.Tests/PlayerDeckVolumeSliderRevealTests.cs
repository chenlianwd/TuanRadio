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
// 实心圆点在发丝线轨道上突兀）：两滑条拇指整替模板为「20×20 透明根（保命中区，
// Fluent 命中区=Thumb 控件尺寸，由 Slider 模板像素行决定）+ 居中 4×14 圆角竖条
// （AccentGreen，与滑条填充同 token；左上三连徽标是 C_FF56F5C4，Dark 与三个调色
// 板下与 AccentGreen 同值、薄荷绿 Light 档微差）」；音量滑条静止态收敛发丝线、
// 拇指隐去，悬停音量区（VolumeArea:pointerover，仅 VOL/滑条/百分比、资料库按钮
// 在簇外；面板透明底使文本与间隙也可命中防回落闪烁）或滑条获焦（拖动指针捕获期
// :pointerover 丢失的兜底）显影完整形态，音量数值由百分比文本常显兜底。
// 双向变异锚定：删 VolumeArea 显影样式 → hover 用例红；删/改静止态样式 → rest
// 用例红；显影触发回退成 DeckRoot（整 deck 点亮）→ 局部化用例红；拇指模板样式
// 丢失（Fluent 圆点复辟）→ 形态用例红；触发簇扩到资料库按钮 → 按钮负向用例红；
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

            // 模板根透明铺满（非空背景才可命中，同频谱命中层结论）+ 居中竖条
            var borders = thumb.GetVisualDescendants().OfType<Border>().ToList();
            Assert.Equal(2, borders.Count);
            var (root, bar) = (borders[0], borders[1]);
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
        }
    }

    [AvaloniaFact]
    public async Task Volume_QuietAtRest_PercentTextStaysVisible()
    {
        using var f = Fixture.Build();
        await Settle();

        // 静止态：滑条降不透明度（发丝线感）、拇指隐去；音量数值文本常显兜底不跟显影
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"rest opacity {f.VolumeSlider.Opacity} expected dimmed hairline (style removed?)");
        Assert.True(f.VolumeThumb.Opacity < 0.05,
            $"volume thumb visible at rest: {f.VolumeThumb.Opacity}");
        Assert.True(f.VolumePercentText.Opacity > 0.95,
            $"percent text must stay visible: {f.VolumePercentText.Opacity}");
    }

    [AvaloniaFact]
    public async Task Volume_RevealsOnVolumeAreaHover_Only_AndRetreats()
    {
        using var f = Fixture.Build();
        await Settle();

        // 悬停 deck 底部进度行（发丝线区域）：进度条随 DeckRoot:pointerover 显影，
        // 但音量滑条保持发丝线——显影触发是 VolumeArea 局部化的，不随整 deck 点亮
        var deckPoint = f.Deck.TranslatePoint(
            new Point(f.Deck.Bounds.Width / 2, f.Deck.Bounds.Height - 12), f.Window)!.Value;
        f.Window.MouseMove(deckPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.ProgressThumb.Opacity > 0.95, "progress not revealed on deck hover");
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"volume slider revealed by deck hover: {f.VolumeSlider.Opacity} " +
            "(slider reveal trigger no longer localized to VolumeArea?)");
        Assert.True(f.VolumeThumb.Opacity < 0.05,
            $"volume thumb revealed by deck hover: {f.VolumeThumb.Opacity} " +
            "(reveal trigger no longer localized to VolumeArea?)");

        // 悬停资料库按钮（与音量区同簇但显影触发簇外）→ 音量滑条仍保持发丝线
        var libraryPoint = f.LibraryButton.TranslatePoint(
            new Point(f.LibraryButton.Bounds.Width / 2, f.LibraryButton.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(libraryPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"volume slider revealed by library button hover: {f.VolumeSlider.Opacity} " +
            "(VolumeArea cluster swallowed the library button?)");
        Assert.True(f.VolumeThumb.Opacity < 0.05,
            $"volume thumb revealed by library button hover: {f.VolumeThumb.Opacity} " +
            "(VolumeArea cluster swallowed the library button?)");

        // 悬停音量滑条本体（VolumeArea 内）→ 显影完整形态
        var volumePoint = f.VolumeSlider.TranslatePoint(
            new Point(f.VolumeSlider.Bounds.Width / 2, f.VolumeSlider.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(volumePoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity > 0.95, $"volume not revealed: {f.VolumeSlider.Opacity}");
        Assert.True(f.VolumeThumb.Opacity > 0.95, $"volume thumb not revealed: {f.VolumeThumb.Opacity}");

        // 悬停划到 VOL 文本（无背景不自身命中，靠 VolumeArea 透明底接住）→ 保持显影
        var volPoint = f.VolLabel.TranslatePoint(
            new Point(f.VolLabel.Bounds.Width / 2, f.VolLabel.Bounds.Height / 2), f.Window)!.Value;
        f.Window.MouseMove(volPoint);
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeThumb.Opacity > 0.95,
            $"volume thumb retreated over VOL label: {f.VolumeThumb.Opacity} " +
            "(VolumeArea transparent hit layer missing?)");

        // 指针离开（落在标题栏上方空白）→ 回落发丝线
        f.Window.MouseMove(new Point(40, 40));
        Dispatcher.UIThread.RunJobs();
        await Settle();
        Assert.True(f.VolumeSlider.Opacity is > 0.05 and < 0.95,
            $"opacity after pointer leaves: {f.VolumeSlider.Opacity} expected back to hairline");
        Assert.True(f.VolumeThumb.Opacity < 0.05,
            $"volume thumb still visible after pointer leaves: {f.VolumeThumb.Opacity}");
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
