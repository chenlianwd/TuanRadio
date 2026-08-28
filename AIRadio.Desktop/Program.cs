using Avalonia;
using System;

namespace AIRadio.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 中文系统控制台默认 GBK；日志/转发输出统一 UTF-8，否则调试输出整片乱码。
        // 无附加控制台（双击启动的 WinExe）时该 setter 可能抛 IOException，静默跳过
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console attached */ }

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
