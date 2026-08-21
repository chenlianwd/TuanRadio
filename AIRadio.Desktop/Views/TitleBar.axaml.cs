using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>窗口标题栏。窗口 chrome（拖拽/双击最大化/min/close）通过 (Window)VisualRoot 访问宿主。</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 标题栏内的按钮点击会冒泡到这里，拖动只应在非按钮区域启动
        if (InteractionGuards.IsOverButton(e.Source))
            return;

        if (VisualRoot is Window w && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { w.BeginMoveDrag(e); }
            catch { /* 平台在部分状态下可能拒绝拖动 */ }
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (InteractionGuards.IsOverButton(e.Source))
            return;

        if (VisualRoot is Window w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window w)
            w.WindowState = WindowState.Minimized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is Window w)
            w.Close();
    }
}
