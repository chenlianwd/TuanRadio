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
        Resources["ConvModeBgConverter"] = new ConversationModeBackgroundConverter();
        Resources["ConvModeIconConverter"] = new ConversationModeIconConverter();
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
            vm.SendMessageCommand.Execute(Unit.Default);
        }
    }
}

public class MessageRoleToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBrush = new(Color.Parse("#2196F3"));
    private static readonly SolidColorBrush AssistantBrush = new(Color.Parse("#424242"));
    private static readonly SolidColorBrush DefaultBrush = new(Color.Parse("#616161"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is MessageRole role ? role switch
        {
            MessageRole.User => UserBrush,
            MessageRole.Assistant => AssistantBrush,
            _ => DefaultBrush
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
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#FFE53935"));
    private static readonly SolidColorBrush InactiveBrush = new(Color.Parse("#FF424242"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? ActiveBrush : InactiveBrush;
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

public class ConversationModeBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new(Color.Parse("#FF1ED760"));
    private static readonly SolidColorBrush InactiveBrush = new(Color.Parse("#FF424242"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? ActiveBrush : InactiveBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

public class ConversationModeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "■" : "🗣";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
