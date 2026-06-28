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
            ApplyChatMessageTheme(isDark);
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
            ApplyChatMessageTheme(isDark);
        }
    }

    private void ApplyChatMessageTheme(bool isDark)
    {
        var bubbleBrush = new SolidColorBrush(Color.Parse(isDark ? "#EE15151E" : "#FFF0ECF7"));
        var bubbleBorderBrush = new SolidColorBrush(Color.Parse(isDark ? "#223C3C48" : "#FFD9D1E8"));
        var messageBrush = new SolidColorBrush(Color.Parse(isDark ? "#FFF0EEF8" : "#FF474255"));
        var senderBrush = new SolidColorBrush(Color.Parse(isDark ? "#FFA783FF" : "#FF7A68A4"));

        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            if (Math.Abs(border.MaxWidth - ChatBubbleMaxWidth) > 0.1 || border.Child is not TextBlock message)
                continue;

            border.Background = bubbleBrush;
            border.BorderBrush = bubbleBorderBrush;
            border.BorderThickness = new Thickness(1);
            message.Foreground = messageBrush;

            if (border.GetVisualParent() is StackPanel panel)
            {
                foreach (var sender in panel.Children.OfType<TextBlock>())
                    sender.Foreground = senderBrush;
            }
        }
    }

    private void SetShellTextForeground(string color)
    {
        if (this.FindControl<Border>("ShellCard") is not Border shell) return;

        var brush = new SolidColorBrush(Color.Parse(color));
        foreach (var text in shell.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.GetVisualAncestors().OfType<Border>().Any(IsDarkMessageBubble))
                continue;
            text.Foreground = brush;
        }
    }

    private static bool IsDarkMessageBubble(Border border)
        => border.Child is TextBlock && Math.Abs(border.MaxWidth - ChatBubbleMaxWidth) <= 0.1;

    private void FillDotFields()
    {
        var line = string.Join("  ", Enumerable.Repeat(".", 74));
        var field = string.Join(Environment.NewLine, Enumerable.Repeat(line, 20));
        if (this.FindControl<TextBlock>("ClockDots") is { } clockDots)
            clockDots.Text = field;
        if (this.FindControl<TextBlock>("RoomDots") is { } roomDots)
            roomDots.Text = field;
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
