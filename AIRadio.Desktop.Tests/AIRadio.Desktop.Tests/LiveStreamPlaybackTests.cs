using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 直播电台流（Live Stream）播放链路关键语义测试：
/// 涵盖 LibVLC 播放器防护（Seek/Previous 旁路）、早期结束恢复路由（不降级到单曲搜索）、
/// 预检网络策略校验、歌词与听歌画像旁路、UI 播放器实时 ICY 元数据更新与 DJ 串场播报。
/// </summary>
public class LiveStreamPlaybackTests
{
    [Fact]
    public void AudioService_GetRecoveryActionForTrack_RoutesLiveStreamProperly()
    {
        var normalTrack = new Track { Title = "Normal Song", SourceId = "netease:123" };
        var liveTrack = new Track { Title = "Live Radio", SourceId = "radio-browser:abc-123" };

        Assert.False(normalTrack.IsLiveStream);
        Assert.True(liveTrack.IsLiveStream);

        // 重试次数 0：两者均首先尝试刷新当前源
        Assert.Equal(AudioService.EarlyEndRecoveryAction.RefreshCurrentSource,
            AudioService.GetRecoveryActionForTrack(normalTrack, 0));
        Assert.Equal(AudioService.EarlyEndRecoveryAction.RefreshCurrentSource,
            AudioService.GetRecoveryActionForTrack(liveTrack, 0));

        // 重试次数 1：普通曲目尝试跨源替补；直播流不得降级到单曲搜歌，必须直接切曲（Advance）
        Assert.Equal(AudioService.EarlyEndRecoveryAction.TryAlternativeSource,
            AudioService.GetRecoveryActionForTrack(normalTrack, 1));
        Assert.Equal(AudioService.EarlyEndRecoveryAction.Advance,
            AudioService.GetRecoveryActionForTrack(liveTrack, 1));

        // 重试次数 2：超过预算均直接切曲
        Assert.Equal(AudioService.EarlyEndRecoveryAction.Advance,
            AudioService.GetRecoveryActionForTrack(normalTrack, 2));
        Assert.Equal(AudioService.EarlyEndRecoveryAction.Advance,
            AudioService.GetRecoveryActionForTrack(liveTrack, 2));
    }

    [Fact]
    public void AudioService_LooksLikeEarlyEnd_ReturnsTrueForLiveStream()
    {
        using var svc = new AudioService();
        var method = typeof(AudioService).GetMethod("LooksLikeEarlyEnd", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var liveTrack = new Track { Title = "Live Radio", SourceId = "radio-browser:xyz" };
        var result = (bool)method!.Invoke(svc, new object[] { liveTrack })!;

        Assert.True(result);
    }

    [Fact]
    public void AudioService_Seek_IsNoOp_WhenLiveStream()
    {
        using var svc = new AudioService();
        var lastPosField = typeof(AudioService).GetField("_lastPositionMs", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(lastPosField);

        var liveTrack = new Track
        {
            Title = "Live Radio",
            SourceId = "radio-browser:station-1",
            FilePath = "https://example.com/stream.mp3"
        };

        // 注入当前曲目
        var playlistField = typeof(AudioService).GetField("_playlist", BindingFlags.NonPublic | BindingFlags.Instance);
        var currentIndexField = typeof(AudioService).GetField("_currentIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(playlistField);
        Assert.NotNull(currentIndexField);

        var list = (List<Track>)playlistField!.GetValue(svc)!;
        list.Add(liveTrack);
        currentIndexField!.SetValue(svc, 0);

        Assert.True(svc.CurrentTrack?.IsLiveStream);

        // 调用 Seek，验证位置未被修改
        lastPosField!.SetValue(svc, 1000L);
        svc.Seek(TimeSpan.FromSeconds(30));

        Assert.Equal(1000L, (long)lastPosField.GetValue(svc)!);
    }

    [Fact]
    public async Task PlaybackPreflightService_ValidatesLiveStreamUrl_WithoutQueryingBroker()
    {
        var current = new Track { Id = "0", SourceId = "netease:0", Title = "Current" };
        var publicLiveTrack = new Track
        {
            Id = "1",
            SourceId = "radio-browser:public-stream",
            Title = "Public Radio",
            FilePath = "https://ice1.somafm.com/groovesalad-128-mp3"
        };
        var privateLiveTrack = new Track
        {
            Id = "2",
            SourceId = "radio-browser:private-stream",
            Title = "Private Radio",
            FilePath = "http://192.168.1.100:8000/stream.mp3"
        };

        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
        audio.SetupGet(x => x.CurrentTrack).Returns(current);
        audio.SetupGet(x => x.PlaybackQueue).Returns(new[] { current, publicLiveTrack, privateLiveTrack });

        var broker = new Mock<IMusicSourceBroker>();
        using var preflight = new PlaybackPreflightService(audio.Object, broker.Object);

        await preflight.RunAsync();

        // 公网有效流地址应通过预检
        Assert.Equal(TrackPlayability.Playable, publicLiveTrack.Playability);
        Assert.Equal(PlaybackFailureKind.None, publicLiveTrack.PlaybackFailure);

        // 私网 IP 广播流被 MediaUriPolicy 拦截，拒绝非法出站
        Assert.Equal(TrackPlayability.Failed, privateLiveTrack.Playability);
        Assert.Equal(PlaybackFailureKind.SourceUnavailable, privateLiveTrack.PlaybackFailure);

        // 直播流绝不走单曲 Broker 详细解析
        broker.Verify(x => x.ResolveTrackDetailedAsync(
            It.IsAny<OnlineTrack>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void LyricsViewModel_OnLiveStreamTrack_SetsNoLyricsImmediately()
    {
        var audio = new Mock<IAudioService>();
        var trackSubject = new Subject<Track?>();
        audio.SetupGet(x => x.TrackChanged).Returns(trackSubject);
        audio.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());

        var lyricService = new Mock<ILyricService>();
        using var vm = new LyricsViewModel(audio.Object, lyricService.Object);

        var liveTrack = new Track
        {
            Title = "Live Stream Station",
            Artist = "Radio Broadcaster",
            SourceId = "radio-browser:station-live"
        };

        trackSubject.OnNext(liveTrack);

        Assert.False(vm.HasLyrics);
        Assert.True(vm.HasStatusText);
        Assert.Equal(AppLanguage.T("暂无歌词", "No lyrics available"), vm.LyricStatusText);

        // 验证未向歌词服务发起任何网络请求
        lyricService.Verify(x => x.GetLyricsAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ListeningProfileService_SkipsIngestion_ForLiveStream()
    {
        var llm = new Mock<ILLMService>();
        var tempFile = Path.Combine(Path.GetTempPath(), $"profile_live_test_{Guid.NewGuid():N}.json");
        try
        {
            using var svc = new ListeningProfileService(llm.Object, tempFile, () => DateTime.UtcNow);
            var liveTrack = new Track
            {
                Title = "Live Jazz",
                Artist = "Jazz Station",
                SourceId = "radio-browser:jazz-1"
            };

            svc.NotifyPlaybackStarted(liveTrack);
            svc.NotifyPositionSampled(liveTrack, TimeSpan.FromSeconds(60));
            svc.NotifyPlaybackEndedNaturally(liveTrack);

            var snapshot = svc.GetSnapshot();
            Assert.Equal(0, snapshot.TotalEventsIngested);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void PlayerViewModel_ReflectsLiveStreamState_AndGuardsSeek()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var audio = new Mock<IAudioService>();
            var trackSubject = new Subject<Track?>();
            var liveMetaSubject = new Subject<string?>();

            audio.SetupGet(x => x.TrackChanged).Returns(trackSubject);
            audio.SetupGet(x => x.LiveMetadataChanged).Returns(liveMetaSubject);
            audio.SetupGet(x => x.StateChanged).Returns(Observable.Never<PlaybackState>());
            audio.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());

            Track? current = null;
            audio.SetupGet(x => x.CurrentTrack).Returns(() => current);

            using var vm = new PlayerViewModel(audio.Object);

            // 1. 普通单曲
            current = new Track { Title = "Normal Song", Artist = "Artist A", Duration = TimeSpan.FromMinutes(4) };
            trackSubject.OnNext(current);
            Assert.False(vm.IsLiveStream);
            Assert.NotEqual(AppLanguage.Get("S_LiveBadge"), vm.DurationText);

            // 2. 切换至直播电台
            current = new Track
            {
                Title = "Lofi Radio",
                Artist = "Lofi Girl",
                SourceId = "radio-browser:lofi-girl",
                Duration = TimeSpan.Zero
            };
            trackSubject.OnNext(current);

            Assert.True(vm.IsLiveStream);
            Assert.Equal(AppLanguage.Get("S_LiveBadge"), vm.DurationText);
            Assert.Equal(0, vm.TotalSeconds);

            // 3. 拖拽和 Seek 命令应被屏蔽，不调用底层的 audio.Seek
            vm.StartSeek();
            vm.SeekTo(30);
            vm.EndSeek(30);
            audio.Verify(x => x.Seek(It.IsAny<TimeSpan>()), Times.Never);

            // 4. ICY 实时流元数据到达，更新 TrackArtist 且不修改底层曲目模型
            liveMetaSubject.OnNext("Nujabes - Feather");
            Assert.Equal("Nujabes - Feather", vm.TrackArtist);
            Assert.Equal("Lofi Girl", current.Artist);

            // 5. ICY 元数据清空后回退为电台原艺术家/标签
            liveMetaSubject.OnNext(null);
            Assert.Equal("Lofi Girl", vm.TrackArtist);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void DJService_BuildFallbackIntroduction_GeneratesLiveRadioBanter()
    {
        var now = new DateTime(2026, 11, 9, 20, 0, 0);
        var liveTrack = new Track
        {
            Title = "经典音乐广播",
            Artist = "Classic FM",
            SourceId = "radio-browser:cfm-001"
        };

        // 1. 中文开场（无上一首曲目）
        var profileZh = new DJProfile { Name = "小音", Language = "zh" };
        var introZhOpening = DJService.BuildFallbackIntroduction(profileZh, null, liveTrack, now, null);
        Assert.Contains("小音", introZhOpening.Text);
        Assert.Contains("经典音乐广播", introZhOpening.Text);
        Assert.Contains("电台", introZhOpening.Text);

        // 2. 中文切曲（有上一首曲目）
        var prevTrack = new Track { Title = "晴天", Artist = "周杰伦" };
        var introZhTransition = DJService.BuildFallbackIntroduction(profileZh, prevTrack, liveTrack, now, null);
        Assert.Contains("经典音乐广播", introZhTransition.Text);
        Assert.Contains("波段", introZhTransition.Text);

        // 3. 英文开场
        var profileEn = new DJProfile { Name = "Lumen", Language = "en" };
        var introEnOpening = DJService.BuildFallbackIntroduction(profileEn, null, liveTrack, now, null);
        Assert.Contains("Lumen", introEnOpening.Text);
        Assert.Contains("经典音乐广播", introEnOpening.Text);
        Assert.Contains("Tuning you live", introEnOpening.Text);

        // 4. 英文切曲
        var introEnTransition = DJService.BuildFallbackIntroduction(profileEn, prevTrack, liveTrack, now, null);
        Assert.Contains("Switching frequency", introEnTransition.Text);
        Assert.Contains("经典音乐广播", introEnTransition.Text);
    }
}
