using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.WebRemote;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WebRemoteViewModelTests
{
    private sealed class StubWebRemoteService : IWebRemoteService
    {
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public int ConnectedClientCount { get; set; }
        public string PrimaryUrl { get; private set; } = string.Empty;
        public IReadOnlyList<string> AccessUrls => string.IsNullOrEmpty(PrimaryUrl) ? Array.Empty<string>() : [PrimaryUrl];
        public string AccessToken { get; private set; } = string.Empty;
        public string LastError { get; private set; } = string.Empty;
        public int NotifyCount { get; private set; }

        public Func<WebRemoteSnapshot>? SnapshotProvider { get; private set; }
        public Func<WebRemoteCommand, Task<WebRemoteCommandResult>>? CommandHandler { get; private set; }

        public event Action? StatusChanged;

        public void ConfigureHandlers(
            Func<WebRemoteSnapshot> snapshotProvider,
            Func<WebRemoteCommand, Task<WebRemoteCommandResult>> commandHandler)
        {
            SnapshotProvider = snapshotProvider;
            CommandHandler = commandHandler;
        }

        public Task<bool> StartAsync(int preferredPort, string token, IPAddress? bindAddress = null, CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            Port = preferredPort <= 0 ? 37260 : preferredPort;
            AccessToken = token;
            PrimaryUrl = $"http://192.168.1.55:{Port}/?token={Uri.EscapeDataString(token)}";
            LastError = string.Empty;
            StatusChanged?.Invoke();
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            IsRunning = false;
            Port = 0;
            PrimaryUrl = string.Empty;
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task RotateTokenAsync(string newToken)
        {
            AccessToken = newToken;
            if (IsRunning)
                PrimaryUrl = $"http://192.168.1.55:{Port}/?token={Uri.EscapeDataString(newToken)}";
            StatusChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void NotifyStateChanged(bool immediate = false) => NotifyCount++;

        public void Dispose() => IsRunning = false;
    }

    [AvaloniaFact]
    public async Task ToggleWebRemote_StartsStopsRotatesToken_AndDispatchesCommandsOnMainThread()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var originalLanguage = AppLanguage.Current;

        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-webremote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var audioMock = new Mock<IAudioService>();
        var currentTrack = new Track
        {
            Id = "song-1",
            SourceId = "netease:1",
            Title = "Test Track",
            Artist = "Test Artist",
            Duration = TimeSpan.FromMinutes(3)
        };
        var currentTrackSubj = new BehaviorSubject<Track?>(currentTrack);
        audioMock.SetupGet(x => x.CurrentTrack).Returns(() => currentTrackSubj.Value);
        audioMock.SetupGet(x => x.TrackChanged).Returns(currentTrackSubj);
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.PlaybackRecoveryNotices).Returns(Observable.Never<PlaybackRecoveryNotice>());
        audioMock.SetupGet(x => x.TrackMediaInfo).Returns(Observable.Never<TrackMediaInfo>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(new[] { currentTrack });
        audioMock.SetupGet(x => x.RepeatMode).Returns("radio");
        audioMock.SetupGet(x => x.Duration).Returns(TimeSpan.FromMinutes(3));
        audioMock.SetupProperty(x => x.Volume, 0.8f);

        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.DjName).Returns("Haru");
        djMock.SetupGet(x => x.IsLlmConfigured).Returns(true);
        var chatCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        djMock.Setup(x => x.GenerateChatResponseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => chatCalled.TrySetResult(true))
            .ReturnsAsync("Got it, playing next!");
        djMock.Setup(x => x.GenerateChatResponseAsync(It.IsAny<string>()))
            .Callback(() => chatCalled.TrySetResult(true))
            .ReturnsAsync("Got it, playing next!");

        var llmMock = new Mock<ILLMService>();
        var secureMock = new Mock<ISecureStorage>();
        var searchMock = new Mock<IMusicSearchService>();
        var sttMock = new Mock<ISttService>();
        var webRemote = new StubWebRemoteService();

        var vm = new MainWindowViewModel(
            audioMock.Object,
            djMock.Object,
            llmMock.Object,
            secureMock.Object,
            searchMock.Object,
            sttMock.Object,
            Path.Combine(tempDir, "playlist.json"),
            Path.Combine(tempDir, "settings.json"),
            webRemoteService: webRemote);

        try
        {
            // 默认关闭
            Assert.False(vm.IsWebRemoteRunning);
            Assert.Null(vm.WebRemoteQrImage);
            Assert.Equal(string.Empty, vm.WebRemoteUrl);

            // 开启开关 -> 服务启动、生成二维码并更新 URL
            await vm.ToggleWebRemoteCommand!.Execute();
            Assert.True(vm.IsWebRemoteRunning);
            Assert.True(vm.SettingsVM.WebRemoteEnabled);
            Assert.NotNull(vm.WebRemoteQrImage);
            Assert.Contains("http://192.168.1.55:37260/?token=", vm.WebRemoteUrl);

            var oldToken = webRemote.AccessToken;
            var snap = webRemote.SnapshotProvider!.Invoke();
            Assert.Equal("Test Track", snap.Title);
            Assert.Equal("Test Artist", snap.Artist);
            Assert.True(snap.CanSeek);

            // 刷新令牌 -> 旧令牌替换、二维码链接更新
            await vm.RefreshWebRemoteTokenCommand!.Execute();
            Assert.NotEqual(oldToken, webRemote.AccessToken);
            Assert.Contains(webRemote.AccessToken, vm.WebRemoteUrl);

            // 派发核心遥控命令
            var playRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_play" });
            Assert.True(playRes.Ok);
            audioMock.Verify(x => x.Play(), Times.Once);

            var nextRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "next" });
            Assert.True(nextRes.Ok);
            audioMock.Verify(x => x.Next(), Times.Once);

            var volRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "set_volume", Value = 0.45 });
            Assert.True(volRes.Ok);
            Assert.Equal(0.45f, audioMock.Object.Volume, 2);

            var seekRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek", Value = 42.0 });
            Assert.True(seekRes.Ok);
            audioMock.Verify(x => x.Seek(TimeSpan.FromSeconds(42.0)), Times.Once);

            var focusRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_focus_mode" });
            Assert.True(focusRes.Ok);
            Assert.True(vm.IsFocusRadioMode);

            var sleepRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "set_sleep_timer", IntValue = 15 });
            Assert.True(sleepRes.Ok);
            Assert.True(vm.IsSleepTimerActive);

            var chatRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "chat", Text = "hello dj" });
            Assert.True(chatRes.Ok);
            await chatCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            djMock.Verify(x => x.GenerateChatResponseAsync("hello dj", It.IsAny<CancellationToken>()), Times.AtLeastOnce());

            // 直播流禁止 Seek
            currentTrackSubj.OnNext(new Track
            {
                Id = "live-1",
                SourceId = "radio-browser:abc",
                Title = "Jazz FM",
                FilePath = "https://stream.example.com/jazz.mp3"
            });
            var liveSeekRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "seek", Value = 10 });
            Assert.False(liveSeekRes.Ok);
            Assert.Equal("live_stream_not_seekable", liveSeekRes.Message);

            // 播客不支持收藏与音乐 Like 反馈
            currentTrackSubj.OnNext(new Track
            {
                Id = "podcast:ep-1",
                SourceId = "podcast:ep-1",
                Title = "Podcast Ep 1",
                FilePath = "https://cdn.example.com/ep1.mp3"
            });
            var podFavRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "toggle_favorite" });
            Assert.False(podFavRes.Ok);
            Assert.Equal("podcast_favorite_unsupported", podFavRes.Message);
            var podLikeRes = await webRemote.CommandHandler!(new WebRemoteCommand { Action = "feedback", Text = "like" });
            Assert.False(podLikeRes.Ok);
            Assert.Equal("podcast_favorite_unsupported", podLikeRes.Message);

            // 再次点击关闭遥控
            await vm.ToggleWebRemoteCommand!.Execute();
            Assert.False(vm.IsWebRemoteRunning);
            Assert.False(vm.SettingsVM.WebRemoteEnabled);
            Assert.Null(vm.WebRemoteQrImage);
        }
        finally
        {
            vm.Dispose();
            AppLanguage.Apply(originalLanguage);
            RxApp.MainThreadScheduler = originalScheduler;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [AvaloniaFact]
    public void MainWindowAndSettingsView_ExposeWebRemoteControls_WithExpectedBindings()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        var theme = new FluentTheme();
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var headerBtn = window.FindControl<Button>("WebRemoteButton");
            Assert.NotNull(headerBtn);

            vm.OpenWebRemoteSettingsCommand!.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsSettingsOpen);
            Assert.Equal(3, vm.SettingsVM.TabIndex);

            var settingsView = window.FindDescendantOfType<SettingsView>();
            Assert.NotNull(settingsView);
            var portBox = settingsView!.FindControl<TextBox>("WebRemotePortBox");
            Assert.NotNull(portBox);
            Assert.Equal("37260", portBox!.Text);
            var statusBlock = settingsView.FindControl<TextBlock>("SettingsWebRemoteStatus");
            Assert.NotNull(statusBlock);
        }
        finally
        {
            window?.Close();
            app.Styles.Remove(theme);
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task SettingsViewModel_PersistsWebRemoteConfig_AcrossReload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-wr-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var secure = new Mock<ISecureStorage>();
            using (var s1 = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                secure.Object,
                settingsPath))
            {
                await s1.LoadAsync();
                Assert.False(s1.WebRemoteEnabled);
                Assert.Equal(37260, s1.WebRemotePort);
                Assert.False(string.IsNullOrWhiteSpace(s1.WebRemoteToken));

                s1.WebRemoteEnabled = true;
                s1.WebRemotePortText = "38888";
                s1.WebRemoteToken = "CustomPinnedToken_99";
                await s1.SaveCommand.Execute();
                Assert.Equal(38888, s1.WebRemotePort);
            }

            using (var s2 = new SettingsViewModel(
                new Mock<ILLMService>().Object,
                secure.Object,
                settingsPath))
            {
                await s2.LoadAsync();
                Assert.True(s2.WebRemoteEnabled);
                Assert.Equal(38888, s2.WebRemotePort);
                Assert.Equal("38888", s2.WebRemotePortText);
                Assert.Equal("CustomPinnedToken_99", s2.WebRemoteToken);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
