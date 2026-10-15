using Avalonia;
using System;
using System.Threading;

namespace AIRadio.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 中文系统控制台默认 GBK；日志/转发输出统一 UTF-8，否则调试输出整片乱码。
        // 无附加控制台（双击启动的 WinExe）时该 setter 可能抛 IOException，静默跳过
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

        // 单实例守卫：双开的原子写保证文件不损坏，但 last-writer-wins 会静默回滚
        // 另一实例的设置/歌单变更，且双实例共享代理端口产生难排查的生死序列。
        // Local\ 作用域 = 每登录会话一份（%APPDATA% 按用户隔离，跨会话互不干扰）
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AIRadio.TuanRadio.SingleInstance",
            out var isFirstInstance);
        if (!isFirstInstance)
            return;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
