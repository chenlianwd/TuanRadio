using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>
/// 点击非输入区域时释放输入框键盘焦点。Avalonia 点击不可聚焦元素（背景 Border/Grid 等装饰）
/// 不会转移焦点，聊天/设置输入框的光标会一直驻留并继续吞掉后续键入；这里在窗口级补上
/// “点空白 = 退出输入态”的常规桌面预期。
/// </summary>
internal static class InputFocusRelease
{
    public static void Attach(TopLevel window)
        => window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TopLevel { FocusManager: { } manager } || e.Source is not Visual source)
            return;
        if (ShouldRelease(source, manager.GetFocusedElement()))
            manager.ClearFocus();
    }

    // 仅当焦点在输入框上、且点击落在纯装饰区域（既不在任何 TextBox 内，也未经过可聚焦控件）时释放；
    // 点击按钮/滑杆等可聚焦控件交给原生焦点逻辑，不与既有交互打架
    internal static bool ShouldRelease(Visual clickSource, IInputElement? focused)
    {
        if (FindAncestorTextBox(focused as Visual) is null)
            return false;

        for (Visual? v = clickSource; v?.GetVisualParent() is { } parent; v = parent)
        {
            if (v is TextBox or InputElement { Focusable: true })
                return false;
        }
        return true;
    }

    private static TextBox? FindAncestorTextBox(Visual? visual)
    {
        for (Visual? v = visual; v != null; v = v.GetVisualParent())
        {
            if (v is TextBox box)
                return box;
        }
        return null;
    }
}
