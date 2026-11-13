using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Immutable;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// 独立搜索浮层与曲库抽屉解耦测试：顶栏搜索打开 SearchDrawer（自动聚焦、输入绑定、回车搜索、
// 按钮 CommandParameter、关闭按钮、视觉树分离生命周期），控制栏菜单打开 PlaylistDrawer
// （无搜索页签、激活页签在静态与悬停/按下态下均解析出 AccentGreen 描边与高亮）。
public class SearchDrawerTests
{
    private static MainWindowViewModel CreateViewModel(Mock<IMusicSearchService> searchMock, string settingsDir)
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(a => a.TrackEnded).Returns(new Subject<Track?>());
        audio.SetupGet(a => a.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(a => a.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(a => a.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.SetupGet(a => a.SpectrumData).Returns(new Subject<float[]>());
        audio.SetupGet(a => a.TtsStateChanged).Returns(new Subject<bool>());
        audio.SetupGet(a => a.TtsError).Returns(new Subject<string>());
        audio.SetupGet(a => a.Playlist).Returns(new List<Track>().AsReadOnly());

        return new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            searchMock.Object,
            new Mock<ISttService>().Object,
            System.IO.Path.Combine(settingsDir, "playlist.json"),
            System.IO.Path.Combine(settingsDir, "settings.json"));
    }

    [AvaloniaFact]
    public void SearchDrawer_AndPlaylistDrawer_AreSeparateAndWiredEndToEnd()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var searchMock = new Mock<IMusicSearchService>();
        searchMock.Setup(s => s.SearchAsync("hello", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OnlineTrack> { new() { Id = "s-1", Title = "Hello", Artist = "Artist", Source = "netease" } });

        var vm = CreateViewModel(searchMock, dir);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var searchButton = window.FindControl<Button>("SearchButton")
                ?? throw new InvalidOperationException("SearchButton not found");
            var searchDrawer = window.FindDescendantOfType<SearchDrawer>()
                ?? throw new InvalidOperationException("SearchDrawer not found");
            var playlistDrawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");
            var searchDrawerRoot = searchDrawer.FindControl<Border>("DrawerRoot")
                ?? throw new InvalidOperationException("SearchDrawer DrawerRoot not found");
            var playlistDrawerRoot = playlistDrawer.FindControl<Border>("DrawerRoot")
                ?? throw new InvalidOperationException("PlaylistDrawer DrawerRoot not found");

            Assert.False(searchDrawerRoot.IsVisible);
            Assert.False(playlistDrawerRoot.IsVisible);
            Assert.Equal(vm.ToggleSearchCommand, searchButton.Command);

            // 点击顶栏搜索按钮 -> 仅打开独立搜索浮层并聚焦输入框
            searchButton.Command!.Execute(searchButton.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSearchOpen);
            Assert.False(vm.IsLibraryOpen);
            Assert.True(searchDrawerRoot.IsVisible);
            Assert.False(playlistDrawerRoot.IsVisible);

            var searchInput = searchDrawer.FindControl<TextBox>("SearchInput")
                ?? throw new InvalidOperationException("SearchInput not found");
            Assert.True(searchInput.IsFocused);

            // TwoWay 输入绑定 + 状态提示可见性 + 回车触发搜索
            searchInput.Text = "hello";
            Assert.Equal("hello", vm.PlaylistVM.SearchText);

            var nonEnterArgs = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A };
            searchInput.RaiseEvent(nonEnterArgs);
            Assert.False(nonEnterArgs.Handled);
            searchMock.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

            var enterArgs = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            searchInput.RaiseEvent(enterArgs);
            Dispatcher.UIThread.RunJobs();

            Assert.True(enterArgs.Handled);
            searchMock.Verify(s => s.SearchAsync("hello", 20, It.IsAny<CancellationToken>()), Times.Once);

            // 结果项 Play/Add 按钮 Command 与 CommandParameter 均正确绑定
            var resultsList = searchDrawer.FindControl<ListBox>("SearchResultsList")
                ?? throw new InvalidOperationException("SearchResultsList not found");
            var track = Assert.Single(vm.PlaylistVM.SearchResults);
            var row = resultsList.GetRealizedContainers().OfType<ListBoxItem>()
                .FirstOrDefault(item => ReferenceEquals(item.Content, track))
                ?? throw new InvalidOperationException("search result row not realized");
            var rowButtons = row.GetVisualDescendants().OfType<Button>().ToList();
            var playBtn = rowButtons.Single(b => ReferenceEquals(b.Command, vm.PlaylistVM.PlayOnlineCommand));
            var addBtn = rowButtons.Single(b => ReferenceEquals(b.Command, vm.PlaylistVM.AddOnlineCommand));
            Assert.Equal(track, playBtn.CommandParameter);
            Assert.Equal(track, addBtn.CommandParameter);

            // 搜索按钮在 IsSearching=true 时禁用且 Enter 不重复触发
            var executeSearchBtn = searchDrawer.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.PlaylistVM.SearchCommand));
            Assert.True(executeSearchBtn.IsEnabled);
            vm.PlaylistVM.IsSearching = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(executeSearchBtn.IsEnabled);

            var enterWhileSearching = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            searchInput.RaiseEvent(enterWhileSearching);
            Assert.True(enterWhileSearching.Handled);
            searchMock.Verify(s => s.SearchAsync("hello", 20, It.IsAny<CancellationToken>()), Times.Once);
            vm.PlaylistVM.IsSearching = false;

            // 关闭按钮绑定 ToggleSearchCommand，点击可关闭搜索浮层；再点菜单仅打开曲库且保留 Tab 0
            var closeButton = searchDrawer.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Command, vm.ToggleSearchCommand));
            closeButton.Command!.Execute(closeButton.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSearchOpen);
            Assert.False(searchDrawerRoot.IsVisible);

            vm.ToggleLibraryCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsLibraryOpen);
            Assert.False(vm.IsSearchOpen);
            Assert.True(playlistDrawerRoot.IsVisible);
            Assert.False(searchDrawerRoot.IsVisible);
            Assert.Equal(0, vm.PlaylistVM.TabIndex);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
            try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void PlaylistDrawer_HasNoSearchTab_AndHighlightsActiveTabBrushesAcrossStates()
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
            window.Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri("avares://AIRadio/Themes/Colors.axaml")));
            window.Show();
            vm.ToggleLibraryCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            var playlistDrawer = window.FindDescendantOfType<PlaylistDrawer>()
                ?? throw new InvalidOperationException("PlaylistDrawer not found");

            Assert.Null(playlistDrawer.FindControl<ListBox>("SearchResultsList"));
            Assert.DoesNotContain(
                playlistDrawer.GetVisualDescendants().OfType<Button>(),
                b => ReferenceEquals(b.Command, vm.OpenSearchCommand) || ReferenceEquals(b.Command, vm.ToggleSearchCommand));

            var tabButtons = playlistDrawer.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("drawer-tab"))
                .ToList();
            var playlistTab = tabButtons.Single(b => ReferenceEquals(b.Command, vm.OpenPlaylistCommand));
            var favoritesTab = tabButtons.Single(b => ReferenceEquals(b.Command, vm.OpenFavoritesCommand));
            var programTab = tabButtons.Single(b => ReferenceEquals(b.Command, vm.OpenProgramCommand));
            var radioTab = tabButtons.Single(b => ReferenceEquals(b.Command, vm.OpenRadioStationsCommand));

            const uint accentGreen = 0xFF56F5C4u;
            static uint? BrushColor(Avalonia.Media.IBrush? brush)
                => (brush as ImmutableSolidColorBrush)?.Color.ToUInt32();

            static void AssertTabActiveBrush(Button tab, bool expectedActive)
            {
                var presenter = tab.GetVisualDescendants().OfType<ContentPresenter>().First();
                var path = tab.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault();
                if (expectedActive)
                {
                    Assert.Contains("active", tab.Classes);
                    Assert.Equal(accentGreen, BrushColor(tab.BorderBrush));
                    Assert.Equal(accentGreen, BrushColor(presenter.BorderBrush));
                    if (path != null)
                        Assert.Equal(accentGreen, BrushColor(path.Stroke));
                }
                else
                {
                    Assert.DoesNotContain("active", tab.Classes);
                    Assert.NotEqual(accentGreen, BrushColor(tab.BorderBrush));
                    if (path != null)
                        Assert.NotEqual(accentGreen, BrushColor(path.Stroke));
                }
            }

            // Tab 0 初始激活
            AssertTabActiveBrush(playlistTab, true);
            AssertTabActiveBrush(favoritesTab, false);

            // 激活态下悬停与按下时 Fluent PART_ContentPresenter 仍保持 AccentGreen 边框
            ((IPseudoClasses)playlistTab.Classes).Set(":pointerover", true);
            Dispatcher.UIThread.RunJobs();
            AssertTabActiveBrush(playlistTab, true);
            ((IPseudoClasses)playlistTab.Classes).Set(":pressed", true);
            Dispatcher.UIThread.RunJobs();
            AssertTabActiveBrush(playlistTab, true);
            ((IPseudoClasses)playlistTab.Classes).Set(":pressed", false);
            ((IPseudoClasses)playlistTab.Classes).Set(":pointerover", false);

            // 依次切换到 Tab 1（收藏）、Tab 3（节目单）、Tab 5（网络电台）
            vm.OpenFavoritesCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            AssertTabActiveBrush(playlistTab, false);
            AssertTabActiveBrush(favoritesTab, true);

            vm.PlaylistVM.TabIndex = 3;
            Dispatcher.UIThread.RunJobs();
            AssertTabActiveBrush(favoritesTab, false);
            AssertTabActiveBrush(programTab, true);

            vm.PlaylistVM.TabIndex = 5;
            Dispatcher.UIThread.RunJobs();
            AssertTabActiveBrush(programTab, false);
            AssertTabActiveBrush(radioTab, true);
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
    public void SearchDrawer_DetachedFromVisualTree_DoesNotRetainFocusSubscription()
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
            var drawer = new SearchDrawer();
            window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 离树状态赋值 DataContext 后不应在未挂载时触发聚焦
            drawer.DataContext = vm;
            vm.OpenSearchCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            var input = drawer.FindControl<TextBox>("SearchInput")!;
            Assert.False(input.IsFocused);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
