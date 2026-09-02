using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AIRadio.Desktop.Views;

/// <summary>聊天区（消息列表 + 状态/麦克风浮层 + 输入栏）。自管 RoomDots/scroll/mic（spec §5.5）。</summary>
public partial class ChatArea : UserControl
{
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
        FillRoomDots();
        DataContextChanged += OnDataContextChanged;
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
