using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIRadio.Desktop.Views;

/// <summary>窗口标题栏。窗口 chrome（拖拽/min/close）通过 (Window)VisualRoot 访问宿主。</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VisualRoot is Window w && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            w.BeginMoveDrag(e);
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
