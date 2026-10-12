using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AIRadio.Desktop.Services;

/// <summary>
/// AppLanguage 的宿主侧 UI 适配（AppLanguage 本体已去 UI 框架化、驻留 AIRadio.Desktop.Music 类库）：
/// 持有字符串宿主资源字典、订阅 Changed 原地重填触发 DynamicResource 刷新，
/// 并向 AppLanguage 注入 UI 线程编组器（Dispatcher.Post）。
/// </summary>
public static class AppLanguageUi
{
    private static ResourceDictionary? _stringsHost;

    /// <summary>App 启动时挂载字符串宿主字典并注入编组器；必须在主窗口创建之前调用。</summary>
    public static void Attach(Application app)
    {
        _stringsHost = new ResourceDictionary();
        app.Resources.MergedDictionaries.Add(_stringsHost);
        FillStrings(AppLanguage.Current);
        // 先订阅重填：后续 Changed 订阅者（VM 重算）触发时资源已是新语言
        AppLanguage.Changed += OnLanguageChanged;
        AppLanguage.SetUiMarshal(action =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        });
    }

    private static void OnLanguageChanged() => FillStrings(AppLanguage.Current);

    private static void FillStrings(string language)
    {
        if (_stringsHost == null)
            return;

        var source = AppLanguage.GetStringTable(language);
        _stringsHost.Clear();
        foreach (var pair in source)
            _stringsHost[pair.Key] = pair.Value;
    }
}
