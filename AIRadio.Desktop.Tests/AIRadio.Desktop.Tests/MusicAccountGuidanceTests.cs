using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class MusicAccountGuidanceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public MusicAccountGuidanceTests()
    {
        AppLanguage.Apply("zh");
    }

    public void Dispose()
    {
        AppLanguage.Apply("zh");
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.GuidanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static Mock<IMusicSourceBroker> CreateBrokerMock(params MusicProviderDescriptor[] descriptors)
    {
        var broker = new Mock<IMusicSourceBroker>();
        broker.Setup(b => b.GetProviderDescriptors()).Returns(descriptors);
        broker.Setup(b => b.ConfigureProviders(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Callback(() => broker.Raise(b => b.ProviderConfigurationChanged += null, EventArgs.Empty));
        broker.Setup(b => b.SearchWithReportAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(), Array.Empty<SourceSearchStatus>()));
        broker.Setup(b => b.SearchFastWithReportAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(), Array.Empty<SourceSearchStatus>()));
        broker.Setup(b => b.SearchSlowWithReportAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(), Array.Empty<SourceSearchStatus>()));
        broker.Setup(b => b.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OnlineTrack>());
        return broker;
    }

    private static Mock<IAudioService> CreateAudioMock(Subject<PlaybackRecoveryNotice>? recoverySubject = null)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(a => a.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(a => a.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(a => a.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(a => a.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(a => a.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(a => a.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(a => a.TtsError).Returns(new Subject<string>());
        audio.Setup(a => a.Playlist).Returns(() => new List<Track>().AsReadOnly());
        if (recoverySubject != null)
            audio.Setup(a => a.PlaybackRecoveryNotices).Returns(recoverySubject);
        return audio;
    }

    private static Mock<ISecureStorage> CreateMemorySecureStorage()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var storage = new Mock<ISecureStorage>();
        storage.Setup(s => s.GetApiKeyAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => dict.TryGetValue(key, out var val) ? val : null);
        storage.Setup(s => s.SaveApiKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string key, string val) =>
            {
                dict[key] = val;
                return Task.CompletedTask;
            });
        storage.Setup(s => s.DeleteApiKey(It.IsAny<string>()))
            .Callback((string key) => dict.Remove(key));
        return storage;
    }

    private MainWindowViewModel CreateMainVm(
        out Mock<IAudioService> audio,
        out MusicAccountStore accounts,
        out Mock<IMusicSourceBroker> broker,
        Subject<PlaybackRecoveryNotice>? recoverySubject = null)
    {
        var dir = CreateTempDir();
        var storage = CreateMemorySecureStorage();
        accounts = new MusicAccountStore(storage.Object);
        broker = CreateBrokerMock(
            new MusicProviderDescriptor("Netease", "网易云音乐"),
            new MusicProviderDescriptor("Kugou", "酷狗音乐"));
        audio = CreateAudioMock(recoverySubject);
        return new MainWindowViewModel(
            audio.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            storage.Object,
            broker.Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            Path.Combine(dir, "settings.json"),
            accountStore: accounts);
    }

    [Fact]
    public void Settings_UnauthenticatedStatuses_DistinguishNeteaseOptionalAndKugouRequired()
    {
        var dir = CreateTempDir();
        using var vm = new SettingsViewModel(
            new Mock<ILLMService>().Object,
            CreateMemorySecureStorage().Object,
            Path.Combine(dir, "settings.json"));

        Assert.StartsWith("未登录", vm.NeteaseAccountStatus);
        Assert.Contains("免费曲库", vm.NeteaseAccountStatus);
        Assert.StartsWith("未登录", vm.KugouAccountStatus);
        Assert.Contains("未生效", vm.KugouAccountStatus);

        vm.SelectedLanguage = "en";

        Assert.StartsWith("Not signed in", vm.NeteaseAccountStatus);
        Assert.Contains("free catalog", vm.NeteaseAccountStatus);
        Assert.StartsWith("Not signed in", vm.KugouAccountStatus);
        Assert.Contains("inactive", vm.KugouAccountStatus);
    }

    [Fact]
    public async Task Settings_MusicProviderBadge_TracksKugouToggleLoginAndLanguage()
    {
        var dir = CreateTempDir();
        var storage = CreateMemorySecureStorage();
        var accounts = new MusicAccountStore(storage.Object);
        var broker = CreateBrokerMock(
            new MusicProviderDescriptor("Netease", "网易云音乐"),
            new MusicProviderDescriptor("Kugou", "酷狗音乐"));

        using var vm = new SettingsViewModel(
            new Mock<ILLMService>().Object,
            storage.Object,
            Path.Combine(dir, "settings.json"),
            accountStore: accounts,
            musicSearch: broker.Object);

        var netease = vm.MusicProviders.Single(p => p.Id == "Netease");
        var kugou = vm.MusicProviders.Single(p => p.Id == "Kugou");

        Assert.False(netease.NeedsQrLogin);
        Assert.False(netease.HasStatusBadge);
        Assert.Equal(string.Empty, netease.StatusBadge);

        if (!vm.HasExperimentalProviders)
        {
            Assert.False(kugou.NeedsQrLogin);
            Assert.False(kugou.HasStatusBadge);
            return;
        }

        Assert.True(kugou.NeedsQrLogin);
        Assert.True(kugou.HasStatusBadge);
        Assert.Equal("需扫码", kugou.StatusBadge);

        vm.SelectedLanguage = "en";
        Assert.Equal("QR required", kugou.StatusBadge);

        kugou.Enabled = false;
        await vm.ApplyMusicProviderOptionsAsync();
        Assert.False(kugou.NeedsQrLogin);
        Assert.False(kugou.HasStatusBadge);

        kugou.Enabled = true;
        await vm.ApplyMusicProviderOptionsAsync();
        Assert.True(kugou.NeedsQrLogin);

        // Off-thread credential update clears the badge cleanly
        await Task.Run(() => accounts.SetKugouCookieAsync("token=abc; userid=42"));
        Assert.False(kugou.NeedsQrLogin);
        Assert.False(kugou.HasStatusBadge);

        await accounts.SetKugouCookieAsync(null);
        Assert.True(kugou.NeedsQrLogin);
        Assert.True(kugou.HasStatusBadge);
    }

    [Fact]
    public void StatusBarPrompt_TriggersOnceForOnlineTrackFailure_IgnoresNonMusic_AndHidesOnRecoveryBlock()
    {
        var original = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var recoverySubject = new Subject<PlaybackRecoveryNotice>();
        using var vm = CreateMainVm(out var audio, out _, out _, recoverySubject);
        try
        {
            // Live radio and podcast failures must never suggest signing in to Kugou
            audio.SetupGet(a => a.CurrentTrack).Returns(new Track
            {
                SourceId = RadioStation.SourcePrefix + "station-1",
                Title = "FM 99"
            });
            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip", Array.Empty<string>()));
            Assert.False(vm.IsKugouLoginPromptVisible);

            audio.SetupGet(a => a.CurrentTrack).Returns(new Track
            {
                SourceId = PodcastFeed.SourcePrefix + "ep-1",
                Title = "Episode 1"
            });
            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip", Array.Empty<string>()));
            Assert.False(vm.IsKugouLoginPromptVisible);

            audio.SetupGet(a => a.CurrentTrack).Returns(new Track
            {
                SourceId = "",
                FilePath = "C:\\Music\\broken.mp3",
                Title = "Local Broken"
            });
            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip", Array.Empty<string>()));
            Assert.False(vm.IsKugouLoginPromptVisible);

            // Ordinary online music track triggers prompt (in full build) without Error lamp
            audio.SetupGet(a => a.CurrentTrack).Returns(new Track
            {
                SourceId = "netease:1001",
                Title = "Song A"
            });
            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip", Array.Empty<string>()));
            Assert.Equal(vm.SettingsVM.HasExperimentalProviders, vm.IsKugouLoginPromptVisible);
            Assert.Equal(vm.SettingsVM.HasExperimentalProviders, vm.ShowKugouLoginPrompt);
            Assert.False(vm.HasPlaybackRecoveryFailure);
            Assert.NotEqual(RadioState.Error, vm.CurrentState);

            if (!vm.SettingsVM.HasExperimentalProviders)
                return;

            // Real blocking recovery failure takes precedence over the guidance prompt
            recoverySubject.OnNext(new PlaybackRecoveryNotice(true, 3, "blocked", Array.Empty<string>()));
            Assert.True(vm.HasPlaybackRecoveryFailure);
            Assert.False(vm.ShowKugouLoginPrompt);

            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 0, string.Empty, Array.Empty<string>()));
            Assert.False(vm.HasPlaybackRecoveryFailure);
            Assert.True(vm.ShowKugouLoginPrompt);

            // Dismissing suppresses further prompts for the remainder of the session
            vm.DismissKugouLoginPromptCommand.Execute().Subscribe();
            Assert.False(vm.IsKugouLoginPromptVisible);
            Assert.False(vm.ShowKugouLoginPrompt);

            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip-again", Array.Empty<string>()));
            Assert.False(vm.IsKugouLoginPromptVisible);
        }
        finally
        {
            RxApp.MainThreadScheduler = original;
        }
    }

    [Fact]
    public async Task StatusBarPrompt_ResolveFailureAndNavigationAndAutoHideOnLogin()
    {
        var original = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            // 1) Disabled Kugou provider does not trigger prompt
            using (var vmDisabled = CreateMainVm(out _, out _, out var broker1))
            {
                broker1.Setup(b => b.ResolveTrackAsync(It.IsAny<OnlineTrack>(), true, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ResolveTrackResult?)null);
                var kugouOption = vmDisabled.SettingsVM.MusicProviders.Single(p => p.Id == "Kugou");
                kugouOption.Enabled = false;
                await vmDisabled.SettingsVM.ApplyMusicProviderOptionsAsync();

                var result = await vmDisabled.ResolveTrackUrlAsync(
                    new Track { SourceId = "netease:1", Title = "VIP Track" },
                    requireAlternative: false,
                    CancellationToken.None);
                Assert.Null(result);
                Assert.False(vmDisabled.IsKugouLoginPromptVisible);
            }

            // 2) Enabled Kugou + signed out -> ResolveTrackUrlAsync failure triggers prompt; OpenMusicAccountSettings navigates & dismisses
            using (var vmNav = CreateMainVm(out _, out _, out var broker2))
            {
                broker2.Setup(b => b.ResolveTrackAsync(It.IsAny<OnlineTrack>(), true, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ResolveTrackResult?)null);

                await vmNav.ResolveTrackUrlAsync(
                    new Track { SourceId = "netease:2", Title = "VIP Track" },
                    requireAlternative: false,
                    CancellationToken.None);

                Assert.Equal(vmNav.SettingsVM.HasExperimentalProviders, vmNav.IsKugouLoginPromptVisible);
                vmNav.IsSearchOpen = true;
                vmNav.OpenMusicAccountSettingsCommand.Execute().Subscribe();

                Assert.False(vmNav.IsSearchOpen);
                Assert.True(vmNav.IsSettingsOpen);
                Assert.Equal(1, vmNav.SettingsVM.TabIndex);
                Assert.False(vmNav.IsKugouLoginPromptVisible);
            }

            // 3) Signing in to Kugou automatically hides an active prompt
            using (var vmLogin = CreateMainVm(out _, out var accounts3, out var broker3))
            {
                broker3.Setup(b => b.ResolveTrackAsync(It.IsAny<OnlineTrack>(), true, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ResolveTrackResult?)null);

                await vmLogin.ResolveTrackUrlAsync(
                    new Track { SourceId = "netease:3", Title = "VIP Track" },
                    requireAlternative: false,
                    CancellationToken.None);

                if (vmLogin.SettingsVM.HasExperimentalProviders)
                {
                    Assert.True(vmLogin.IsKugouLoginPromptVisible);
                    await accounts3.SetKugouCookieAsync("token=xyz; userid=99");
                    Assert.False(vmLogin.IsKugouLoginPromptVisible);
                }
            }

            // 4) Disabling Kugou provider in Settings hides an active prompt
            using (var vmDisableAfterPrompt = CreateMainVm(out _, out _, out var broker4))
            {
                broker4.Setup(b => b.ResolveTrackAsync(It.IsAny<OnlineTrack>(), true, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((ResolveTrackResult?)null);

                await vmDisableAfterPrompt.ResolveTrackUrlAsync(
                    new Track { SourceId = "netease:4", Title = "VIP Track" },
                    requireAlternative: false,
                    CancellationToken.None);

                if (vmDisableAfterPrompt.SettingsVM.HasExperimentalProviders)
                {
                    Assert.True(vmDisableAfterPrompt.IsKugouLoginPromptVisible);
                    var kugouOption = vmDisableAfterPrompt.SettingsVM.MusicProviders.Single(p => p.Id == "Kugou");
                    kugouOption.Enabled = false;
                    await vmDisableAfterPrompt.SettingsVM.ApplyMusicProviderOptionsAsync();
                    Assert.False(vmDisableAfterPrompt.IsKugouLoginPromptVisible);
                }
            }
        }
        finally
        {
            RxApp.MainThreadScheduler = original;
        }
    }

    [Fact]
    public async Task SearchDrawer_KugouNotSignedInHint_OnlyShowsForKugou_SurvivesSlowSearch_AndClearsOnLogin()
    {
        var original = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        using var vm = CreateMainVm(out _, out var accounts, out var broker);
        try
        {
            var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            broker.Setup(b => b.SearchFastWithReportAsync("alpha", 20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchOutcome(
                    new List<OnlineTrack>(),
                    new[]
                    {
                        new SourceSearchStatus("网易云音乐", "ok", 0, null),
                        new SourceSearchStatus("酷狗音乐", "failed", 0, "未登录", FailureKind: MusicSourceFailureKind.NotSignedIn)
                    }));
            broker.Setup(b => b.SearchSlowWithReportAsync("alpha", 20, It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    slowStarted.TrySetResult();
                    await releaseSlow.Task;
                    return new SearchOutcome(
                        new List<OnlineTrack>(),
                        new[] { new SourceSearchStatus("YouTube", "ok", 0, null) });
                });

            vm.PlaylistVM.SearchText = "alpha";
            await vm.PlaylistVM.SearchCommand.Execute().ToTask();
            Assert.True(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseSlow.SetResult();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline &&
                   !vm.PlaylistVM.SearchStatusMessage.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(10);
            }
            Assert.Contains("YouTube", vm.PlaylistVM.SearchStatusMessage, StringComparison.OrdinalIgnoreCase);

            // Slow search update must not overwrite the Kugou NotSignedIn hint
            Assert.True(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            // Non-report status updates (such as playing an online result) clear the Kugou hint
            var hit = new OnlineTrack { Id = "netease:88", Title = "Gamma", Artist = "A", Source = "网易" };
            broker.Setup(b => b.SearchFastWithReportAsync("gamma", 20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchOutcome(
                    new List<OnlineTrack> { hit },
                    new[]
                    {
                        new SourceSearchStatus("酷狗音乐", "failed", 0, "未登录", FailureKind: MusicSourceFailureKind.NotSignedIn)
                    }));
            vm.PlaylistVM.SearchText = "gamma";
            await vm.PlaylistVM.SearchCommand.Execute().ToTask();
            Assert.True(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            vm.PlaylistVM.PlayOnlineCommand.Execute(hit).Subscribe();
            Assert.False(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            // Signing in to Kugou clears the search hint
            broker.Setup(b => b.SearchFastWithReportAsync("delta", 20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchOutcome(
                    new List<OnlineTrack>(),
                    new[]
                    {
                        new SourceSearchStatus("酷狗音乐", "failed", 0, "未登录", FailureKind: MusicSourceFailureKind.NotSignedIn)
                    }));
            vm.PlaylistVM.SearchText = "delta";
            await vm.PlaylistVM.SearchCommand.Execute().ToTask();
            Assert.True(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            await accounts.SetKugouCookieAsync("token=abc; userid=7");
            Assert.False(vm.PlaylistVM.HasKugouNotSignedInSearchHint);

            // OpenSubsonic NotSignedIn uses server credentials (not QR login) so must not show the Kugou QR shortcut
            broker.Setup(b => b.SearchFastWithReportAsync("beta", 20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchOutcome(
                    new List<OnlineTrack> { new() { Id = "netease:9", Title = "Beta", Artist = "A", Source = "网易" } },
                    new[]
                    {
                        new SourceSearchStatus("OpenSubsonic", "failed", 0, "auth", FailureKind: MusicSourceFailureKind.NotSignedIn)
                    }));
            vm.PlaylistVM.SearchText = "beta";
            await vm.PlaylistVM.SearchCommand.Execute().ToTask();
            Assert.False(vm.PlaylistVM.HasKugouNotSignedInSearchHint);
        }
        finally
        {
            RxApp.MainThreadScheduler = original;
        }
    }

    [AvaloniaFact]
    public async Task StatusBarAndSearchDrawer_UiControlsBindAndNavigateEndToEnd()
    {
        var original = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var recoverySubject = new Subject<PlaybackRecoveryNotice>();
        using var vm = CreateMainVm(out var audio, out _, out var broker, recoverySubject);
        Window? window = null;
        var theme = new FluentTheme();
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm, Width = 960, Height = 720 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // SearchDrawer shortcut button appears on Kugou NotSignedIn and jumps to Settings page 1
            broker.Setup(b => b.SearchFastWithReportAsync("kugou-test", 20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SearchOutcome(
                    new List<OnlineTrack> { new() { Id = "netease:11", Title = "Test", Artist = "Artist", Source = "网易" } },
                    new[]
                    {
                        new SourceSearchStatus("酷狗音乐", "failed", 0, "未登录", FailureKind: MusicSourceFailureKind.NotSignedIn)
                    }));
            vm.OpenSearchCommand.Execute().Subscribe();
            vm.PlaylistVM.SearchText = "kugou-test";
            await vm.PlaylistVM.SearchCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();

            var searchDrawer = window.FindDescendantOfType<SearchDrawer>()
                ?? throw new InvalidOperationException("SearchDrawer not found");
            var searchLoginBtn = searchDrawer.FindControl<Button>("KugouSearchLoginButton")
                ?? throw new InvalidOperationException("KugouSearchLoginButton not found");
            Assert.True(searchLoginBtn.IsVisible);

            searchLoginBtn.Command!.Execute(searchLoginBtn.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSearchOpen);
            Assert.True(vm.IsSettingsOpen);
            Assert.Equal(1, vm.SettingsVM.TabIndex);

            // StatusBar prompt fits cleanly in the 28px bar and navigates on click
            vm.IsSettingsOpen = false;
            audio.SetupGet(a => a.CurrentTrack).Returns(new Track { SourceId = "netease:22", Title = "Track 22" });
            recoverySubject.OnNext(new PlaybackRecoveryNotice(false, 1, "skip", Array.Empty<string>()));
            Dispatcher.UIThread.RunJobs();

            var statusBar = window.FindDescendantOfType<StatusBar>()
                ?? throw new InvalidOperationException("StatusBar not found");
            var promptBar = statusBar.FindControl<Grid>("KugouLoginPromptBar")
                ?? throw new InvalidOperationException("KugouLoginPromptBar not found");
            var actionBtn = statusBar.FindControl<Button>("KugouLoginPromptActionButton")
                ?? throw new InvalidOperationException("KugouLoginPromptActionButton not found");

            Assert.Equal(vm.SettingsVM.HasExperimentalProviders, promptBar.IsVisible);
            if (vm.SettingsVM.HasExperimentalProviders)
            {
                Assert.True(statusBar.Bounds.Height <= 28.5);
                Assert.True(actionBtn.Bounds.Height <= statusBar.Bounds.Height + 0.5);

                actionBtn.Command!.Execute(actionBtn.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsSettingsOpen);
                Assert.Equal(1, vm.SettingsVM.TabIndex);
                Assert.False(promptBar.IsVisible);
            }
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            RxApp.MainThreadScheduler = original;
        }
    }
}
