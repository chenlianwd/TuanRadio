using System;
using System.Collections.Generic;
using System.Reactive;
using AIRadio.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AIRadio.Desktop.Views;

/// <summary>库抽屉（播放列表/收藏/搜索 + 导入）。OnImportFiles 用 FilePickerHelper（spec §5.5）。</summary>
public partial class PlaylistDrawer : UserControl
{
    /// <summary>行右键菜单的目标列表。</summary>
    internal enum PlaylistListKind { Library, Favorites, Search }

    internal enum PlaylistMenuAction { PlayNext, Enqueue, CopyInfo, Remove, RemoveSelected, FavoriteSelected }

    internal sealed record PlaylistMenuEntry(string Header, PlaylistMenuAction Action, bool IsSeparator = false);

    // 拖拽排序只在主歌单列表内自吞自吐：自定义应用级格式携带可见序索引，外部拖放源带不动它
    private static readonly DataFormat<string> LibraryDragFormat =
        DataFormat.CreateStringApplicationFormat("AIRadio-Library-TrackIndex");
    private const double DragThreshold = 4;

    private Point? _libraryDragOrigin;
    private bool _libraryDragStarted;
    private int _libraryDragOldIndex = -1;

    public PlaylistDrawer()
    {
        InitializeComponent();

        LibraryList.AddHandler(Control.ContextRequestedEvent, (_, e) => HandleRowContextRequested(e, PlaylistListKind.Library));
        FavoritesList.AddHandler(Control.ContextRequestedEvent, (_, e) => HandleRowContextRequested(e, PlaylistListKind.Favorites));
        SearchResultsList.AddHandler(Control.ContextRequestedEvent, (_, e) => HandleRowContextRequested(e, PlaylistListKind.Search));

        LibraryList.AddHandler(InputElement.PointerPressedEvent, OnLibraryPointerPressed, RoutingStrategies.Tunnel);
        LibraryList.AddHandler(InputElement.PointerMovedEvent, OnLibraryPointerMoved, RoutingStrategies.Tunnel);
        LibraryList.AddHandler(InputElement.PointerReleasedEvent, OnLibraryPointerReleased, RoutingStrategies.Tunnel);
        DragDrop.AddDragOverHandler(LibraryList, OnLibraryDragOver);
        DragDrop.AddDropHandler(LibraryList, OnLibraryDrop);
    }

    // ==== 行右键菜单 ====

    /// <summary>
    /// 构建行右键菜单项（纯函数供直测）。文案内联 T 双语；批量项文案含数量，
    /// 这是走 code-behind 而非 XAML ContextFlyout 的主因（Flyout 不在可视树内、
    /// $parent 命令绑定链走不通）。
    /// </summary>
    internal static IReadOnlyList<PlaylistMenuEntry> BuildRowMenuEntries(PlaylistListKind kind, int selectionCount)
    {
        PlaylistMenuEntry Entry(PlaylistMenuAction action) => action switch
        {
            PlaylistMenuAction.PlayNext => new(Services.AppLanguage.T("下一首播", "Play next"), action),
            PlaylistMenuAction.Enqueue => new(Services.AppLanguage.T("加入队列", "Add to queue"), action),
            PlaylistMenuAction.CopyInfo => new(Services.AppLanguage.T("复制歌曲信息", "Copy track info"), action),
            PlaylistMenuAction.Remove => new(Services.AppLanguage.T("移除", "Remove"), action),
            _ => new(string.Empty, action),
        };
        static PlaylistMenuEntry Separator() => new(string.Empty, PlaylistMenuAction.PlayNext, IsSeparator: true);

        if (kind == PlaylistListKind.Library)
        {
            var entries = new List<PlaylistMenuEntry>
            {
                Entry(PlaylistMenuAction.PlayNext),
                Entry(PlaylistMenuAction.Enqueue),
                Entry(PlaylistMenuAction.CopyInfo),
                Separator(),
            };
            if (selectionCount > 1)
            {
                entries.Add(new(Services.AppLanguage.T($"移除所选 {selectionCount} 首", $"Remove {selectionCount} selected"),
                    PlaylistMenuAction.RemoveSelected));
                entries.Add(new(Services.AppLanguage.T($"收藏所选 {selectionCount} 首", $"Favorite {selectionCount} selected"),
                    PlaylistMenuAction.FavoriteSelected));
                entries.Add(Separator());
            }
            entries.Add(Entry(PlaylistMenuAction.Remove));
            return entries;
        }

        // 收藏/搜索行：下一首播 + 加队列 + 复制（移除/取消收藏由行内按钮承担）
        return new[]
        {
            Entry(PlaylistMenuAction.PlayNext),
            Entry(PlaylistMenuAction.Enqueue),
            Entry(PlaylistMenuAction.CopyInfo),
        };
    }

    private void HandleRowContextRequested(ContextRequestedEventArgs e, PlaylistListKind kind)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Source is not Visual source) return;
        var container = source.FindAncestorOfType<ListBoxItem>();
        if (container?.Content == null) return;

        var selectionCount = kind == PlaylistListKind.Library ? vm.PlaylistVM.SelectedLibraryTracks.Count : 0;
        if (kind == PlaylistListKind.Library &&
            container.Content is Models.Track rowTrack &&
            !vm.PlaylistVM.SelectedLibraryTracks.Contains(rowTrack))
        {
            // 资源管理器惯例：右键未选中的行时先把选择收敛到该行，批量菜单项才与
            // 用户所见的目标一致
            vm.PlaylistVM.SelectedLibraryTracks.Clear();
            vm.PlaylistVM.SelectedLibraryTracks.Add(rowTrack);
            selectionCount = 1;
        }
        var entries = BuildRowMenuEntries(kind, selectionCount);

        var menu = new MenuFlyout();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var captured = entry;
            var item = container.Content;
            var menuItem = new MenuItem { Header = captured.Header };
            menuItem.Click += (_, _) => InvokeMenuAction(captured.Action, kind, item, vm);
            menu.Items.Add(menuItem);
        }

        e.Handled = true;
        menu.ShowAt(container, showAtPointer: true);
    }

    private void InvokeMenuAction(PlaylistMenuAction action, PlaylistListKind kind, object item, MainWindowViewModel vm)
    {
        var track = item as Models.Track;
        var online = item as Services.OnlineTrack;
        switch (action)
        {
            case PlaylistMenuAction.PlayNext:
                if (track != null)
                    vm.PlaylistVM.PlayTrackNextCommand.Execute(track).Subscribe();
                else if (online != null)
                    vm.PlaylistVM.PlaySearchNextCommand.Execute(online).Subscribe();
                break;
            case PlaylistMenuAction.Enqueue:
                if (track != null)
                    vm.PlaylistVM.EnqueueTrackCommand.Execute(track).Subscribe();
                else if (online != null)
                    vm.PlaylistVM.AddSearchToQueueCommand.Execute(online).Subscribe();
                break;
            case PlaylistMenuAction.CopyInfo:
                CopyTrackInfoToClipboard(kind, track, online, vm);
                break;
            case PlaylistMenuAction.Remove:
                if (track != null)
                    vm.PlaylistVM.RemoveTrackCommand.Execute(track).Subscribe();
                break;
            case PlaylistMenuAction.RemoveSelected:
                vm.PlaylistVM.RemoveSelectedTracksCommand.Execute(Unit.Default).Subscribe();
                break;
            case PlaylistMenuAction.FavoriteSelected:
                vm.PlaylistVM.FavoriteSelectedTracksCommand.Execute(Unit.Default).Subscribe();
                break;
        }
    }

    private async void CopyTrackInfoToClipboard(PlaylistListKind kind, Models.Track? track, Services.OnlineTrack? online, MainWindowViewModel vm)
    {
        // async void 事件路径必须自捕获异常：剪贴板在无 topLevel/关闭竞态时抛出
        var title = track?.Title ?? online?.Title ?? string.Empty;
        var artist = track?.Artist ?? online?.Artist;
        var text = ViewModels.PlaylistViewModel.FormatTrackInfo(title, artist);
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
            if (kind == PlaylistListKind.Search)
                // 走 SetSearchStatus 登记工厂：直赋会被下一次语言切换用旧工厂文案顶掉
                vm.PlaylistVM.SetSearchStatus(() => Services.AppLanguage.T($"已复制「{text}」。", $"Copied \"{text}\"."));
            else if (kind == PlaylistListKind.Library)
                vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T($"已复制「{text}」。", $"Copied \"{text}\".");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Copy track info to clipboard failed");
        }
    }

    // ==== 主歌单拖拽排序 ====

    private void OnLibraryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _libraryDragOrigin = null;
        _libraryDragStarted = false;
        _libraryDragOldIndex = -1;
        // 隧道订阅：ListBox 选择/Button 点击的类处理器都会把冒泡的 PointerPressed 标记
        // Handled，普通注册收不到行内按下；按钮点击不作为拖拽起点（▶/❤️/✕ 轻微挪动
        // 不应劫持成拖拽）
        if (e.Source is not Visual source) return;
        if (source.FindAncestorOfType<Avalonia.Controls.Button>() != null) return;
        if (source.FindAncestorOfType<ListBoxItem>() is not { } container) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.PlaylistVM.CanReorderLibrary) return;

        // 行归属在按下时刻锁定：Moved 现找会在行边界带（按下点近邻下缘+跨行微移）抓错行
        var index = LibraryList.IndexFromContainer(container);
        if (index < 0) return;

        _libraryDragOldIndex = index;
        _libraryDragOrigin = e.GetPosition(LibraryList);
    }

    private void OnLibraryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 松手即清起点：无按键态的悬停移动不得发起拖拽（DoDragDropAsync 在无按键时
        // 会进入永不返回的拖拽循环）
        _libraryDragOrigin = null;
        _libraryDragStarted = false;
        _libraryDragOldIndex = -1;
    }

    private async void OnLibraryPointerMoved(object? sender, PointerEventArgs e)
    {
        // async void 事件路径必须自捕获异常：DoDragDrop 在窗口关闭竞态时抛出
        try
        {
            if (_libraryDragOrigin is not { } origin || _libraryDragStarted) return;
            if (_libraryDragOldIndex < 0) return;
            if (!e.GetCurrentPoint(LibraryList).Properties.IsLeftButtonPressed)
            {
                _libraryDragOrigin = null;
                return;
            }

            var position = e.GetPosition(LibraryList);
            if (Math.Abs(position.X - origin.X) < DragThreshold &&
                Math.Abs(position.Y - origin.Y) < DragThreshold)
                return;

            _libraryDragStarted = true;
            var item = new DataTransferItem();
            item.Set(LibraryDragFormat, _libraryDragOldIndex.ToString());
            var transfer = new DataTransfer();
            transfer.Add(item);
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Library drag failed");
        }
        finally
        {
            _libraryDragOrigin = null;
            _libraryDragStarted = false;
            _libraryDragOldIndex = -1;
        }
    }

    private void OnLibraryDragOver(object? sender, DragEventArgs e)
    {
        if (ContainsLibraryDragIndex(e.DataTransfer) &&
            DataContext is MainWindowViewModel vm &&
            vm.PlaylistVM.CanReorderLibrary)
        {
            e.DragEffects = DragDropEffects.Move;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnLibraryDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel vm || !vm.PlaylistVM.CanReorderLibrary) return;
        if (!TryGetLibraryDragIndex(e.DataTransfer, out var oldIndex)) return;

        var rows = new List<(int Index, double Top, double Height)>();
        foreach (var container in LibraryList.GetRealizedContainers())
        {
            if (container is not ListBoxItem item) continue;
            var index = LibraryList.IndexFromContainer(item);
            if (index < 0) continue;
            var topLeft = item.TranslatePoint(new Point(0, 0), LibraryList);
            if (topLeft is not { } point) continue;
            rows.Add((index, point.Y, item.Bounds.Height));
        }

        var y = e.GetPosition(LibraryList).Y;
        var raw = ComputeDropInsertIndex(rows, y, vm.PlaylistVM.VisibleLibraryTracks.Count);
        // 换算到移除拖拽项后的插入坐标：拖拽项在落点之前时整体前移一位
        var insert = raw - (oldIndex < raw ? 1 : 0);
        vm.PlaylistVM.MoveVisibleTrack(oldIndex, insert);
    }

    private static bool ContainsLibraryDragIndex(IDataTransfer transfer)
        => TryGetLibraryDragIndex(transfer, out _);

    private static bool TryGetLibraryDragIndex(IDataTransfer transfer, out int index)
    {
        index = -1;
        foreach (var item in transfer.Items)
        {
            if (item.TryGetRaw(LibraryDragFormat) is string value && int.TryParse(value, out var parsed))
            {
                index = parsed;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 落点 → 可见列表插入索引：命中行上半区=插到该行之前，越过所有已实现行=列表末尾。
    /// 返回的是"当前可见序"下的原始插入位，调用方自行换算移除拖拽项后的坐标。
    /// </summary>
    internal static int ComputeDropInsertIndex(
        IReadOnlyList<(int Index, double Top, double Height)> rows, double y, int fallbackIndex)
    {
        foreach (var (index, top, height) in rows)
        {
            if (y < top + height / 2)
                return index;
        }
        return fallbackIndex;
    }

    // ==== 定位在播 ====

    private void OnLocatePlaying(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var target = vm.PlaylistVM.NowPlayingListTrack;
        if (target == null)
        {
            vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T(
                "当前没有在播歌曲。", "Nothing is playing right now.");
            return;
        }
        if (!vm.PlaylistVM.VisibleLibraryTracks.Contains(target))
        {
            // 在播曲被过滤词/同步歌单视图排除：提示代替无动作
            vm.PlaylistVM.LocalLibraryStatus = Services.AppLanguage.T(
                "在播歌曲不在当前过滤视图内。", "The playing track is not in the current filtered view.");
            return;
        }
        LibraryList.ScrollIntoView(target);
    }

    // ==== 导入 ====

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
