using System.Linq;
using System;
using System.Reactive.Concurrency;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 模型名输入框（AutoCompleteBox）水印链路锚定：11.3.9 控件自带 Watermark 属性，
// Fluent 模板经 TemplateBinding 转发到内嵌 TextBox——历史上"/template/ 样式恢复"
// 路径实证不生效（内嵌 TextBox 收不到值，实机水印空白），此处在真实 Fluent 主题
// 下锚定直设属性路径与生产 SettingsView 的端到端传播。
public class ModelInputWatermarkTests
{
    [AvaloniaFact]
    public void DirectWatermark_PropagatesToInnerTextBox_UnderFluentTheme()
    {
        // 属性存在但 XML 文档缺条目（ref 的 Avalonia.Controls.xml 查不到）——反射锚死，
        // 防未来按文档表查成员再得出"无 Watermark"的误判
        Assert.NotNull(typeof(AutoCompleteBox).GetProperty("Watermark"));

        var window = new Window { Width = 400, Height = 200 };
        window.Resources.MergedDictionaries.Add(new FluentTheme());
        var box = new AutoCompleteBox { Watermark = "直设属性" };
        window.Content = box;
        window.Show();

        var inner = box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()
            ?? throw new InvalidOperationException("inner TextBox not found");

        Assert.Equal("直设属性", inner.Watermark);
    }

    [AvaloniaFact]
    public void ModelInputBox_WatermarkBindsToViewModel_AndFollowsProviderSwitch()
    {
        // 生产 SettingsView 端到端锚定 VM→AutoCompleteBox.Watermark 绑定；隐藏弹层不布局、
        // 模板不实例化（内嵌 TextBox 不存在），控件→内嵌 TextBox 一段由上面 Fluent 探针锚定
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
            var box = view.FindControl<AutoCompleteBox>("ModelInputBox")
                ?? throw new InvalidOperationException("ModelInputBox not found");

            // 默认 openai：示例文案直达绑定末端
            Assert.Contains("gpt-4o-mini", box.Watermark as string ?? string.Empty);

            // 切本地模型：跟随重算，且提示下拉手势
            vm.SettingsVM.SelectedProvider = "local";
            Assert.Contains("本机模型", box.Watermark as string ?? string.Empty);
            Assert.Equal(vm.SettingsVM.ModelWatermark, box.Watermark);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
