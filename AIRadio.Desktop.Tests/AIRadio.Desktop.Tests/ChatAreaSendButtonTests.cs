using System;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 聊天发送按钮回归：SEND 实体按键样式（复古芯片家族 token）替换原紫灰圆钮
/// （C_FF7F7896 为游离于配色主题外的硬编码遗留，此测试防止其回潮）。
/// </summary>
public class ChatAreaSendButtonTests
{
    [AvaloniaFact]
    public void SendButton_IsRetroChipWithSendLabel()
    {
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        var view = new ChatArea { DataContext = vm };
        // 同时注入新旧两个键：断言背景恰为主题感知的芯片底色——若有人把 Background
        // 改回 C_FF7F7896（资源仍可解析）或任何无关色，Equal 即红
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ["C_EE050507"] = Color.FromUInt32(0xEE050507),
            ["C_FF7F7896"] = Color.FromUInt32(0xFF7F7896)
        });
        Window? window = null;
        try
        {
            window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var send = view.FindNameScope()?.Find<Button>("SendButton")
                ?? throw new InvalidOperationException("SendButton not found in ChatArea");

            // 命令接线不受样式改造影响
            Assert.Same(vm.ChatVM.SendMessageCommand, send.Command);

            // 芯片底色 = 主题感知 token（VOICE/NOPE 同族），恰为芯片底色而非旧紫灰
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(send.Background);
            Assert.Equal(Color.FromUInt32(0xEE050507), background.Color);

            // 实体按键文字词汇：全大写 SEND（与 VOL/ON AIR 同为有意保留的英文设计元素）
            var label = Assert.IsType<TextBlock>(send.Content);
            Assert.Equal("SEND", label.Text);

            // 列宽防裁剪锚（结构断言而非字体度量——headless 字体维度是已知盲区，实测度量不可信）：
            // 发送列须与麦克风列同为 56px，给 SEND 文字（Consolas 11 bold 实算 ≈27px）留足内容区
            Assert.Equal(56, ((Grid)send.Parent!).ColumnDefinitions[2].Width.Value);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
        }
    }
}
