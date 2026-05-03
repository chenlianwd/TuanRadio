using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.ViewModels;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive;

namespace AIRadio.Desktop.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        Resources["MessageRoleConverter"] = new MessageRoleToBrushConverter();
        Resources["MessageAlignmentConverter"] = new MessageRoleToAlignmentConverter();
        Resources["MicBgConverter"] = new MicBackgroundConverter();
        Resources["MicIconConverter"] = new MicIconConverter();
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChatViewModel vm)
            {
                vm.Messages.CollectionChanged += (_, _) =>
                {
                    if (MessagesScroller != null)
                    {
                        MessagesScroller.Offset = MessagesScroller.Offset.WithY(MessagesScroller.Extent.Height);
                    }
                };
            }
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChatViewModel vm)
        {
            vm.SendMessageCommand.Execute(Unit.Default).Subscribe();
        }
    }
}

public class MessageRoleToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is MessageRole role ? role switch
        {
            MessageRole.User => new SolidColorBrush(Color.Parse("#2196F3")),
            MessageRole.Assistant => new SolidColorBrush(Color.Parse("#424242")),
            _ => new SolidColorBrush(Color.Parse("#616161"))
        } : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class MessageRoleToAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is MessageRole role ? role switch
        {
            MessageRole.User => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Left
        } : Avalonia.Layout.HorizontalAlignment.Left;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class MicBackgroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(Color.Parse("#FFE53935"))
            : new SolidColorBrush(Color.Parse("#FF424242"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class MicIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "■" : "🎤";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
