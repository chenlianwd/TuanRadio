using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using ReactiveUI;
using Xunit;

namespace AIRadio.Desktop.Tests;

// Note: all tests use real AudioService (requires LibVLC native libs). Consider mocking for CI (L25).
public class PlayerViewModelTests
{
    [Fact]
    public void ToggleRepeatCommand_UpdatesModeTextAndTooltip()
    {
        var mode = "radio";
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns(() => mode);
        audio.Setup(x => x.SetRepeatMode(It.IsAny<string>()))
            .Callback<string>(value => mode = value);
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());

        using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
        Assert.Equal("电台模式", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("list", vm.RepeatMode);
        Assert.Equal("ALL", vm.RepeatText);
        Assert.Equal("列表循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("single", vm.RepeatMode);
        Assert.Equal("单曲循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("none", vm.RepeatMode);
        Assert.Equal("关闭循环", vm.RepeatModeTip);

        vm.ToggleRepeatCommand.Execute().Subscribe();
        Assert.Equal("radio", vm.RepeatMode);
        Assert.Equal("电台模式", vm.RepeatModeTip);
    }

    [Fact]
    public void TrackSource_FollowsPlaybackIdentityAndLanguage()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        AppLanguage.Apply("zh");
        try
        {
            var tracks = new Subject<Track?>();
            Track? current = null;
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.CurrentTrack).Returns(() => current);
            audio.SetupGet(x => x.RepeatMode).Returns("radio");
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
            current = new Track { Title = "Song", SourceId = "netease:1" };
            tracks.OnNext(current);
            Assert.Equal("网易云音乐", vm.TrackSource);
            AppLanguage.Apply("en");
            Assert.Equal("NetEase Cloud Music", vm.TrackSource);
            current = new Track { Title = "Song", SourceId = "audius:2" };
            tracks.OnNext(current);
            Assert.Equal("Audius", vm.TrackSource);
            current = null;
            tracks.OnNext(null);
            Assert.Equal("", vm.TrackSource);
        }
        finally
        {
            AppLanguage.Apply("zh");
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void TrackQuality_PrefersActualInfoOverDeclared_AndDropsStaleEvents()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        AppLanguage.Apply("zh");
        try
        {
            var tracks = new Subject<Track?>();
            var mediaInfo = new Subject<TrackMediaInfo>();
            Track? current = null;
            var audio = new Mock<IAudioService>();
            audio.SetupGet(x => x.CurrentTrack).Returns(() => current);
            audio.SetupGet(x => x.RepeatMode).Returns("radio");
            audio.SetupGet(x => x.TrackChanged).Returns(tracks);
            audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
            audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
            audio.SetupGet(x => x.TrackMediaInfo).Returns(mediaInfo);
            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);

            // 声明值兜底（OpenSubsonic 刷新路径落到 Track 的 Declared* 字段）
            current = new Track
            {
                Title = "Song", SourceId = "opensubsonic:1",
                DeclaredCodec = "FLAC", DeclaredBitrateKbps = 900, IsPreviewStream = true
            };
            tracks.OnNext(current);
            Assert.Equal("无损", vm.TrackQualityText);
            Assert.Equal("无损", vm.TrackQualityShort);
            Assert.Contains("FLAC · 900 kbps", vm.TrackQualityDetail);
            Assert.True(vm.HasQuality);
            Assert.True(vm.TrackIsPreview);
            Assert.Equal("试听片段", vm.TrackPreviewText);

            // LibVLC 实际值到达后覆盖声明值；行内显示品级，技术细节进 Tooltip
            mediaInfo.OnNext(new TrackMediaInfo(current.Id, "MP3", 320, 44100, 2));
            Assert.Equal("高清", vm.TrackQualityText);
            Assert.Equal("高清", vm.TrackQualityShort);
            Assert.Contains("MP3 · 320 kbps", vm.TrackQualityDetail);
            Assert.Contains("44.1 kHz", vm.TrackQualityDetail);
            Assert.Contains("立体声", vm.TrackQualityDetail);

            // 语言切换重算品级文案（无损/高清/标准三档英文全锚定）与试听徽标
            AppLanguage.Apply("en");
            Assert.Equal("HD", vm.TrackQualityText);
            Assert.Equal("Preview", vm.TrackPreviewText);
            Assert.Contains("stereo", vm.TrackQualityDetail);
            mediaInfo.OnNext(new TrackMediaInfo(current.Id, "FLAC", null, null, null));
            Assert.Equal("Lossless", vm.TrackQualityText);
            mediaInfo.OnNext(new TrackMediaInfo(current.Id, "MP3", 128, null, null));
            Assert.Equal("Standard", vm.TrackQualityText);
            AppLanguage.Apply("zh");

            // 快切歌：新曲目回到声明值（无声明则为空），旧媒体迟到信息被 TrackId 卫兵丢弃
            var next = new Track { Title = "Next", SourceId = "local:2" };
            current = next;
            tracks.OnNext(next);
            Assert.False(vm.HasQuality);
            mediaInfo.OnNext(new TrackMediaInfo("stale-id", "MP3", 320, 44100, 2));
            Assert.False(vm.HasQuality);

            // 新曲目的实际值正常生效；无损编码无码率也能判品级，全空时徽标保持隐藏
            mediaInfo.OnNext(new TrackMediaInfo(next.Id, "FLAC", null, 48000, 2));
            Assert.Equal("无损", vm.TrackQualityText);
            Assert.True(vm.HasQuality);
            // 品级不可判（有损缺码率且无声明值可回落）：行内回退裸编码名，徽标可见、Tooltip 技术细节入口不丢
            mediaInfo.OnNext(new TrackMediaInfo(next.Id, "OPUS", null, 48000, 2));
            Assert.Equal("OPUS", vm.TrackQualityText);
            Assert.True(vm.HasQuality);
            Assert.Contains("48 kHz", vm.TrackQualityDetail);
            mediaInfo.OnNext(new TrackMediaInfo(next.Id, null, null, null, null));
            Assert.False(vm.HasQuality);
        }
        finally
        {
            AppLanguage.Apply("zh");
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void QualityTier_ClassifiesByCodecAndBitrate()
    {
        // 有损编码按码率分档：128/192 标准，256/320 高清
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Standard,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", 128));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Standard,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("AAC", 192));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", 256));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", 320));
        // 分档边界锚定：255/256 高清下限，599/600 hires 无损下限（编码缺失走声明码率兜底）
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Standard,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, 255));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, 256));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, 599));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Lossless,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, 600));
        // 无损：编码优先（忽略大小写、无需码率）；编码缺失时 hires 声明码率兜底
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Lossless,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("FLAC", null));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Lossless,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("flac", 320));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Lossless,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("PCM", null));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.Lossless,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, 999));
        // 已知有损编码高码率不冒充无损（DTS 1411k、AC3 640k 等高码率有损真实存在），封顶高清
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("DTS", 1411));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("AC3", 640));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.HighDefinition,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", 999));
        // 无法诚实分档（缺码率的有损编码/全空/非正值）→ None，行内回退裸编码名
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.None,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", null));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.None,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier(null, null));
        Assert.Equal(AIRadio.Desktop.ViewModels.QualityTier.None,
            AIRadio.Desktop.ViewModels.PlayerViewModel.ClassifyQualityTier("MP3", 0));
    }

    private static Mock<IAudioService> CreatePositionalAudioMock(Subject<TimeSpan> positions)
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns("radio");
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(positions);
        audio.Setup(x => x.Seek(It.IsAny<TimeSpan>()));
        return audio;
    }

    [Fact]
    public void PlayerViewModel_SeekTo_UpdatesPositionTextAndForwardsToService()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var positions = new Subject<TimeSpan>();
            double? seekTarget = null;
            var audio = CreatePositionalAudioMock(positions);
            audio.Setup(x => x.Seek(It.IsAny<TimeSpan>()))
                .Callback<TimeSpan>(ts => seekTarget = ts.TotalSeconds);

            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
            positions.OnNext(TimeSpan.FromSeconds(12));
            Assert.Equal(12, vm.CurrentSeconds);
            Assert.Equal("0:12", vm.PositionText);

            vm.SeekTo(30.0);
            Assert.Equal(30.0, seekTarget);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlayerViewModel_DraggingState_FreezesPositionUpdates()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        try
        {
            var positions = new Subject<TimeSpan>();
            var audio = CreatePositionalAudioMock(positions);

            using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
            positions.OnNext(TimeSpan.FromSeconds(10));

            vm.StartSeek();
            positions.OnNext(TimeSpan.FromSeconds(50));
            // 拖动期间不得回写 CurrentSeconds，否则 Slider 会被播放位置拉回
            Assert.Equal(10, vm.CurrentSeconds);

            vm.EndSeek(50);
            positions.OnNext(TimeSpan.FromSeconds(51));
            Assert.Equal(51, vm.CurrentSeconds);
        }
        finally
        {
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [Fact]
    public void PlayerViewModel_SyncsInitialVolumeAndShuffleFromService()
    {
        var audio = new Mock<IAudioService>();
        audio.SetupGet(x => x.RepeatMode).Returns("radio");
        audio.SetupGet(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.SetupGet(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.SetupGet(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.SetupGet(x => x.Volume).Returns(0.4f);
        audio.SetupGet(x => x.IsShuffled).Returns(true);

        using var vm = new AIRadio.Desktop.ViewModels.PlayerViewModel(audio.Object);
        Assert.Equal(0.4f, vm.Volume);
        Assert.True(vm.IsShuffled);
    }

    [Fact]
    public void AudioService_PlayAtIndex_InvalidIndex_NoCrash()
    {
        var service = new AudioService();
        try
        {
            service.PlayAtIndex(-1);
            service.PlayAtIndex(999);
            // No exception
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void AudioService_NextPrevious_DoNotThrow()
    {
        var service = new AudioService();
        service.LoadTracks(new[]
        {
            new Track { Title = "Song 1", FilePath = "http://example.com/1.mp3" },
            new Track { Title = "Song 2", FilePath = "http://example.com/2.mp3" }
        });

        try
        {
            service.Next();
            Assert.NotNull(service.CurrentTrack);

            service.Previous();
            Assert.NotNull(service.CurrentTrack);

            Assert.Equal(2, service.Playlist.Count);
        }
        finally
        {
            service.Dispose();
        }
    }
}
