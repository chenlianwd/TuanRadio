using System;
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
}
