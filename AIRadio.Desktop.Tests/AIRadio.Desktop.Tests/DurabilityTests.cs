using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 耐久性回归：把「发布前人工验证清单」中可自动化的部分固化为循环/并发压力——
/// 电台连续续播、自然结束并发单飞、真实多曲连播、画像跨会话积累、
/// 播放列表混合并发写盘、聊天长对话画像注入（docs/plans：P3 自动化耐久测试）。
/// 人工部分（无输出设备/睡眠唤醒/进程内存观察）仍需真人执行。
/// </summary>
public class DurabilityTests
{
    private static string CreateTempFile(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests.Durability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    private static Track MakeTrack(string sourceId) => new()
    {
        Id = sourceId,
        SourceId = sourceId,
        Title = $"歌{sourceId}",
        Artist = $"歌手{sourceId}",
        FilePath = $"http://example.com/{sourceId}.mp3"
    };

    private static Mock<IAudioService> CreateAudioMock(List<Track> playlist, Func<Track?> currentTrack)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => playlist.AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns(currentTrack);
        audio.Setup(x => x.RepeatMode).Returns("radio");
        audio.Setup(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()))
            .Callback<IEnumerable<Track>>(tracks => playlist.AddRange(tracks));
        return audio;
    }

    private static readonly MethodInfo AutoRadioMethod = typeof(MainWindowViewModel)
        .GetMethod("HandleAutoRadioTrackEndedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Task InvokeAutoRadioAsync(MainWindowViewModel vm, Track current)
        => (Task)AutoRadioMethod.Invoke(vm, new object[] { current })!;

    private static (MainWindowViewModel Vm, Func<Track?> Current, Mock<IRecommendationService> Rec, IDisposable Life) CreateRadioVm()
    {
        var playlist = new List<Track>();
        // 同一局部变量被两个闭包共享：current() 读、PlayTrack 回调写（模拟真实 TrackChanged 推进）
        var currentTrack = MakeTrack("seed");
        var audio = CreateAudioMock(playlist, () => currentTrack);
        audio.Setup(x => x.PlayTrack(It.IsAny<Track>()))
            .Callback<Track>(t => currentTrack = t);

        var dj = new Mock<IDJService>();
        dj.SetupGet(x => x.TtsEnabled).Returns(false);
        dj.Setup(x => x.GenerateTrackIntroductionAsync(It.IsAny<Track>(), It.IsAny<Track>()))
            .ReturnsAsync(new DJScript { Text = "串场", Expression = "smile", Motion = "wave" });

        var counter = 0;
        var rec = new Mock<IRecommendationService>();
        rec.SetupGet(x => x.CurrentProgram).Returns((RadioProgram?)null);
        rec.SetupGet(x => x.RecentlyPlayed).Returns(Array.Empty<Track>());
        rec.SetupGet(x => x.FeedbackHistory).Returns(Array.Empty<UserMusicFeedback>());
        rec.Setup(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()))
            .ReturnsAsync(() => MakeTrack($"radio-{Interlocked.Increment(ref counter)}"));

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            CreateTempFile("playlist.json"),
            CreateTempFile("settings.json"),
            recommendationService: rec.Object);

        return (vm, () => currentTrack, rec, new DisposableAction(vm.Dispose));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public async Task RadioAutoAdvance_ThirtyConsecutiveEndings_KeepsPlaylistConsistent()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, current, rec, life) = CreateRadioVm();
        try
        {
            vm.PlaylistVM.AddExternalTrack(MakeTrack("seed"));

            for (var i = 1; i <= 30; i++)
            {
                await InvokeAutoRadioAsync(vm, current()!);
                var id = $"radio-{i}";
                Assert.Equal(1, vm.PlaylistVM.Tracks.Count(t => t.SourceId == id));
            }

            Assert.Equal(31, vm.PlaylistVM.Tracks.Count);
            Assert.Equal("radio-30", current()!.SourceId);
            // 30 轮后推荐管线仍健康（未被中途击穿成 DJ 兜底）
            rec.Verify(x => x.GetNextTrackAsync(It.IsAny<RecommendationRequest>()), Times.Exactly(30));
        }
        finally
        {
            life.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public async Task AutoAdvance_ConcurrentEndingsAreSingleFlight_NoCorruptionAcrossVolleys()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, current, _, life) = CreateRadioVm();
        try
        {
            vm.PlaylistVM.AddExternalTrack(MakeTrack("seed"));

            for (var volley = 0; volley < 8; volley++)
            {
                var trackAtVolley = current()!;
                var before = vm.PlaylistVM.Tracks.Count;
                // 同一曲的并发自然结束：单飞守卫只放行一个，其余立即返回
                await Task.WhenAll(Enumerable.Range(0, 6)
                    .Select(_ => InvokeAutoRadioAsync(vm, trackAtVolley)));
                // 至少推进一首且不会有 6 份重复（丢弃路径不产生 playlist 副本）
                var after = vm.PlaylistVM.Tracks.Count;
                Assert.InRange(after - before, 0, 3);
            }

            // 全程无重复身份、当前曲始终有效
            var ids = vm.PlaylistVM.Tracks.Select(t => t.SourceId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.StartsWith("radio-", current()!.SourceId);
        }
        finally
        {
            life.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>生成指定毫秒数的静音 WAV（16bit 单声道 8kHz），供真实播放链路耐久使用。</summary>
    private static string WriteSilenceWav(string path, int durationMs)
    {
        const int rate = 8000;
        var dataLen = (int)(rate * durationMs / 1000.0) * 2;
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLen);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLen);
        writer.Write(new byte[dataLen]);
        return path;
    }

    [Fact]
    public async Task RealPlayback_FourSilenceTracks_AdvanceThroughAll()
    {
        // 真实 LibVLC 链路：加载 4 首静音短曲，自然结束逐首推进到末尾。
        // 覆盖「连续播放」人工清单的可自动化部分（设备异常/睡眠唤醒仍需人工）
        var dir = Path.GetDirectoryName(CreateTempFile("placeholder.json"))!;
        var tracks = Enumerable.Range(0, 4)
            .Select(i => Track.FromFile(WriteSilenceWav(Path.Combine(dir, $"silence-{i}.wav"), 500)))
            .ToList();

        var audio = new AudioService();
        try
        {
            var endCount = 0;
            var nextIndex = 0;
            var allPlayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            audio.TrackEnded.Subscribe(_ =>
            {
                if (Interlocked.Increment(ref endCount) >= 4)
                {
                    allPlayed.TrySetResult(true);
                    return;
                }
                var index = Interlocked.Increment(ref nextIndex);
                try
                {
                    audio.PlayAtIndex(index);
                }
                catch (Exception ex)
                {
                    allPlayed.TrySetException(ex);
                }
            });

            audio.LoadTracks(tracks);
            audio.PlayAtIndex(0);

            await allPlayed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(4, endCount);
        }
        finally
        {
            audio.Dispose();
        }
    }

    [Fact]
    public async Task ListeningProfile_ThirtySessionRoundtrips_FileStaysValidAndCounterMonotonic()
    {
        var profileFile = CreateTempFile("listener-profile.json");
        var llm = new Mock<ILLMService>();
        var now = new DateTime(2026, 9, 17, 12, 0, 0);
        long expectedTotal = 0;

        try
        {
            for (var session = 1; session <= 30; session++)
            {
                var service = new ListeningProfileService(llm.Object, profileFile, () => now);
                await service.LoadAsync(CancellationToken.None);

                expectedTotal += 3;
                for (var e = 0; e < 3; e++)
                    service.RecordEvent(new ListeningEventData
                    {
                        Type = ListeningEventType.Played,
                        Title = $"歌{session}-{e}",
                        Artist = $"歌手{session % 5}",
                        Time = now.AddMinutes(e),
                    });
                await service.FlushAsync(CancellationToken.None);
                service.Dispose();

                // 每个会话结束后文件必须可解析且计数单调
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(profileFile));
                Assert.Equal(expectedTotal, doc.RootElement.GetProperty("TotalEventsIngested").GetInt64());
            }

            var finalService = new ListeningProfileService(llm.Object, profileFile, () => now);
            await finalService.LoadAsync(CancellationToken.None);
            Assert.Equal(expectedTotal, finalService.GetSnapshot().TotalEventsIngested);
            finalService.Dispose();
        }
        finally
        {
            if (File.Exists(profileFile))
                File.Delete(profileFile);
        }
    }

    [Fact]
    public async Task Playlist_MixedConcurrentAddsAndSaves_FileAlwaysParseable()
    {
        var playlistFile = CreateTempFile("playlist.json");
        var playlist = new List<Track>();
        Func<Track?> current = () => null;
        var audio = CreateAudioMock(playlist, current);
        var vm = new PlaylistViewModel(
            audio.Object,
            new Mock<IMusicSearchService>().Object,
            playlistFile);

        try
        {
            for (var round = 0; round < 15; round++)
            {
                var batch = Enumerable.Range(0, 2)
                    .Select(i => MakeTrack($"mix-{round}-{i}"))
                    .ToList();
                // AddExternalTrack 是 UI 亲和 API（生产链路经 Dispatcher.UIThread.Post 编组，
                // 见 MainWindowViewModel.GetNextTrackForAudioServiceAsync）：同线程顺序加入，
                // 落盘并发压力（直接保存 ×2 + AddExternalTrack 内部的 fire-and-forget 保存）
                vm.AddExternalTrack(batch[0]);
                vm.AddExternalTrack(batch[1]);
                await Task.WhenAll(vm.SaveAsync(), vm.SaveAsync());

                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(playlistFile));
                Assert.True(doc.RootElement.TryGetProperty("Tracks", out _));
            }

            Assert.Equal(30, vm.Tracks.Count);
            var ids = vm.Tracks.Select(t => t.SourceId).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
        finally
        {
            vm.Dispose();
        }
    }

    private sealed class FakeProfile : IListeningProfileService
    {
        public ListenerProfileSnapshot Snapshot { get; set; } = ListenerProfileSnapshot.Empty;
        public bool Enabled { get; set; } = true;

        public ListenerProfileSnapshot GetSnapshot() => Enabled ? Snapshot : ListenerProfileSnapshot.Empty;
        public void RecordEvent(ListeningEventData eventData) { }
        public void NotifyPlaybackStarted(Track track) { }
        public void NotifyPlaybackEndedNaturally(Track track) { }
        public void NotifyPositionSampled(Track track, TimeSpan position) { }
        public void NotifyTrackSwitched(Track? next) { }
        public void NotifyPlaybackPaused() { }
        public void Reset() { }
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshDigestAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task ChatLongConversation_SixtyRoundsWithProfile_HistoryCappedAndInjectionStable()
    {
        var llm = new Mock<ILLMService>();
        var captured = new List<List<ChatMessage>>();
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("回复[calm]")
            .Callback<string, List<ChatMessage>>((_, history) => captured.Add(history.ToList()));

        var profile = new FakeProfile
        {
            Snapshot = new ListenerProfileSnapshot
            {
                TotalEventsIngested = ListeningProfileService.InjectionMinEvents,
                TopArtists = new[]
                {
                    new ArtistAffinity { Artist = "周杰伦", Score = 5, PlayCount = 10 },
                    new ArtistAffinity { Artist = "陈绮贞", Score = 3, PlayCount = 6 },
                    new ArtistAffinity { Artist = "五月天", Score = 2, PlayCount = 4 },
                },
                DislikeBlacklist = new[] { new ProfileMusicRef("不爱听", "某人") },
                Digest = new TasteDigestData { Text = "偏爱华语流行", Language = "zh" },
            },
        };
        var dj = new DJService(llm.Object, profile: profile);
        dj.Initialize(new DJProfile { Name = "小音", SystemPrompt = "测试人设", Language = "zh" });

        for (var round = 1; round <= 60; round++)
        {
            var response = await dj.GenerateChatResponseAsync($"第{round}句");
            Assert.Equal("回复[calm]", response);

            var history = captured[^1];
            // 历史裁剪恒定在上限（system + 10 对；首轮仅 system，用户消息是独立参数）
            Assert.InRange(history.Count, 1, 21);
            Assert.Equal(2, history[0].Content.Split("听众长期口味").Length);
            Assert.Contains("偏爱华语流行", history[0].Content);
        }

        // 满长对话后仍在裁剪上限内
        Assert.Equal(21, captured[^1].Count);
    }
}
