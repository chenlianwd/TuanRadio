using System.Reactive.Concurrency;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

// 回归：Avalonia 点击不可聚焦元素不转移键盘焦点，聊天/设置输入框光标会永久驻留并吞掉
// 后续键入。窗口级 InputFocusRelease 在点击纯装饰区域时 ClearFocus；点输入框自身或
// 可聚焦控件（按钮等）不得释放，交给原生焦点逻辑。
public class InputFocusReleaseTests
{
    private static PointerPressedEventArgs Press(Control source) => new(
        source, new Pointer(1, PointerType.Mouse, true), source, new Point(5, 5), 0,
        new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
        KeyModifiers.None, 1);

    [AvaloniaFact]
    public void DecorationClick_ReleasesTextBoxFocus()
    {
        var window = new Window();
        InputFocusRelease.Attach(window);
        var box = new TextBox();
        var decoration = new Border { Width = 100, Height = 40 };
        window.Content = new StackPanel { Children = { box, decoration } };
        window.Show();

        Assert.True(box.Focus());
        decoration.RaiseEvent(Press(decoration));
        Assert.False(box.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void TextBoxClick_KeepsFocus()
    {
        var window = new Window();
        InputFocusRelease.Attach(window);
        var box = new TextBox();
        window.Content = new StackPanel { Children = { box } };
        window.Show();

        Assert.True(box.Focus());
        box.RaiseEvent(Press(box));
        Assert.True(box.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ButtonClick_DoesNotReleaseTextBoxFocus()
    {
        var window = new Window();
        InputFocusRelease.Attach(window);
        var box = new TextBox();
        var button = new Button { Content = "OK" };
        window.Content = new StackPanel { Children = { box, button } };
        window.Show();

        Assert.True(box.Focus());
        button.RaiseEvent(Press(button));
        // Avalonia 原生会把焦点移给被点击的可聚焦控件；处理器不得介入清除（ClearFocus 会把焦点置空）
        Assert.True(button.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void DecorationClick_KeepsNonTextBoxFocus()
    {
        var window = new Window();
        InputFocusRelease.Attach(window);
        var button = new Button { Content = "OK" };
        var decoration = new Border { Width = 100, Height = 40 };
        window.Content = new StackPanel { Children = { button, decoration } };
        window.Show();

        Assert.True(button.Focus());
        decoration.RaiseEvent(Press(decoration));
        Assert.True(button.IsFocused);
        window.Close();
    }

    // 锚定 MainWindow 构造里的 Attach 接线：挂接一旦被移除（麦克风按钮同类接线回归先例），
    // 真实窗口上点装饰区域将不再释放聊天输入框焦点。
    // headless 无主题，SettingsView 的 ToggleSwitch 缺模板部件会在布局时抛异常，
    // 挂真实 VM 让设置/抽屉弹层保持隐藏、只布局常驻树（同 ChatAreaMicButtonTests 手法）。
    [AvaloniaFact]
    public void MainWindowWiring_DecorationClick_ReleasesChatInputFocus()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            var chatBox = window.FindDescendantOfType<ChatArea>()?.FindDescendantOfType<TextBox>()
                ?? throw new InvalidOperationException("chat input TextBox not found");
            var shell = window.FindControl<Border>("ShellCard")
                ?? throw new InvalidOperationException("ShellCard not found");

            Assert.True(chatBox.Focus());
            shell.RaiseEvent(Press(shell));
            Assert.False(chatBox.IsFocused);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
