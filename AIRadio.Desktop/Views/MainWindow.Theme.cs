using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

// Theme management extracted from MainWindow.axaml.cs to reduce file size
public partial class MainWindow
{
    private void UpdateThemeButtons(bool isDark)
    {
        _themeButton ??= this.FindControl<Button>("ThemeButton");

        if (isDark)
        {
            if (_themeButton != null) { _themeButton.Background = new SolidColorBrush(Color.Parse("#FF171722")); _themeButton.Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")); }
            Background = new SolidColorBrush(Color.Parse("#FF08080B"));
            SetThemeColors(
                "#FF030305", "#FF050507", "#F0131320", "#CC1B1B2A", "#F008080D",
                "#E91B1B2A", "#F007070A", "#E9161623", "#66111113", "#F0222234",
                "#33494B66");
            SetShellTextForeground("#FFEDEDF5");
        }
        else
        {
            if (_themeButton != null) { _themeButton.Background = new SolidColorBrush(Color.Parse("#FFFFFFFF")); _themeButton.Foreground = new SolidColorBrush(Color.Parse("#FF111118")); }
            Background = new SolidColorBrush(Color.Parse("#FFF5F1FF"));
            SetThemeColors(
                "#FFF5F1FF", "#FFE9E2F7", "#F8FFFFFF", "#EDEBE5FF", "#F7F8F6FF",
                "#ECEEE9FF", "#F4F3F8FF", "#F8FFFFFF", "#99EEEAF5", "#EAE7F3FF",
                "#664E4862");
            SetShellTextForeground("#FF17171F");
        }
    }


    // Note: walks the visual tree on every theme switch. If performance becomes an issue,
    // tag elements that need theming with an attached property and use Name-based lookups.
    private void SetShellTextForeground(string color)
    {
        if (this.FindControl<Border>("ShellCard") is not Border shell) return;

        var brush = new SolidColorBrush(Color.Parse(color));
        foreach (var text in shell.GetVisualDescendants().OfType<TextBlock>())
        {
            // 跳过自管主题的子控件（UserControl 用 DynamicResource/硬编码自行配色）
            if (text.GetVisualAncestors().OfType<Avalonia.Controls.UserControl>().Any())
                continue;
            text.Foreground = brush;
        }
    }


    private void SetThemeColors(
        string root, string title, string shell, string header, string clock,
        string deck, string queue, string room, string live, string footer, string border)
    {
        SetBackground("RootGrid", root);
        SetBackground("TitleBar", title);
        SetBackground("ShellCard", shell);
        SetBorderBrush("ShellCard", border);
        SetBackground("BrandHeader", header);
        SetBackground("ClockStage", clock);
        SetBackground("PlayerDeck", deck);
        SetBackground("QueueStrip", queue);
        SetBackground("RadioRoom", room);
        SetBackground("LiveStrip", live);
        SetBackground("InputDeck", deck);
        SetBackground("FooterBar", footer);
    }

    private void SetBackground(string name, string color)
    {
        var brush = new SolidColorBrush(Color.Parse(color));
        if (this.FindControl<Control>(name) is Border border)
            border.Background = brush;
        else if (this.FindControl<Control>(name) is Panel panel)
            panel.Background = brush;
    }

    private void SetBorderBrush(string name, string color)
    {
        if (this.FindControl<Border>(name) is Border border)
            border.BorderBrush = new SolidColorBrush(Color.Parse(color));
    }
}
