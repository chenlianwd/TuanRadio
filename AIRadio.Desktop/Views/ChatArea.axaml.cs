using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>聊天区（消息列表 + 状态/麦克风浮层 + 输入栏）。自管 RoomDots/scroll/mic（spec §5.5）。</summary>
public partial class ChatArea : UserControl
{
    internal enum ChatMenuAction { CopySelection, CopyMessage, CopyConversation }

    internal sealed record ChatMenuEntry(string Header, ChatMenuAction Action);

    private Button? _micButton;
    private NotifyCollectionChangedEventHandler? _messagesHandler;
    private PropertyChangedEventHandler? _statusRowHandler;
    private MainWindowViewModel? _currentVm;

    public ChatArea()
    {
        InitializeComponent();
        // Button 的类处理器会把左键按下/释放标记为 Handled，XAML 属性挂载默认跳过已 handled 事件，
        // 必须以 handledEventsToo:true 订阅，否则按住说话的按下/释放永远收不到
        MicButton.AddHandler(InputElement.PointerPressedEvent, OnMicPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        MicButton.AddHandler(InputElement.PointerReleasedEvent, OnMicPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        ChatScrollViewer.AddHandler(Control.ContextRequestedEvent, OnChatContextRequested, RoutingStrategies.Bubble, handledEventsToo: true);
        FillRoomDots();
        DataContextChanged += OnDataContextChanged;
    }

    internal static IReadOnlyList<ChatMenuEntry> BuildContextMenuEntries(
        ChatMessage? targetMessage,
        string? selectedText,
        int totalMessages)
    {
        var entries = new List<ChatMenuEntry>(3);
        if (!string.IsNullOrEmpty(selectedText))
            entries.Add(new(AppLanguage.T("复制选中内容", "Copy selection"), ChatMenuAction.CopySelection));
        if (targetMessage != null && !string.IsNullOrWhiteSpace(targetMessage.Content))
            entries.Add(new(AppLanguage.T("复制消息", "Copy message"), ChatMenuAction.CopyMessage));
        if (totalMessages > 0)
            entries.Add(new(AppLanguage.T("复制全部对话", "Copy conversation"), ChatMenuAction.CopyConversation));
        return entries;
    }

    private void OnChatContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Source is not Visual source) return;

        var selectable = source as SelectableTextBlock ?? source.FindAncestorOfType<SelectableTextBlock>();
        var selectedText = selectable?.SelectedText;
        var targetMessage = FindChatMessageFromVisual(source);
        var entries = BuildContextMenuEntries(targetMessage, selectedText, vm.ChatVM.Messages.Count);
        if (entries.Count == 0) return;

        var menu = new MenuFlyout();
        foreach (var entry in entries)
        {
            var captured = entry;
            var menuItem = new MenuItem { Header = captured.Header };
            menuItem.Click += (_, _) => InvokeChatMenuAction(captured.Action, targetMessage, selectedText, vm);
            menu.Items.Add(menuItem);
        }

        e.Handled = true;
        var anchor = (Control?)selectable ?? ChatScrollViewer;
        menu.ShowAt(anchor, showAtPointer: true);
    }

    private static ChatMessage? FindChatMessageFromVisual(Visual? visual)
    {
        for (var current = visual; current != null; current = current.GetVisualParent())
        {
            if (current is StyledElement { DataContext: ChatMessage msg })
                return msg;
        }
        return null;
    }

    private async void InvokeChatMenuAction(
        ChatMenuAction action,
        ChatMessage? targetMessage,
        string? selectedText,
        MainWindowViewModel vm)
    {
        var text = action switch
        {
            ChatMenuAction.CopySelection => selectedText ?? string.Empty,
            ChatMenuAction.CopyMessage => targetMessage?.Content ?? string.Empty,
            ChatMenuAction.CopyConversation => ChatViewModel.FormatConversation(vm.ChatVM.Messages),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Copy chat text to clipboard failed");
        }
    }

    private void FillRoomDots()
    {
        var line = string.Join("  ", Enumerable.Repeat(".", 74));
        var field = string.Join(Environment.NewLine, Enumerable.Repeat(line, 20));
        if (RoomDots is { } dots) dots.Text = field;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm != null && _messagesHandler != null)
            _currentVm.ChatVM.Messages.CollectionChanged -= _messagesHandler;
        if (_currentVm != null && _statusRowHandler != null)
            _currentVm.ChatVM.PropertyChanged -= _statusRowHandler;

        if (DataContext is MainWindowViewModel vm)
        {
            _currentVm = vm;
            _messagesHandler = (_, _) => Dispatcher.UIThread.Post(
                () => ChatScrollViewer?.ScrollToEnd(), DispatcherPriority.Background);
            vm.ChatVM.Messages.CollectionChanged += _messagesHandler;
            // 状态提示条显隐会挤压消息视口，需重新贴底，避免最新消息被推出可视区
            _statusRowHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(ChatViewModel.ShowStatusNotice) ||
                    e.PropertyName == nameof(ChatViewModel.ShowStatusRecall))
                    Dispatcher.UIThread.Post(
                        () => ChatScrollViewer?.ScrollToEnd(), DispatcherPriority.Background);
            };
            vm.ChatVM.PropertyChanged += _statusRowHandler;
        }
        else
        {
            _currentVm = null;
        }
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
            vm.ChatVM.SendMessageCommand.Execute(Unit.Default).Subscribe();
    }

    private void OnMicPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _micButton = sender as Button;
            // 仅在真正开始录音时进入按压视觉：AI 回复中/识别中被拒时按钮不变色、不捕获，
            // 让用户立即看出这次按住没有生效，而不是按下后石沉大海
            if (vm.ChatVM.BeginHoldToTalk())
            {
                if (_micButton != null)
                {
                    _micButton.Background = ResolveBrush(_micButton, "C_FF56F5C4");
                    _micButton.Foreground = ResolveBrush(_micButton, "C_FF050507");
                }
                (sender as Control)?.Focus();
                e.Pointer.Capture(sender as IInputElement);
            }
            e.Handled = true;
        }
    }

    private void OnMicPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            ResetMicButton();
            vm.ChatVM.EndHoldToTalk();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnMicPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetMicButton();
        if (DataContext is MainWindowViewModel vm)
            vm.ChatVM.EndHoldToTalk();
    }

    private void ResetMicButton()
    {
        if (_micButton == null) return;
        _micButton.Background = ResolveBrush(_micButton, "C_33262835");
        _micButton.Foreground = ResolveBrush(_micButton, "C_FFEDEDF5");
    }

    // 主题资源是 Color 而非 IBrush，直接强转会抛 InvalidCastException
    private static IBrush? ResolveBrush(Control control, string key)
        => control.FindResource(key) switch
        {
            Color c => new SolidColorBrush(c),
            IBrush b => b,
            _ => null
        };
}
