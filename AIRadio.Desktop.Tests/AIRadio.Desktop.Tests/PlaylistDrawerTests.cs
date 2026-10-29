using System;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;
using PlaylistDrawer = AIRadio.Desktop.Views.PlaylistDrawer;

namespace AIRadio.Desktop.Tests;

// 歌单列表操作增强的 UI 接线锚定：主歌单行 ▶/多选模式、在播高亮 class、收藏行 ❤️、
// 头部过滤/清空控件、右键菜单项矩阵与拖拽落点纯函数。遵循"绑定断言 + 命令直驱"
// 惯例（headless 下未渲染窗口无法可靠投递指针输入，行为由 PlaylistViewModelTests 覆盖）。
public class PlaylistDrawerTests
{
    [AvaloniaFact]
    public void LibraryRow_PlayButtonAndMultiSelect_Wired()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out var audio);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 0;
            var first = new Track { SourceId = "kugou:a", Title = "A" };
            vm.PlaylistVM.Tracks.Add(first);
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            var list = drawer.FindControl<ListBox>("LibraryList")
                ?? throw new InvalidOperationException("LibraryList not found");

            // 多选改造锚定：点行=选中而非播放（播放由行内 ▶ 承接）
            Assert.Equal(Avalonia.Controls.SelectionMode.Multiple, list.SelectionMode);

            var row = list.GetRealizedContainers().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Content, first))
                ?? throw new InvalidOperationException("library row not realized");
            var playButton = row.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Command == vm.PlaylistVM.PlayFromLibraryCommand)
                ?? throw new InvalidOperationException("▶ button not wired");
            Assert.Equal(first, playButton.CommandParameter);
            Assert.Contains(vm.PlaylistVM.PlayFromLibraryCommand,
                row.GetVisualDescendants().OfType<Button>().Select(button => button.Command));
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void NowPlaying_RowClassFlipsOnTrackChanged()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out var audio);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var track = new Track { SourceId = "kugou:a", Title = "A" };
            vm.PlaylistVM.Tracks.Add(track);

            window = new MainWindow { DataContext = vm };
            // 加载真实色板：在播高亮断言要读到生效 Background（DynamicResource 须可解析）
            window.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://AIRadio/Themes/Colors.axaml")));
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            Avalonia.Controls.Border? RowBorder() => drawer.GetVisualDescendants()
                .OfType<Avalonia.Controls.Border>()
                .SingleOrDefault(b => b.Classes.Contains("library-row") && Equals(b.DataContext, track));

            var border = RowBorder() ?? throw new InvalidOperationException("library row border not found");
            Assert.DoesNotContain("playing", border.Classes);

            var subject = (Subject<Track?>)audio.Object.TrackChanged;
            subject.OnNext(track);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("playing", border.Classes);
            Assert.True(track.IsNowPlaying);
            // 生效值锚定（防样式静默失效）：playing 态描边翻为 AccentGreen（Dark=#56F5C4）、
            // 行背景保持卡片色（在播态不叠 tint——Light 档叠 tint 底正文对比度不达 4.5:1）
            var playingBorderBrush = Assert.IsType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>(
                border.GetValue(Avalonia.Controls.Border.BorderBrushProperty));
            Assert.Equal(0xFF56F5C4u, playingBorderBrush.Color.ToUInt32());
            var playingBackground = Assert.IsType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>(
                border.GetValue(Avalonia.Controls.Border.BackgroundProperty));
            Assert.Equal(0xCC202126u, playingBackground.Color.ToUInt32());

            subject.OnNext(null);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("playing", border.Classes);
            var idleBorderBrush = Assert.IsType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>(
                border.GetValue(Avalonia.Controls.Border.BorderBrushProperty));
            Assert.Equal(0x253D3D4Au, idleBorderBrush.Color.ToUInt32());
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void FavoritesRow_UnfavoriteButtonWired()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out _);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var track = new Track { SourceId = "kugou:a", Title = "A" };
            vm.PlaylistVM.Tracks.Add(track);
            vm.PlaylistVM.ToggleFavoriteCommand.Execute(track).Subscribe();

            window = new MainWindow { DataContext = vm };
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 1; // 收藏页
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            var favoritesList = drawer.FindControl<ListBox>("FavoritesList")
                ?? throw new InvalidOperationException("FavoritesList not found");
            var row = favoritesList.GetRealizedContainers().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Content, track))
                ?? throw new InvalidOperationException("favorites row not realized");

            // 收藏页取消收藏断头路修复锚定：行内 ❤️ 必须接 ToggleFavoriteCommand
            Assert.Contains(row.GetVisualDescendants().OfType<Button>(),
                button => button.Command == vm.PlaylistVM.ToggleFavoriteCommand);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void HeaderControls_FilterLocateClear_Wired()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out _);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");

            // 过滤框：VM → 控件单向可见（TwoWay 的源到目标侧）
            vm.PlaylistVM.LibraryFilterText = "周杰伦";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(drawer.GetVisualDescendants().OfType<TextBox>(), box => box.Text == "周杰伦");

            // 清空按钮：命令与武装态文案直连
            var clearButton = drawer.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Command == vm.PlaylistVM.ClearPlaylistCommand)
                ?? throw new InvalidOperationException("clear button not wired");
            Assert.Equal(vm.PlaylistVM.ClearPlaylistButtonText, clearButton.Content);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void LibraryDrag_PointerPressOnRowRecordsDragOrigin()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out _);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var track = new Track { SourceId = "kugou:a", Title = "A" };
            vm.PlaylistVM.Tracks.Add(track);

            window = new MainWindow { DataContext = vm };
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            var border = drawer.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .SingleOrDefault(b => b.Classes.Contains("library-row") && Equals(b.DataContext, track))
                ?? throw new InvalidOperationException("library row border not found");
            var windowPoint = border.TranslatePoint(new Avalonia.Point(5, 5), window)
                ?? throw new InvalidOperationException("cannot translate border position");

            // 真实指针按下（行内非按钮区域）：隧道订阅必须先于 ListBox 选择类处理器
            // 收到事件并记录拖拽起点与行归属（普通冒泡注册会被选择的 Handled 拦截恒收不到）
            window.MouseMove(windowPoint);
            window.MouseDown(windowPoint, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var drawerType = typeof(PlaylistDrawer);
            var originField = drawerType.GetField("_libraryDragOrigin",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var oldIndexField = drawerType.GetField("_libraryDragOldIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(originField);
            Assert.NotNull(oldIndexField);
            Assert.NotNull(originField.GetValue(drawer));
            // 唯一曲目行：按下即锁定索引 0（Moved 现找会在行边界带抓错行）
            Assert.Equal(0, oldIndexField.GetValue(drawer));

            // 松手清理（防悬停幽灵拖拽）
            window.MouseUp(windowPoint, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(originField.GetValue(drawer));
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void LibraryMultiSelect_RealPointerSelectionFlowsToViewModel()
    {
        // TwoWay 绑定端到端锚定：ListBox.SelectedItems 是 DirectProperty（getter 返回的
        // 集合实例原地增删、属性变更零通知），OneWayToSource 永不推送 VM——批量命令的
        // 选择通道必须经 TwoWay 由控件采纳 VM 集合实例来打通
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel(out _);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var first = new Track { SourceId = "kugou:a", Title = "A" };
            var second = new Track { SourceId = "kugou:b", Title = "B" };
            vm.PlaylistVM.Tracks.Add(first);
            vm.PlaylistVM.Tracks.Add(second);

            window = new MainWindow { DataContext = vm };
            window.Show();
            vm.IsLibraryOpen = true;
            vm.PlaylistVM.TabIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var drawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            var list = drawer.FindControl<ListBox>("LibraryList")
                ?? throw new InvalidOperationException("LibraryList not found");
            Avalonia.Point RowPoint(Track track) => drawer.GetVisualDescendants()
                .OfType<Avalonia.Controls.Border>()
                .Single(b => b.Classes.Contains("library-row") && Equals(b.DataContext, track))
                .TranslatePoint(new Avalonia.Point(150, 5), window)
                ?? throw new InvalidOperationException("cannot translate row position");

            // SelectionMode.Multiple = Explorer 风格：单击单选（替换）、Ctrl+点击加选
            var firstPoint = RowPoint(first);
            window.MouseMove(firstPoint);
            window.MouseDown(firstPoint, Avalonia.Input.MouseButton.Left);
            window.MouseUp(firstPoint, Avalonia.Input.MouseButton.Left);
            var secondPoint = RowPoint(second);
            window.MouseMove(secondPoint);
            window.MouseDown(secondPoint, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            window.MouseUp(secondPoint, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, vm.PlaylistVM.SelectedLibraryTracks.Count);
            Assert.Contains(first, vm.PlaylistVM.SelectedLibraryTracks);
            Assert.Contains(second, vm.PlaylistVM.SelectedLibraryTracks);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    // ==== 右键菜单项矩阵（纯函数） ====

    [Fact]
    public void BuildRowMenuEntries_LibrarySingleSelection_NoBatchEntries()
    {
        var entries = PlaylistDrawer.BuildRowMenuEntries(PlaylistDrawer.PlaylistListKind.Library, selectionCount: 1);
        var actions = entries.Where(e => !e.IsSeparator).Select(e => e.Action).ToArray();
        Assert.Equal(new[]
        {
            PlaylistDrawer.PlaylistMenuAction.PlayNext,
            PlaylistDrawer.PlaylistMenuAction.Enqueue,
            PlaylistDrawer.PlaylistMenuAction.CopyInfo,
            PlaylistDrawer.PlaylistMenuAction.Remove,
        }, actions);
    }

    [Fact]
    public void BuildRowMenuEntries_LibraryMultiSelection_IncludesBatchEntriesWithCount()
    {
        var entries = PlaylistDrawer.BuildRowMenuEntries(PlaylistDrawer.PlaylistListKind.Library, selectionCount: 3);
        var actions = entries.Where(e => !e.IsSeparator).Select(e => e.Action).ToArray();
        Assert.Equal(new[]
        {
            PlaylistDrawer.PlaylistMenuAction.PlayNext,
            PlaylistDrawer.PlaylistMenuAction.Enqueue,
            PlaylistDrawer.PlaylistMenuAction.CopyInfo,
            PlaylistDrawer.PlaylistMenuAction.RemoveSelected,
            PlaylistDrawer.PlaylistMenuAction.FavoriteSelected,
            PlaylistDrawer.PlaylistMenuAction.Remove,
        }, actions);
        Assert.Contains(entries, e => e.Action == PlaylistDrawer.PlaylistMenuAction.RemoveSelected && e.Header.Contains('3'));
        Assert.Contains(entries, e => e.Action == PlaylistDrawer.PlaylistMenuAction.FavoriteSelected && e.Header.Contains('3'));
    }

    [Fact]
    public void BuildRowMenuEntries_FavoritesAndSearch_QueueAndCopyOnly()
    {
        foreach (var kind in new[]
                 { PlaylistDrawer.PlaylistListKind.Favorites, PlaylistDrawer.PlaylistListKind.Search })
        {
            var entries = PlaylistDrawer.BuildRowMenuEntries(kind, selectionCount: 5);
            var actions = entries.Where(e => !e.IsSeparator).Select(e => e.Action).ToArray();
            Assert.Equal(new[]
            {
                PlaylistDrawer.PlaylistMenuAction.PlayNext,
                PlaylistDrawer.PlaylistMenuAction.Enqueue,
                PlaylistDrawer.PlaylistMenuAction.CopyInfo,
            }, actions);
        }
    }

    // ==== 拖拽落点纯函数 ====

    [Fact]
    public void ComputeDropInsertIndex_HitsRowUpperHalfBeforeIt_AndFallsThroughToEnd()
    {
        var rows = new (int Index, double Top, double Height)[]
        {
            (0, 0, 40),
            (1, 48, 40),
            (2, 96, 40),
        };

        // 第 1 行上半区：插到最前
        Assert.Equal(0, PlaylistDrawer.ComputeDropInsertIndex(rows, y: 10, fallbackIndex: 3));
        // 第 1 行下半区：插到第 2 行（索引 1）之前
        Assert.Equal(1, PlaylistDrawer.ComputeDropInsertIndex(rows, y: 30, fallbackIndex: 3));
        // 第 2 行上半区：插到第 2 行（索引 1）之前
        Assert.Equal(1, PlaylistDrawer.ComputeDropInsertIndex(rows, y: 60, fallbackIndex: 3));
        // 第 2 行下半区：插到第 3 行（索引 2）之前
        Assert.Equal(2, PlaylistDrawer.ComputeDropInsertIndex(rows, y: 80, fallbackIndex: 3));
        // 越过所有已实现行：列表末尾
        Assert.Equal(3, PlaylistDrawer.ComputeDropInsertIndex(rows, y: 500, fallbackIndex: 3));
        // 空行集合（虚拟化未实现任何行）：回落末尾
        Assert.Equal(2, PlaylistDrawer.ComputeDropInsertIndex(Array.Empty<(int, double, double)>(), y: 5, fallbackIndex: 2));
    }
}
