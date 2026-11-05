using System;
using System.Linq;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 聊天发送按钮回归：箭头芯片按键（VOICE/NOPE 同族主题感知 token）替换原紫灰圆钮
/// （C_FF7F7896 为游离于配色主题外的硬编码遗留）。图标为箭头——语言无关
/// （2026-11-03 用户裁决：文字版 SEND 须考虑不识英文的用户）。
/// </summary>
public class ChatAreaSendButtonTests
{
    [AvaloniaFact]
    public void SendButton_IsRetroChipWithArrowGlyph()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        var view = new ChatArea { DataContext = vm };
        // 同时注入新旧两个键：断言背景恰为主题感知的芯片底色——若有人把 Background
        // 改回 C_FF7F7896（资源仍可解析）或任何无关色，Equal 即红
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ["C_EE050507"] = Color.FromUInt32(0xEE050507),
            ["C_FF7F7896"] = Color.FromUInt32(0xFF7F7896),
            ["C_FFB9FFE8"] = Color.FromUInt32(0xFFB9FFE8)
        });
        Window? window = null;
        try
        {
            window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var send = view.FindNameScope()?.Find<Button>("SendButton")
                ?? throw new InvalidOperationException("SendButton not found in ChatArea");
            var mic = view.FindNameScope()?.Find<Button>("MicButton")
                ?? throw new InvalidOperationException("MicButton not found in ChatArea");

            // 命令接线不受样式改造影响
            Assert.Same(vm.ChatVM.SendMessageCommand, send.Command);

            // 芯片底色 = 主题感知 token（VOICE/NOPE 同族），恰为芯片底色而非旧紫灰
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(send.Background);
            Assert.Equal(Color.FromUInt32(0xEE050507), background.Color);

            // 语言无关的箭头图标：线稿 Path、芯片文字色描边（文字版复活即红——
            // 不识英文的用户读不懂 SEND）
            var arrow = Assert.IsType<Avalonia.Controls.Shapes.Path>(send.Content);
            Assert.Null(arrow.Fill);
            Assert.NotNull(arrow.Stroke);

            // 按键簇防回退锚（结构断言而非字体度量——headless 字体维度是已知盲区）：
            // 麦克风与发送须同簇紧排（4px，等宽 Grid 列的 9px 列距+按钮内边距会把
            // 观感间隔放大到过开——2026-11-03 用户反馈）；不设固定宽贴内容自适应
            var cluster = Assert.IsType<StackPanel>(send.Parent);
            Assert.Equal(4, cluster.Spacing);
            Assert.Same(mic, cluster.Children[0]);
            Assert.Same(send, cluster.Children[1]);
            Assert.True(double.IsNaN(send.Width), "发送键不应设固定宽（贴内容自适应）");
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void ChatMessages_SupportTextSelectionAndCopyContextMenu()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        var fluent = new Avalonia.Themes.Fluent.FluentTheme();
        Avalonia.Application.Current!.Styles.Add(fluent);
        Window? window = null;
        try
        {
            vm.ChatVM.Messages.Add(new AIRadio.Desktop.Models.ChatMessage
            {
                Role = AIRadio.Desktop.Models.MessageRole.User,
                Content = "放一首安静的歌"
            });
            vm.ChatVM.AddAssistantMessage("好，这就换一首舒缓一点的。");

            var view = new ChatArea { DataContext = vm };
            window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 气泡文本必须用 SelectableTextBlock，支持鼠标直接划选 + Ctrl+C 复制
            var selectables = Avalonia.VisualTree.VisualExtensions
                .GetVisualDescendants(view)
                .OfType<SelectableTextBlock>()
                .ToList();
            Assert.Equal(2, selectables.Count);

            // 全部对话格式化：带发言人署名
            var formatted = ChatViewModel.FormatConversation(vm.ChatVM.Messages);
            Assert.Contains("放一首安静的歌", formatted);
            // mock DJ 无实名 → 回退通用署名
            Assert.Contains("AI 主播: 好，这就换一首舒缓一点的。", formatted);
            Assert.Contains("我: 放一首安静的歌", formatted);

            // 右键菜单项：有选中文本 + 命中气泡 + 有历史消息时三项齐全
            var entriesWithSelection = ChatArea.BuildContextMenuEntries(
                vm.ChatVM.Messages[1], "舒缓一点", vm.ChatVM.Messages.Count);
            Assert.Equal(3, entriesWithSelection.Count);
            Assert.Equal(ChatArea.ChatMenuAction.CopySelection, entriesWithSelection[0].Action);
            Assert.Equal(ChatArea.ChatMenuAction.CopyMessage, entriesWithSelection[1].Action);
            Assert.Equal(ChatArea.ChatMenuAction.CopyConversation, entriesWithSelection[2].Action);

            // 未选中文本但右键气泡：复制本条消息 + 复制全部对话
            var entriesBubbleOnly = ChatArea.BuildContextMenuEntries(
                vm.ChatVM.Messages[1], null, vm.ChatVM.Messages.Count);
            Assert.Equal(2, entriesBubbleOnly.Count);
            Assert.Equal(ChatArea.ChatMenuAction.CopyMessage, entriesBubbleOnly[0].Action);
            Assert.Equal(ChatArea.ChatMenuAction.CopyConversation, entriesBubbleOnly[1].Action);
        }
        finally
        {
            window?.Close();
            Avalonia.Application.Current!.Styles.Remove(fluent);
            vm.Dispose();
        }
    }
}
