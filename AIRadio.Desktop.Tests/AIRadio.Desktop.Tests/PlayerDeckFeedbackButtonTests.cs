using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using AIRadio.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Moq;
using ReactiveUI;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 回归：PlayerDeck 反馈行（NOPE/SIM/CALM/FIRE/STORY，原 LIKE 已并入中部心形收藏）曾被用户
/// 整体判定"无效"。实际接线完好，根因是各动作几乎全部静默——反馈只影响后续节目单、空闲时早退、
/// STORY 生成失败也无提示。锚定三件事：
/// 1. 五枚按钮 + 心形的 Command 编译绑定必须解析到 MainWindowViewModel 对应命令实例；
/// 2. 播放中每个动作都要有即时可见回应（DJ 气泡）并落到推荐/画像侧；
/// 3. 空闲时反馈行给引导气泡、心形静默早退，而非统一无反应。
/// 注：headless 下未渲染窗口无法投递指针输入，点击链路由 Avalonia Button 内部
/// 状态机保证（与播放控制按钮同机制），测试以"绑定断言+命令直驱"覆盖行为。
/// </summary>
public class PlayerDeckFeedbackButtonTests
{
    private static (MainWindowViewModel Vm, Mock<IRecommendationService> Rec, Mock<IAudioService> Audio) CreateViewModel(
        Track? currentTrack, Func<SongStory>? storyFactory = null,
        Mock<IListeningProfileService>? profileMock = null)
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.PositionChanged).Returns(new Subject<TimeSpan>());
        audio.Setup(x => x.SpectrumData).Returns(new Subject<float[]>());
        audio.Setup(x => x.TtsStateChanged).Returns(new Subject<bool>());
        audio.Setup(x => x.TtsError).Returns(new Subject<string>());
        audio.Setup(x => x.Playlist).Returns(() => new List<Track>().AsReadOnly());
        audio.Setup(x => x.CurrentTrack).Returns(currentTrack);

        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dj = new Mock<IDJService>();
        // TtsEnabled 缺省 false：SpeakDjTextAsync 早退，STORY 断言到聊天气泡即可
        dj.Setup(x => x.GenerateSongStoryAsync(It.IsAny<Track>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Track track, CancellationToken _) => storyFactory?.Invoke()
                ?? new SongStory
                {
                    Title = track.Title,
                    Track = track,
                    Lines = { new DjScriptLine { Text = "一段试播故事" } }
                });

        var rec = new Mock<IRecommendationService>();
        rec.Setup(x => x.FeedbackHistory).Returns(new List<UserMusicFeedback>());
        rec.Setup(x => x.RecentlyPlayed).Returns(new List<Track>());

        var vm = new MainWindowViewModel(
            audio.Object,
            dj.Object,
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            new Mock<IMusicSearchService>().Object,
            new Mock<ISttService>().Object,
            Path.Combine(dir, "playlist.json"),
            settingsFile: Path.Combine(dir, "settings.json"),
            recommendationService: rec.Object,
            listeningProfile: profileMock?.Object);
        return (vm, rec, audio);
    }

    [AvaloniaFact]
    public void RemoveBlacklistItem_UsesTheMainWindowRecommendationService()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var profile = new Mock<IListeningProfileService>();
        profile.Setup(x => x.GetSnapshot()).Returns(ListenerProfileSnapshot.Empty);
        profile.Setup(x => x.RemoveDislikeEvents("晴天", "周杰伦")).Returns(true);
        var (vm, recommendations, _) = CreateViewModel(
            new Track { Title = "晴天", Artist = "周杰伦", SourceId = "netease:1" }, profileMock: profile);
        try
        {
            vm.SettingsVM.RemoveBlacklistItemCommand.Execute(
                new ProfileBlacklistItem("晴天", "周杰伦", "晴天 - 周杰伦")).Subscribe();

            recommendations.Verify(x => x.RemoveDislikeFeedback("晴天", "周杰伦"), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private static Button FindByContent(PlayerDeck view, string content)
        => view.GetVisualDescendants().OfType<Button>()
               .FirstOrDefault(b => string.Equals(b.Content as string, content, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"{content} button not found in PlayerDeck");

    private static void Execute(ReactiveCommand<Unit, Unit> command)
        => command.Execute().Subscribe();

    [AvaloniaFact]
    public async Task FeedbackRow_WithCurrentTrack_AllButtonsTakeEffect()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var (vm, rec, _) = CreateViewModel(track);
        Window? window = null;
        try
        {
            var view = new PlayerDeck { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            // 绑定断言：编译绑定必须把 VM 命令实例解析到对应按钮上（防 XAML 重命名静默断裂）
            var bindings = new (string Label, System.Windows.Input.ICommand Cmd)[]
            {
                ("NOPE", vm.DislikeCurrentTrackCommand),
                ("SIM", vm.SimilarToCurrentTrackCommand),
                ("CALM", vm.CalmerRecommendationCommand),
                ("FIRE", vm.EnergeticRecommendationCommand),
                ("STORY", vm.TellSongStoryCommand),
            };
            foreach (var (label, cmd) in bindings)
                Assert.Same(cmd, FindByContent(view, label).Command);

            // 心形（图标按钮无 Content，FindByContent 摸不到）按 Command 值锚定绑定
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(),
                b => ReferenceEquals(b.Command, vm.ToggleCurrentFavoriteCommand));

            var chatBefore = vm.ChatVM.Messages.Count;

            // 心形收藏即"喜欢"（原反馈行 LIKE 已并入）：收藏与正向反馈一起落；
            // 收藏本身不再刷气泡（2026-10-22 用户反馈），图标态即反馈
            Execute(vm.ToggleCurrentFavoriteCommand);
            Assert.True(vm.IsCurrentFavorite);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Like && f.TrackId == "src-1")), Times.Once);

            Execute(vm.DislikeCurrentTrackCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Dislike && f.Title == "晴天" && f.Artist == "周杰伦")), Times.Once);

            Execute(vm.SimilarToCurrentTrackCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Similar)), Times.Once);

            Execute(vm.CalmerRecommendationCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Calmer)), Times.Once);
            rec.Verify(x => x.SetMoodBias("calm"), Times.Once);

            Execute(vm.EnergeticRecommendationCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Energetic)), Times.Once);
            rec.Verify(x => x.SetMoodBias("energetic"), Times.Once);

            // STORY 为 CreateFromTask，异步落气泡
            Execute(vm.TellSongStoryCommand);
            for (var i = 0; i < 40 && vm.ChatVM.Messages.Count < chatBefore + 5; i++)
                await Task.Delay(50);

            // 四枚反馈各一句 + STORY 讲述一句 = 5 条新消息（心形收藏无气泡）
            Assert.Equal(chatBefore + 5, vm.ChatVM.Messages.Count);
            Assert.Contains(vm.ChatVM.Messages, m => m.Content.Contains("一段试播故事"));
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public async Task FeedbackRow_NoCurrentTrack_ClicksGiveGuidanceInsteadOfSilence()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var (vm, rec, _) = CreateViewModel(currentTrack: null);
        Window? window = null;
        try
        {
            var view = new PlayerDeck { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var chatBefore = vm.ChatVM.Messages.Count;
            // 心形收藏无曲目时静默早退（控制区按钮语义，与反馈行的引导气泡不同）
            Execute(vm.ToggleCurrentFavoriteCommand);
            Execute(vm.DislikeCurrentTrackCommand);
            Execute(vm.SimilarToCurrentTrackCommand);
            Execute(vm.CalmerRecommendationCommand);
            Execute(vm.EnergeticRecommendationCommand);
            Execute(vm.TellSongStoryCommand);
            for (var i = 0; i < 40 && vm.ChatVM.Messages.Count < chatBefore + 5; i++)
                await Task.Delay(50);

            // 空闲时不落反馈/不设氛围，但五次反馈点击每次都要有引导气泡（静默=被当成"按钮无效"）
            Assert.Equal(chatBefore + 5, vm.ChatVM.Messages.Count);
            rec.Verify(x => x.RecordFeedback(It.IsAny<UserMusicFeedback>()), Times.Never);
            rec.Verify(x => x.SetMoodBias(It.IsAny<string?>()), Times.Never);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    [AvaloniaFact]
    public async Task SongStory_EmptyLines_FallsBackToNoticeBubble()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        // LLM 失败：DJService 返回空 Lines
        var (vm, _, _) = CreateViewModel(track, storyFactory: () => new SongStory());
        try
        {
            var chatBefore = vm.ChatVM.Messages.Count;
            Execute(vm.TellSongStoryCommand);
            for (var i = 0; i < 40 && vm.ChatVM.Messages.Count <= chatBefore; i++)
                await Task.Delay(50);

            Assert.Equal(chatBefore + 1, vm.ChatVM.Messages.Count); // 兜底气泡而非静默
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>
    /// 回归（2026-10-20 用户裁决：LIKE 并入心形收藏——点收藏=收藏到歌单+正向反馈双落）：
    /// 收藏沿触发 Like 反馈（会话+长期画像双链）；取消收藏只动歌单，不回撤画像也不重复记
    /// （事件唯一事实）。收藏/取消均不刷聊天气泡（2026-10-22 用户反馈：模板文案每点必现，图标态即反馈）。
    /// </summary>
    [AvaloniaFact]
    public void ToggleFavorite_RecordsLikeSignalOnlyOnFavoriteEdge()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var profile = new Mock<IListeningProfileService>();
        var (vm, rec, _) = CreateViewModel(track, profileMock: profile);
        try
        {
            Execute(vm.ToggleCurrentFavoriteCommand);
            Assert.True(vm.IsCurrentFavorite);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Like && f.TrackId == "src-1")), Times.Once);
            profile.Verify(x => x.RecordEvent(It.Is<ListeningEventData>(
                e => e.Type == ListeningEventType.Like && e.SourceId == "src-1" && e.Artist == "周杰伦")),
                Times.Once);
            var chatAfterFavorite = vm.ChatVM.Messages.Count;

            // 取消收藏：移出收藏但不回撤画像（无第二条 Like），两个沿都不刷气泡
            Execute(vm.ToggleCurrentFavoriteCommand);
            Assert.False(vm.IsCurrentFavorite);
            rec.Verify(x => x.RecordFeedback(It.IsAny<UserMusicFeedback>()), Times.Once);
            profile.Verify(x => x.RecordEvent(It.IsAny<ListeningEventData>()), Times.Once);
            Assert.Equal(chatAfterFavorite, vm.ChatVM.Messages.Count);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>
    /// 回归（2026-10-20 用户反馈：不喜欢的时候没人愿意把歌听完）：NOPE 必须立即切歌。
    /// 会话级避开只防"后续推荐再选它"，正在播的这首必须马上跳过；其余反馈动作不得切歌。
    /// </summary>
    [AvaloniaFact]
    public void DislikeCurrentTrack_SkipsToNextTrackImmediately()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var (vm, rec, audio) = CreateViewModel(track);
        // 电台模式经推荐回调取曲，切歌守卫永远放行
        audio.Setup(x => x.RepeatMode).Returns("radio");
        try
        {
            Execute(vm.ToggleCurrentFavoriteCommand);
            Execute(vm.CalmerRecommendationCommand);
            Execute(vm.SimilarToCurrentTrackCommand);
            audio.Verify(x => x.Next(), Times.Never);

            Execute(vm.DislikeCurrentTrackCommand);
            audio.Verify(x => x.Next(), Times.Once);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Dislike)), Times.Once);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>
    /// 回归（复审发现）：单曲队列下 Next 会 (index+1)%count 绕回重播本曲——
    /// 刚拉黑的歌从头再来是二次折磨。列表模式队列中无其它曲目时 NOPE 只记录不切歌，
    /// 气泡同步收敛为不含"跳过"承诺；出现其它曲目后恢复立即切歌。
    /// </summary>
    [AvaloniaFact]
    public void DislikeCurrentTrack_SingleTrackList_DoesNotReplayDislikedTrack()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var (vm, _, audio) = CreateViewModel(track);
        audio.Setup(x => x.RepeatMode).Returns("list");
        try
        {
            audio.Setup(x => x.Playlist).Returns(new List<Track> { track }.AsReadOnly());
            Execute(vm.DislikeCurrentTrackCommand);
            audio.Verify(x => x.Next(), Times.Never);
            Assert.Contains(vm.ChatVM.Messages, m =>
                m.Content.Contains("本轮不再安排") && !m.Content.Contains("这就跳过"));

            audio.Setup(x => x.Playlist).Returns(new List<Track>
            {
                track,
                new Track { Title = "七里香", Artist = "周杰伦", SourceId = "src-2" },
            }.AsReadOnly());
            Execute(vm.DislikeCurrentTrackCommand);
            audio.Verify(x => x.Next(), Times.Once);
            Assert.Contains(vm.ChatVM.Messages, m => m.Content.Contains("这就跳过"));
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>
    /// 回归（2026-10-20 审核：SIM 名字暗示"马上来相似的"、实际只记长期画像零即时效果）：
    /// 按下 SIM 必须立即以"与当前歌曲气质相近"意图重排节目单（不打断在播曲目）。
    /// </summary>
    [AvaloniaFact]
    public async Task SimilarCurrentTrack_RegeneratesProgramWithSimilarIntent()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var track = new Track { Title = "晴天", Artist = "周杰伦", SourceId = "src-1" };
        var (vm, rec, _) = CreateViewModel(track);
        RecommendationRequest? captured = null;
        rec.Setup(x => x.CreateProgramAsync(It.IsAny<RecommendationRequest>()))
            .Callback<RecommendationRequest>(r => captured = r)
            .ReturnsAsync(new RadioProgram());
        try
        {
            Execute(vm.SimilarToCurrentTrackCommand);
            for (var i = 0; i < 40 && captured == null; i++)
                await Task.Delay(50);

            Assert.NotNull(captured);
            Assert.Equal(RecommendationIntentKeys.SimilarToCurrent, captured!.UserIntentKey);
            Assert.Equal("晴天", captured.CurrentTrack?.Title);
        }
        finally
        {
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    /// <summary>
    /// 回归（2026-10-20 用户反馈：红框内 LIKE/NOPE/SIM/CALM/FIRE/STORY 文字与按钮没对齐）：
    /// 根因——Fluent Button 模板的内容对齐 TemplateBinding 到按钮属性而 ControlTheme 未设
    /// Center（ContentControl 双向默认 Stretch），ContentPresenter.ArrangeOverrideImpl 的
    /// Stretch 分支把隐式 TextBlock arrange 到整个内容区、不收缩也不加偏移，字形从左上起
    /// 排——实机像素测量垂直贴上沿偏约 4px（顶距 3px/底距 11px）、水平偏左（小字号下
    /// 视觉以垂直为主）。图标按钮因 PathIcon 固定尺寸拉不动而幸免。锚定：MainWindow
    /// 样式族必须给 Button 显式双向 Center 内容对齐（删 Setter 即红）。
    /// </summary>
    [AvaloniaFact]
    public void FeedbackRow_TextButtons_HaveCenteredContentAlignment()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            // 样式族挂在 MainWindow.Window.Styles，断言必须在真实窗口作用域内做
            window = new MainWindow { DataContext = vm };
            window.Show();

            foreach (var label in new[] { "NOPE", "SIM", "CALM", "FIRE", "STORY" })
            {
                var button = window.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(b => string.Equals(b.Content as string, label, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"{label} button not found in MainWindow");
                Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, button.HorizontalContentAlignment);
                Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, button.VerticalContentAlignment);
            }
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }
}
