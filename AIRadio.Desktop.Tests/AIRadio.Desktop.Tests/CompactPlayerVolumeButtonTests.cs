using System;
using System.Reactive.Concurrency;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 回归锚定（2026-10-19 用户反馈：音量按钮点击后整块变绿看不清字形、未点击时反而像 hover）：
/// 根因——Avalonia 类型选择器只匹配精确类型（headless 实证 Button 样式对 ToggleButton 不生效），
/// MainWindow 的 Button 样式族覆盖不到它，Fluent 默认未选中态=半透明白浅底（视觉近似 hover）、
/// 选中态=实心 Accent 填充（ToggleButtonBackgroundChecked=SystemControlHighlightAccentBrush，
/// 图标同为 AccentGreen → 绿底绿字）。修复后：基础/hover 与 Button 家族同款 token，
/// checked（音量弹层展开）用半透明强调色。
/// headless 无 FluentTheme：装配仅含 ContentPresenter#PART_ContentPresenter 的最小模板
/// （结构与 Fluent 一致，不带 Background 绑定——C# Bind 是 LocalValue 优先级，
/// 会压过条件样式触发器，测不出模板级样式效果），使模板级样式真实落点。
/// </summary>
public class CompactPlayerVolumeButtonTests
{
    [AvaloniaFact]
    public void VolumeButton_FourStates_FollowButtonFamilyNotFluentAccent()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            var view = new CompactPlayer { DataContext = vm };
            var button = view.FindNameScope()?.Find<ToggleButton>("CompactVolumeButton")
                ?? throw new InvalidOperationException("CompactVolumeButton not found in CompactPlayer");
            button.Theme = CreateMinimalToggleTheme();
            // 裸窗口无 Window 模板 → 无 OverlayLayer，Popup.Open 会抛异常；
            // 弹层开启联动是既有行为（非本次回归范围），解除绑定后 checked 态仍由
            // ToggleButton 伪类真实驱动
            var popup = (button.Parent as Panel)?.Children.OfType<Popup>().FirstOrDefault()
                ?? throw new InvalidOperationException("volume Popup not found next to button");
            // Bind 同属性会替换并释放 XAML 装入的 IsOpen 绑定（ClearValue 压不住已编译绑定的回推）
            popup.Bind(Popup.IsOpenProperty, System.Reactive.Linq.Observable.Return(false));
            window = new Window
            {
                Content = view,
                Width = 560,
                Height = 140,
                RequestedThemeVariant = ThemeVariant.Dark,
            };
            window.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://AIRadio/Themes/Colors.axaml")));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var presenter = button.FindDescendantOfType<ContentPresenter>()
                ?? throw new InvalidOperationException("PART_ContentPresenter not realized");

            // 基础态：Button 家族同款底色（控件级样式，不再吃 Fluent 半透明白浅底）
            AssertColor("#11121620", button.Background);

            var center = button.TranslatePoint(
                new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            Dispatcher.UIThread.RunJobs();

            // hover：家族同款 hover 底色
            AssertColor("#24212632", presenter.Background);

            // 点击 → checked（音量弹层展开）：半透明强调色，而非 Fluent 实心 Accent #FF56F5C4
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(":checked", button.Classes);
            AssertColor("#6656F5C4", presenter.Background); // checked + 指针仍在按钮上

            // 指针移开：checked 态仍保持半透明强调色
            window.MouseMove(new Point(10, 10));
            Dispatcher.UIThread.RunJobs();
            AssertColor("#3356F5C4", presenter.Background);

            // 再点 → 取消选中，回归普通按钮形态（hover→移开回基础底色）
            window.MouseMove(center);
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(":checked", button.Classes);
            AssertColor("#24212632", presenter.Background); // 未选中 + 指针悬停
            window.MouseMove(new Point(10, 10));
            Dispatcher.UIThread.RunJobs();
            AssertColor("#11121620", presenter.Background);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private static ControlTheme CreateMinimalToggleTheme()
    {
        var theme = new ControlTheme(typeof(ToggleButton));
        // ControlTheme 的模板经 Setter 注入（Template 属性不在 ControlTheme 上）。
        // ContentPresenter 背景走 TemplateBinding（Template 优先级）：非空才可命中
        // （同频谱命中层结论）、基态回落控件 Background、条件样式（StyleTrigger）可覆盖。
        theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty,
            new FuncControlTemplate<ToggleButton>((control, _) =>
            {
                var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
                // Template 优先级绑定控件 Background（TemplateBinding 同语义）：
                // 基态回落控件底色、条件样式（StyleTrigger）可覆盖、解除后回落
                presenter.Bind(ContentPresenter.BackgroundProperty,
                    control.GetObservable(TemplatedControl.BackgroundProperty),
                    BindingPriority.Template);
                return presenter;
            })));
        return theme;
    }

    private static void AssertColor(string expected, IBrush? brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.Equal(Color.Parse(expected), solid.Color);
    }
}
