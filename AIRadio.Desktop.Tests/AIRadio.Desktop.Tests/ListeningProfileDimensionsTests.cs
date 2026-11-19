using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
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
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ListeningProfileDimensionsTests
{
    [Theory]
    [InlineData("晴天", "周杰伦", "zh")]
    [InlineData("Mojito", "周杰伦", "zh")]
    [InlineData("Love Story", "Taylor Swift", "en")]
    [InlineData("夜に駆ける", "YOASOBI", "ja")]
    [InlineData("さくら", "宇多田ヒカル", "ja")]
    [InlineData("밤편지", "아이유", "ko")]
    [InlineData("12345", "???", null)]
    public void DetectTrackLanguage_IdentifiesScriptAccurately(string title, string artist, string? expected)
    {
        Assert.Equal(expected, ListeningPersona.DetectTrackLanguage(title, artist));
    }

    [Fact]
    public async Task Snapshot_LanguageDistribution_RespectsThresholdExcludesNegativeAndFiltersNoise()
    {
        var now = new DateTime(2026, 11, 19, 14, 0, 0);
        var (service, dir) = await CreateLoadedServiceAsync(() => now);
        try
        {
            // 9 条正样本（不足 10 条门槛）→ 为空
            for (var i = 0; i < 9; i++)
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Completed,
                    Title = $"晴天{i}",
                    Artist = "周杰伦",
                    Time = now.AddMinutes(-i),
                });
            Assert.Empty(service.GetSnapshot().LanguageDistribution);

            // 补足到 10+ 样本：8 首华语 + 3 首英文 + 1 首日语（如果权重 <5% 应被过滤）
            // 另加若干 Dislike/Skipped 韩语曲：负信号不应污染语种配比
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Completed,
                Title = "稻香",
                Artist = "周杰伦",
                Time = now,
            });
            for (var i = 0; i < 4; i++)
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Completed,
                    Title = $"Track {i}",
                    Artist = "Coldplay",
                    Time = now.AddMinutes(-i),
                });
            // 1 次 Played 权重 0.2，在总权重 ~14.2 中约 1.4% (< 5%) 应作为噪音剔除
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = "レモン",
                Artist = "米津玄師",
                Time = now,
            });
            for (var i = 0; i < 5; i++)
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Dislike,
                    Title = $"싫어{i}",
                    Artist = "가수",
                    Time = now,
                });

            var dist = service.GetSnapshot().LanguageDistribution;
            Assert.Equal(2, dist.Count);
            Assert.Equal("zh", dist[0].LanguageCode);
            Assert.Equal("en", dist[1].LanguageCode);
            Assert.True(dist[0].Share > dist[1].Share);
            Assert.Equal(1.0, dist.Sum(x => x.Share), precision: 5);
            Assert.DoesNotContain(dist, x => x.LanguageCode is "ja" or "ko");
        }
        finally
        {
            service.Dispose();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Snapshot_TimeBandPreferences_SeparatesMorningAndLateNightTastes()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0);
        var (service, dir) = await CreateLoadedServiceAsync(() => now);
        try
        {
            var morning = new DateTime(2026, 11, 19, 9, 0, 0);
            var night = new DateTime(2026, 11, 18, 23, 30, 0);
            var afternoon = new DateTime(2026, 11, 19, 14, 0, 0);

            // 上午 6 条华语 + energetic
            for (var i = 0; i < 5; i++)
            {
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Completed,
                    Title = $"朝歌{i}",
                    Artist = "五月天",
                    Time = morning.AddMinutes(-i),
                });
            }
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Energetic,
                Detail = "energetic",
                Time = morning,
            });

            // 深夜 6 条欧美/纯音 + calm + 1 个被踩的负分歌手（不得进 TopArtists）
            for (var i = 0; i < 5; i++)
            {
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Completed,
                    Title = $"Nocturne {i}",
                    Artist = "Max Richter",
                    Time = night.AddMinutes(-i),
                });
            }
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Calmer,
                Detail = "calm",
                Time = night,
            });
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Dislike,
                Title = "Noise",
                Artist = "Bad DJ",
                Time = night,
            });

            // 下午仅 3 条（不足 5 条门槛）→ 不应收录
            for (var i = 0; i < 3; i++)
            {
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Played,
                    Title = $"午后{i}",
                    Artist = "林俊杰",
                    Time = afternoon.AddMinutes(-i),
                });
            }

            var bands = service.GetSnapshot().TimeBandPreferences;
            Assert.True(bands.ContainsKey("morning"));
            Assert.True(bands.ContainsKey("night"));
            Assert.False(bands.ContainsKey("afternoon"));

            var morningPref = bands["morning"];
            Assert.Contains("五月天", morningPref.TopArtists);
            Assert.Contains("energetic", morningPref.TopMoods);
            Assert.Equal("zh", morningPref.DominantLanguage);

            var nightPref = bands["night"];
            Assert.Contains("Max Richter", nightPref.TopArtists);
            Assert.DoesNotContain("Bad DJ", nightPref.TopArtists);
            Assert.Contains("calm", nightPref.TopMoods);
            Assert.Equal("en", nightPref.DominantLanguage);
        }
        finally
        {
            service.Dispose();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task DigestPrompt_IncludesLanguageMixAndTimeBandSummary()
    {
        AppLanguage.Apply("zh");
        var now = new DateTime(2026, 11, 19, 23, 0, 0);
        string? capturedPrompt = null;
        var llm = new Mock<ILLMService>();
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((p, _) => capturedPrompt = p)
            .ReturnsAsync("{\"summary\":\"深夜喜欢安静英语曲目\",\"tags\":[\"欧美\"]}");

        var (service, dir) = await CreateLoadedServiceAsync(() => now, llm.Object);
        try
        {
            for (var i = 0; i < 30; i++)
            {
                service.RecordEvent(new ListeningEventData
                {
                    Type = ListeningEventType.Completed,
                    Title = $"Song {i}",
                    Artist = $"Artist {i % 4}",
                    Time = now.AddMinutes(-i),
                });
            }

            await service.RefreshDigestAsync(CancellationToken.None);

            Assert.NotNull(capturedPrompt);
            Assert.Contains("语种配比", capturedPrompt);
            Assert.Contains("时段偏好", capturedPrompt);
        }
        finally
        {
            service.Dispose();
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task RecommendationService_PromptAndFallback_ReflectCurrentTimeBandAndLanguageMix()
    {
        AppLanguage.Apply("zh");
        var snapshot = BuildThresholdSnapshotWithNewDimensions();
        var profile = new FakeProfile { Snapshot = snapshot };

        // 1. LLM 路径：23:30 深夜命中 night 时段偏好与语种配比
        var nightPrompts = new List<string>();
        var llm = new Mock<ILLMService>();
        llm.Setup(x => x.ChatAsync(Capture.In(nightPrompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("Max Richter On The Nature\nColdplay Yellow\n坂本龙一 Merry Christmas");
        var search = new Mock<IMusicSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var nightService = new RecommendationService(
            llm.Object, search.Object, profile, clock: () => new DateTime(2026, 11, 19, 23, 30, 0));
        await nightService.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        var nightPrompt = Assert.Single(nightPrompts);
        Assert.Contains("语种配比：华语 65%、日语 35%", nightPrompt);
        Assert.Contains("当前时段（深夜）偏好：常听 坂本龙一", nightPrompt);
        Assert.Contains("偏向 calm 氛围", nightPrompt);

        // 2. 兜底路径（模拟 LLM 调用异常）：上午 9:00 优先挑 morning 时段偏好歌手
        var failingLlm = new Mock<ILLMService>();
        failingLlm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        var searchedQueries = new List<string>();
        var fallbackSearch = new Mock<IMusicSearchService>();
        fallbackSearch.Setup(x => x.SearchAsync(Capture.In(searchedQueries), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var morningFallbackService = new RecommendationService(
            failingLlm.Object, fallbackSearch.Object, profile, clock: () => new DateTime(2026, 11, 19, 9, 0, 0));
        await morningFallbackService.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        Assert.Contains("五月天 相似歌曲", searchedQueries);
        Assert.Contains("摇滚 热血", searchedQueries);
    }

    [Fact]
    public async Task DJService_ChatProfileInjection_IncludesLanguageMixAndTimeBandFollowingPersonaLanguage()
    {
        var snapshot = BuildThresholdSnapshotWithNewDimensions();
        var profile = new FakeProfile { Snapshot = snapshot };
        var captured = new List<List<ChatMessage>>();
        var llm = new Mock<ILLMService>();
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), Capture.In(captured)))
            .ReturnsAsync("Got it [calm]");

        var djEn = new DJService(
            llm.Object, profile: profile, clock: () => new DateTime(2026, 11, 19, 23, 15, 0));
        djEn.Initialize(new DJProfile { Name = "Alex", SystemPrompt = "Persona", Language = "en" });

        await djEn.GenerateChatResponseAsync("hey dj");

        var systemPromptEn = captured[^1][0].Content;
        Assert.Contains("language mix: Chinese 65%, Japanese 35%", systemPromptEn);
        Assert.Contains("at this hour (late night): leans toward 坂本龙一 · calm", systemPromptEn);

        var djZh = new DJService(
            llm.Object, profile: profile, clock: () => new DateTime(2026, 11, 19, 23, 15, 0));
        djZh.Initialize(new DJProfile { Name = "小夏", SystemPrompt = "人设", Language = "zh" });

        await djZh.GenerateChatResponseAsync("你好");

        var systemPromptZh = captured[^1][0].Content;
        Assert.Contains("语种配比：华语 65%、日语 35%", systemPromptZh);
        Assert.Contains("当前时段（深夜）常听：坂本龙一 · calm", systemPromptZh);
    }

    [Fact]
    public void SettingsViewModel_MapsAndRelocalizesLanguageMixAndTimeBands()
    {
        AppLanguage.Apply("zh");
        var snapshot = new ListenerProfileSnapshot
        {
            TotalEventsIngested = 20,
            LanguageDistribution = new[]
            {
                new LanguageShare("zh", 0.65, 13),
                new LanguageShare("ja", 0.35, 7),
            },
            TimeBandPreferences = new Dictionary<string, TimeBandPreference>
            {
                ["morning"] = new("morning", 8, new[] { "五月天" }, new[] { "energetic" }, "zh"),
                ["night"] = new("night", 10, new[] { "坂本龙一" }, new[] { "calm" }, "ja"),
            },
        };
        var profile = new FakeProfile { Snapshot = snapshot };
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vm = new SettingsViewModel(
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            Path.Combine(dir, "settings.json"),
            listeningProfile: profile);
        try
        {
            vm.ShowProfileTabCommand.Execute().Subscribe();

            Assert.True(vm.ProfileHasCard);
            Assert.True(vm.ProfileHasLanguageMix);
            Assert.Equal("华语 65% · 日语 35%", vm.ProfileLanguageMixText);
            Assert.True(vm.ProfileHasTimeBandPreferences);
            Assert.Contains("上午：五月天 · energetic", vm.ProfileTimeBandSummaryText);
            Assert.Contains("深夜：坂本龙一 · calm", vm.ProfileTimeBandSummaryText);

            vm.SelectedLanguage = "en";

            Assert.Equal("Chinese 65% · Japanese 35%", vm.ProfileLanguageMixText);
            Assert.Contains("morning: 五月天 · energetic", vm.ProfileTimeBandSummaryText);
            Assert.Contains("late night: 坂本龙一 · calm", vm.ProfileTimeBandSummaryText);
        }
        finally
        {
            AppLanguage.Apply("zh");
            vm.Dispose();
            TryDeleteDir(dir);
        }
    }

    [AvaloniaFact]
    public void SettingsView_ProfileCard_BindsLanguageMixAndTimeBandControls()
    {
        var originalScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = CurrentThreadScheduler.Instance;
        var vm = ChatAreaMicButtonTests.CreateViewModel();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = vm };
            window.Show();

            var view = window.FindDescendantOfType<SettingsView>()
                ?? throw new InvalidOperationException("SettingsView not found");
            var langText = view.FindControl<TextBlock>("ProfileLanguageMixText")
                ?? throw new InvalidOperationException("ProfileLanguageMixText not found");
            var bandText = view.FindControl<TextBlock>("ProfileTimeBandSummaryText")
                ?? throw new InvalidOperationException("ProfileTimeBandSummaryText not found");

            vm.SettingsVM.ProfileHasCard = true;
            vm.SettingsVM.ProfileHasLanguageMix = true;
            vm.SettingsVM.ProfileLanguageMixText = "华语 70% · 欧美 30%";
            vm.SettingsVM.ProfileHasTimeBandPreferences = true;
            vm.SettingsVM.ProfileTimeBandSummaryText = "深夜：陈奕迅 · calm";

            Assert.True(langText.IsVisible);
            Assert.Equal("华语 70% · 欧美 30%", langText.Text);
            Assert.True(bandText.IsVisible);
            Assert.Equal("深夜：陈奕迅 · calm", bandText.Text);
        }
        finally
        {
            window?.Close();
            vm.Dispose();
            RxApp.MainThreadScheduler = originalScheduler;
        }
    }

    private static ListenerProfileSnapshot BuildThresholdSnapshotWithNewDimensions() => new()
    {
        TotalEventsIngested = ListeningProfileService.InjectionMinEvents,
        TopArtists = new[]
        {
            new ArtistAffinity { Artist = "周杰伦", Score = 5, PlayCount = 10 },
            new ArtistAffinity { Artist = "陈绮贞", Score = 3, PlayCount = 6 },
            new ArtistAffinity { Artist = "五月天", Score = 2, PlayCount = 4 },
        },
        Digest = new TasteDigestData { Text = "偏爱流行与氛围音乐", Language = "zh" },
        LanguageDistribution = new[]
        {
            new LanguageShare("zh", 0.65, 18),
            new LanguageShare("ja", 0.35, 10),
        },
        TimeBandPreferences = new Dictionary<string, TimeBandPreference>
        {
            ["morning"] = new("morning", 8, new[] { "五月天" }, new[] { "energetic" }, "zh"),
            ["night"] = new("night", 12, new[] { "坂本龙一" }, new[] { "calm" }, "ja"),
        },
    };

    private static async Task<(ListeningProfileService Service, string Dir)> CreateLoadedServiceAsync(
        Func<DateTime> clock,
        ILLMService? llm = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new ListeningProfileService(
            llm ?? new Mock<ILLMService>().Object,
            Path.Combine(dir, "listener-profile.json"),
            clock);
        await svc.LoadAsync(CancellationToken.None);
        return (svc, dir);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private sealed class FakeProfile : IListeningProfileService
    {
        public bool Enabled { get; set; } = true;
        public ListenerProfileSnapshot Snapshot { get; set; } = ListenerProfileSnapshot.Empty;
        public ListenerProfileSnapshot GetSnapshot() => Enabled ? Snapshot : ListenerProfileSnapshot.Empty;
        public void RecordEvent(ListeningEventData eventData) { }
        public void NotifyPlaybackStarted(Track track) { }
        public void NotifyPlaybackEndedNaturally(Track track) { }
        public void NotifyPositionSampled(Track track, TimeSpan position) { }
        public void NotifyTrackSwitched(Track? next) { }
        public void NotifyPlaybackPaused() { }
        public void Reset() { }
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshDigestAsync(CancellationToken cancellationToken, bool force = false) => Task.CompletedTask;
        public void RecordListenerChat(string userMessage) { }
        public Task RefreshImpressionAsync(CancellationToken cancellationToken, bool force = false) => Task.CompletedTask;
        public bool RemoveDislikeEvents(string? title, string? artist) => false;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
