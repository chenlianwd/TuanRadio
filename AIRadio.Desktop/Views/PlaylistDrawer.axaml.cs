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
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var paths = await FilePickerHelper.PickAudioFilesAsync(topLevel);
        if (paths.Length > 0) vm.PlaylistVM.AddFiles(paths);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
            vm.PlaylistVM.SearchCommand.Execute(Unit.Default).Subscribe();
    }
}
