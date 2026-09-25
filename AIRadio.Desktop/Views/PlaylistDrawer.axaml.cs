using System;
using System.Reactive;
using AIRadio.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIRadio.Desktop.Views;

/// <summary>库抽屉（播放列表/收藏/搜索 + 导入）。OnImportFiles 用 FilePickerHelper（spec §5.5）。</summary>
public partial class PlaylistDrawer : UserControl
{
    public PlaylistDrawer()
    {
        InitializeComponent();
    }

    private async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        // async void 事件处理器必须自捕获异常：文件选择器在存储提供者不可用/窗口关闭
        // 竞态时抛出，直达 dispatcher 未处理异常会崩掉整个进程
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var paths = await FilePickerHelper.PickAudioFilesAsync(topLevel);
            if (paths.Length > 0) await vm.PlaylistVM.AddFilesAndIndexAsync(paths);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Import audio files failed");
            if (DataContext is MainWindowViewModel vm)
                vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T("本地文件导入失败", "Local file import failed");
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
            vm.PlaylistVM.SearchCommand.Execute(Unit.Default).Subscribe();
    }

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folder = await FilePickerHelper.PickAudioFolderAsync(topLevel);
            if (!string.IsNullOrWhiteSpace(folder))
                await vm.PlaylistVM.AddLocalFolderAsync(folder);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Import local music folder failed");
            if (DataContext is MainWindowViewModel vm)
                vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T("本地曲库导入失败", "Local library import failed");
        }
    }

    private async void OnRescanLocalLibrary(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is MainWindowViewModel vm)
                await vm.PlaylistVM.RescanLocalLibraryAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Rescan local music library failed");
            if (DataContext is MainWindowViewModel vm)
                vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T("本地曲库重扫失败", "Local library rescan failed");
        }
    }
}
