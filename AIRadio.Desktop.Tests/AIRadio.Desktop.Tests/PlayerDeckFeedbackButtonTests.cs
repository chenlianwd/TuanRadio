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
/// 回归：PlayerDeck 反馈行（LIKE/NOPE/SIM/CALM/FIRE/STORY）曾被用户整体判定"无效"。
/// 实际接线完好，根因是各动作几乎全部静默——反馈只影响后续节目单、空闲时早退、
/// STORY 生成失败也无提示。锚定三件事：
/// 1. 六枚按钮的 Command 编译绑定必须解析到 MainWindowViewModel 对应命令实例；
/// 2. 播放中每个动作都要有即时可见回应（DJ 气泡）并落到推荐/画像侧；
/// 3. 空闲时给出引导气泡而非静默。
/// 注：headless 下未渲染窗口无法投递指针输入，点击链路由 Avalonia Button 内部
/// 状态机保证（与播放控制按钮同机制），测试以"绑定断言+命令直驱"覆盖行为。
/// </summary>
public class PlayerDeckFeedbackButtonTests
{
    private static (MainWindowViewModel Vm, Mock<IRecommendationService> Rec) CreateViewModel(
        Track? currentTrack, Func<SongStory>? storyFactory = null)
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
            recommendationService: rec.Object);
        return (vm, rec);
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
        var (vm, rec) = CreateViewModel(track);
        Window? window = null;
        try
        {
            var view = new PlayerDeck { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            // 绑定断言：编译绑定必须把 VM 命令实例解析到对应按钮上（防 XAML 重命名静默断裂）
            var bindings = new (string Label, System.Windows.Input.ICommand Cmd)[]
            {
                ("LIKE", vm.LikeCurrentTrackCommand),
                ("NOPE", vm.DislikeCurrentTrackCommand),
                ("SIM", vm.SimilarToCurrentTrackCommand),
                ("CALM", vm.CalmerRecommendationCommand),
                ("FIRE", vm.EnergeticRecommendationCommand),
                ("STORY", vm.TellSongStoryCommand),
            };
            foreach (var (label, cmd) in bindings)
                Assert.Same(cmd, FindByContent(view, label).Command);

            var chatBefore = vm.ChatVM.Messages.Count;

            Execute(vm.LikeCurrentTrackCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Like && f.TrackId == "src-1")), Times.Once);
            // 文案锚点：锁动作→文案映射（防 CALM/FIRE 等文案对调时仅靠计数通过）
            Assert.True(vm.ChatVM.Messages[^1].Content.Contains("记下了") ||
                        vm.ChatVM.Messages[^1].Content.Contains("Noted"),
                "LIKE 执行后的最新气泡应是 Like 确认文案");

            Execute(vm.DislikeCurrentTrackCommand);
            rec.Verify(x => x.RecordFeedback(It.Is<UserMusicFeedback>(
                f => f.Action == MusicFeedbackAction.Dislike)), Times.Once);

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
            for (var i = 0; i < 40 && vm.ChatVM.Messages.Count < chatBefore + 6; i++)
                await Task.Delay(50);

            // 五枚反馈各一句即时确认 + STORY 讲述一句 = 6 条新消息
            Assert.Equal(chatBefore + 6, vm.ChatVM.Messages.Count);
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
        var (vm, rec) = CreateViewModel(currentTrack: null);
        Window? window = null;
        try
        {
            var view = new PlayerDeck { DataContext = vm };
            window = new Window { Content = view };
            window.Show();

            var chatBefore = vm.ChatVM.Messages.Count;
            Execute(vm.LikeCurrentTrackCommand);
            Execute(vm.DislikeCurrentTrackCommand);
            Execute(vm.SimilarToCurrentTrackCommand);
            Execute(vm.CalmerRecommendationCommand);
            Execute(vm.EnergeticRecommendationCommand);
            Execute(vm.TellSongStoryCommand);
            for (var i = 0; i < 40 && vm.ChatVM.Messages.Count < chatBefore + 6; i++)
                await Task.Delay(50);

            // 空闲时不落反馈/不设氛围，但六次点击每次都要有引导气泡（静默=被当成"按钮无效"）
            Assert.Equal(chatBefore + 6, vm.ChatVM.Messages.Count);
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
        var (vm, _) = CreateViewModel(track, storyFactory: () => new SongStory());
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
}
