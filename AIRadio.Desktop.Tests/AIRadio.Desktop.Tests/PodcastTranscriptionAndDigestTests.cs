using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
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

public sealed class PodcastTranscriptionAndDigestTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalLanguage;

    public PodcastTranscriptionAndDigestTests()
    {
        _originalLanguage = AppLanguage.Current;
        AppLanguage.Apply("zh");
        _tempDir = Path.Combine(Path.GetTempPath(), $"tuanradio-podstt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        AppLanguage.Apply(_originalLanguage);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TranscribeExcerptAsync_BlocksUnsafeRedirectAndRespectsMaxBytesCap()
    {
        var sttMock = new Mock<ISttService>();
        sttMock.Setup(x => x.TranscribeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("今天聊聊本地语音识别与播客摘要。");

        // 1. 重定向到回环地址必须拦截
        var redirectHandler = new StubAudioHandler((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Redirect);
            resp.Headers.Location = new Uri("http://127.0.0.1:9000/secret.mp3");
            return Task.FromResult(resp);
        });
        using var redirectSvc = new PodcastTranscriptionService(
            sttMock.Object,
            new HttpClient(redirectHandler),
            MediaUriPolicy.ValidateCustomRadioAsync,
            (_, wav, _, _) =>
            {
                File.WriteAllBytes(wav, new byte[] { 1, 2, 3 });
                return Task.FromResult(true);
            });

        var unsafeEp = new PodcastEpisode
        {
            EpisodeKey = "ep-redir",
            Title = "Unsafe Redirect Ep",
            AudioUrl = "https://93.184.216.34/ep.mp3"
        };
        Assert.Equal(string.Empty, await redirectSvc.TranscribeExcerptAsync(unsafeEp));
        sttMock.Verify(x => x.TranscribeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2. 正常公网音频流超过 3MB 时截断读取、转码并传入推断语种，且不遗留临时文件
        string? capturedSourcePath = null;
        string? capturedWavPath = null;
        long capturedSourceSize = 0;
        var oversizedHandler = new StubAudioHandler((req, _) =>
        {
            Assert.NotNull(req.Headers.Range);
            var payload = new byte[PodcastTranscriptionService.MaxAudioExcerptBytes + 256 * 1024];
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(resp);
        });

        using var okSvc = new PodcastTranscriptionService(
            sttMock.Object,
            new HttpClient(oversizedHandler),
            (_, _) => Task.FromResult(true),
            (src, wav, maxSec, _) =>
            {
                capturedSourcePath = src;
                capturedWavPath = wav;
                capturedSourceSize = new FileInfo(src).Length;
                Assert.Equal(PodcastTranscriptionService.MaxExcerptSeconds, maxSec);
                File.WriteAllBytes(wav, new byte[] { 8, 9 });
                return Task.FromResult(true);
            });

        var zhEp = new PodcastEpisode
        {
            EpisodeKey = "ep-zh",
            FeedTitle = "硬核电台",
            Title = "第 10 期：语音转写",
            AudioUrl = "https://cdn.example.com/ep10.mp3"
        };
        var text = await okSvc.TranscribeExcerptAsync(zhEp, "zh");

        Assert.Equal("今天聊聊本地语音识别与播客摘要。", text);
        Assert.Equal(PodcastTranscriptionService.MaxAudioExcerptBytes, capturedSourceSize);
        Assert.NotNull(capturedSourcePath);
        Assert.NotNull(capturedWavPath);
        Assert.False(File.Exists(capturedSourcePath));
        Assert.False(File.Exists(capturedWavPath));
        sttMock.Verify(x => x.TranscribeAsync(capturedWavPath!, "zh", It.IsAny<CancellationToken>()), Times.Once);

        // 3. 纯英文标题且 feed 语种缺省为 zh 时，自动推断为 en
        var enEp = new PodcastEpisode
        {
            EpisodeKey = "ep-en",
            FeedTitle = "Changelog",
            Title = "Episode 100: Local AI Tools",
            AudioUrl = "https://cdn.example.com/ep100.mp3"
        };
        Assert.Equal("en", PodcastTranscriptionService.InferWhisperLanguage(enEp, "zh"));

        // 4. 服务端对 Range 返回 416 时自动退回全量流式读取（仍受 3MB 上限截断）
        var rangeAttempts = 0;
        var fallback416Handler = new StubAudioHandler((req, _) =>
        {
            rangeAttempts++;
            if (req.Headers.Range != null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(resp);
        });
        using var fallbackSvc = new PodcastTranscriptionService(
            sttMock.Object,
            new HttpClient(fallback416Handler),
            (_, _) => Task.FromResult(true),
            (_, wav, _, _) =>
            {
                File.WriteAllBytes(wav, new byte[] { 5, 6 });
                return Task.FromResult(true);
            });
        Assert.Equal("今天聊聊本地语音识别与播客摘要。", await fallbackSvc.TranscribeExcerptAsync(zhEp, "zh"));
        Assert.Equal(2, rangeAttempts);
    }

    [Fact]
    public async Task DjService_DigestWithTranscriptExcerpt_GeneratesPromptAndFallback()
    {
        var ep = new PodcastEpisode
        {
            EpisodeKey = "ep-no-notes",
            FeedTitle = "开源之声",
            Title = "第 7 期：无简介单集",
            Summary = string.Empty,
            AudioUrl = "https://cdn.example.com/ep7.mp3"
        };
        const string transcript = "欢迎收听本期节目，今天我们主要聊一聊桌面客户端如何用本地 Whisper 模型识别播客开场。";
        var profile = new DJProfile { Name = "小音", Language = "zh" };

        var prompt = DJService.BuildPodcastDigestPrompt(profile, ep, RadioSceneMode.Focus, transcript);
        Assert.Contains("音频开头转写片段", prompt);
        Assert.Contains("本地 Whisper 模型", prompt);
        Assert.Contains("[calm]", prompt);

        var fallback = DJService.BuildPodcastDigestFallback(profile, ep, RadioSceneMode.Default, transcript);
        Assert.Contains("音频开篇速览", fallback.Text);
        Assert.Contains("欢迎收听本期节目", fallback.Text);

        var llmMock = new Mock<ILLMService>();
        llmMock.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("本期节目开头主要介绍了如何用本地语音识别提炼播客看点。 [calm]");
        var dj = new DJService(llmMock.Object, new Mock<ITtsService>().Object);
        dj.Initialize(profile);

        var script = await dj.GeneratePodcastDigestAsync(ep, transcript);
        Assert.Equal("calm", script.Emotion);
        Assert.Contains("本地语音识别", script.Text);
    }

    [Fact]
    public async Task MainWindowViewModel_DigestEmptyShownotes_TranscribesCachesAndHandlesFailure()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.NaturalTrackCompleted).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());

        var sttPodMock = new Mock<IPodcastTranscriptionService>();
        sttPodMock.Setup(x => x.TranscribeExcerptAsync(
                It.Is<PodcastEpisode>(e => e.EpisodeKey == "ep-ok"),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("这是一段从音频开头转写出的文本。");
        sttPodMock.Setup(x => x.TranscribeExcerptAsync(
                It.Is<PodcastEpisode>(e => e.EpisodeKey == "ep-fail"),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var djMock = new Mock<IDJService>();
        djMock.SetupGet(x => x.DjName).Returns("小音");
        djMock.Setup(x => x.GeneratePodcastDigestAsync(
                It.Is<PodcastEpisode>(e => e.EpisodeKey == "ep-ok"),
                "这是一段从音频开头转写出的文本。",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DJScript { Text = "转写速读结果已就绪。", Emotion = "calm", Expression = "calm", Motion = "idle" });

        using var vm = new MainWindowViewModel(
            audioMock.Object,
            djMock.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(_tempDir, "playlist.json"),
            Path.Combine(_tempDir, "settings.json"),
            podcastTranscriptionService: sttPodMock.Object);

        try
        {
            var epOk = new PodcastEpisode
            {
                EpisodeKey = "ep-ok",
                Title = "No Shownotes Episode",
                Summary = string.Empty,
                AudioUrl = "https://cdn.example.com/ok.mp3"
            };

            // 第一次触发：转写 + 缓存 + 速读
            await vm.DigestPodcastEpisodeAsync(epOk);
            Assert.False(epOk.IsTranscribing);
            Assert.False(epOk.IsDigesting);
            Assert.Equal("这是一段从音频开头转写出的文本。", epOk.TranscriptExcerpt);
            Assert.Contains(vm.ChatVM.Messages, m => m.Content.Contains("转写速读结果已就绪。"));

            // 第二次触发：直接复用缓存，不重复转写
            await vm.DigestPodcastEpisodeAsync(epOk);
            sttPodMock.Verify(x => x.TranscribeExcerptAsync(epOk, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

            // 转写失败时给出明确提示
            var epFail = new PodcastEpisode
            {
                EpisodeKey = "ep-fail",
                Title = "Silent Episode",
                Summary = string.Empty,
                AudioUrl = "https://cdn.example.com/fail.mp3"
            };
            await vm.DigestPodcastEpisodeAsync(epFail);
            Assert.Contains(vm.ChatVM.Messages, m => m.Content == AppLanguage.Get("S_PodcastTranscribeFailed"));
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public void PodcastTab6_OpmlPanelSubTabs_SwitchAndExposeImportExportButtons()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;

        var audioMock = new Mock<IAudioService>();
        audioMock.SetupGet(x => x.TrackEnded).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.NaturalTrackCompleted).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.TrackChanged).Returns(Observable.Never<Track?>());
        audioMock.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audioMock.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
        audioMock.SetupGet(x => x.SpectrumData).Returns(Observable.Never<float[]>());
        audioMock.SetupGet(x => x.TtsStateChanged).Returns(Observable.Never<bool>());
        audioMock.SetupGet(x => x.TtsError).Returns(Observable.Never<string>());
        audioMock.SetupGet(x => x.Playlist).Returns(Array.Empty<Track>());

        using var storage = new PodcastStorage(Path.Combine(_tempDir, "podcasts.json"));
        using var vm = new MainWindowViewModel(
            audioMock.Object,
            new Mock<IDJService>().Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(_tempDir, "playlist.json"),
            Path.Combine(_tempDir, "settings.json"),
            podcastStorage: storage);

        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm, Width = 943, Height = 893 };
            var strings = new ResourceDictionary();
            foreach (var pair in AppLanguage.GetStringTable("zh"))
                strings[pair.Key] = pair.Value;
            window.Resources.MergedDictionaries.Add(strings);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.OpenPodcastsCommand.Execute().Subscribe();
            vm.PlaylistVM.OpenPodcastSubscribeCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            var drawer = window.GetVisualDescendants().OfType<PlaylistDrawer>().First();
            var importBtn = drawer.FindControl<Button>("ImportOpmlButton");
            var exportBtn = drawer.FindControl<Button>("ExportOpmlButton");
            Assert.NotNull(importBtn);
            Assert.NotNull(exportBtn);
            Assert.False(vm.PlaylistVM.IsPodcastOpmlMode);

            vm.PlaylistVM.SwitchToPodcastOpmlModeCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.PlaylistVM.IsPodcastOpmlMode);
            Assert.True(importBtn!.IsVisible);
            Assert.True(exportBtn!.IsVisible);
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private sealed class StubAudioHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubAudioHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }
}
