using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>指针命中守卫：拖动/双击处理中判断事件源是否落在按钮上，避免按钮点击误触发窗口级手势。</summary>
internal static class InteractionGuards
{
    internal static bool IsOverButton(object? source)
    {
        var visual = source as Visual;
        while (visual != null)
        {
            if (visual is Button)
                return true;

            visual = visual.GetVisualParent();
        }

        return false;
    }
}
