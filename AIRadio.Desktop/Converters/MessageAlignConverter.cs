using System;
using System.Globalization;
using AIRadio.Desktop.Models;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AIRadio.Desktop.Converters;

/// <summary>消息角色→水平对齐：User 右，其余左。合并自 MainWindow.MessageAlignConverter 与 ChatView.MessageRoleToAlignmentConverter。</summary>
public class MessageAlignConverter : IValueConverter
{
    public static readonly MessageAlignConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MessageRole role)
            return role == MessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        return HorizontalAlignment.Left;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
