using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;

namespace AIRadio.Desktop.Tests;

public class RecommendationServiceTests
{
    [Fact]
    public async Task CreateProgramAsync_ReturnsProgramWithPlayableAndUnavailableCandidates()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("夜晚华语 R&B\n晚间放松\nCity pop");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync((string query, int _) => new List<OnlineTrack>
            {
                new() { Id = $"netease:{query}:1", Title = $"{query} A", Artist = "Artist A", Source = "netease" },
                new() { Id = $"netease:{query}:2", Title = $"{query} B", Artist = "Artist B", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id.EndsWith(":1") ? $"http://example.com/{id}.mp3" : null);

        var program = await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntent = "想听适合晚上写代码的中文歌",
            Favorites = new[] { new Track { Title = "收藏歌", Artist = "收藏歌手", IsFavorite = true } }
        });

        Assert.Equal("想听适合晚上写代码的中文歌", program.Context.UserIntent);
        Assert.InRange(program.Tracks.Count, 1, 5);
        Assert.Contains(program.Tracks, item => item.IsPlayable && !string.IsNullOrWhiteSpace(item.PlayUrl));
        Assert.Contains(program.Tracks, item => !item.IsPlayable);
        Assert.All(program.Tracks, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
    }

    [Fact]
    public async Task RecordFeedback_ExcludesDislikedTrackFromNextProgram()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("ambient");
        search.Setup(x => x.SearchAsync("ambient", 8))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:skip", Title = "Skip Me", Artist = "Artist", Source = "netease" },
                new() { Id = "netease:keep", Title = "Keep Me", Artist = "Artist", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");

        service.RecordFeedback(new UserMusicFeedback
        {
            TrackId = "netease:skip",
            Action = MusicFeedbackAction.Dislike
        });

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "ambient" });

        Assert.DoesNotContain(program.Tracks, item => item.Track.SourceId == "netease:skip");
        Assert.Contains(program.Tracks, item => item.Track.SourceId == "netease:keep");
    }

    [Fact]
    public async Task CreateProgramAsync_ContinuesSearchingWhenEarlyCandidatesAreUnavailable()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("ambient");
        search.Setup(x => x.SearchAsync("ambient", 8))
            .ReturnsAsync(Enumerable.Range(1, 6)
                .Select(i => new OnlineTrack
                {
                    Id = $"netease:{i}",
                    Title = $"Candidate {i}",
                    Artist = "Artist",
                    Source = "netease"
                })
                .ToList());
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id == "netease:6" ? "http://example.com/playable.mp3" : null);

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "ambient" });

        Assert.Contains(program.Tracks, item => item.Track.SourceId == "netease:6" && item.IsPlayable);
    }

    [Fact]
    public async Task CreateProgramAsync_EmptySearchResults_ReturnsEmptyProgram()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("安静钢琴曲\n轻音乐");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "安静" });

        Assert.Empty(program.Tracks);
    }

    [Fact]
    public async Task SetMoodBias_NormalizesSynonym_OverridesDetectedMood_AndInjectsPromptHint()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        var prompts = new List<string>();
        minimax.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("钢琴曲");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        service.SetMoodBias("calmer"); // 电台指令常用 "calmer"，应归一化为 "calm"

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "随便来点" });

        Assert.Equal("calm", program.Context.Mood); // 覆盖正则检测（"随便来点" 本应检出 neutral）
        Assert.Contains(prompts, p => p.Contains("氛围偏好：calm"));
    }

    [Fact]
    public async Task SetMoodBias_FreeTextHint_DoesNotOverrideStructuredMood()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        var prompts = new List<string>();
        minimax.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("爵士");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        service.SetMoodBias("爵士咖啡馆"); // 自由词：只进搜索提示，不占结构化 mood

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "随便听听" });

        Assert.Equal("neutral", program.Context.Mood);
        Assert.Contains(prompts, p => p.Contains("氛围偏好：爵士咖啡馆"));
    }

    [Fact]
    public async Task SetMoodBias_Null_ClearsBias()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        var prompts = new List<string>();
        minimax.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("摇滚");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        service.SetMoodBias("energetic");
        service.SetMoodBias(null);

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "随便听听" });

        Assert.Equal("neutral", program.Context.Mood); // 回退正则检测
        Assert.DoesNotContain(prompts, p => p.Contains("氛围偏好"));
    }

    [Fact]
    public async Task CreateProgramAsync_UsesRecentPlaybackHistoryAsStyleContext()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("英伦摇滚");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntent = "继续当前电台",
            CurrentTrack = new Track { Title = "Yellow", Artist = "Coldplay" },
            RecentlyPlayed = new[]
            {
                new Track { Title = "Creep", Artist = "Radiohead" },
                new Track { Title = "Don't Look Back in Anger", Artist = "Oasis" }
            }
        });

        var prompt = Assert.Single(prompts);
        Assert.Contains("最近已播放", prompt);
        Assert.Contains("Creep - Radiohead", prompt);
        Assert.Contains("Don't Look Back in Anger - Oasis", prompt);
        Assert.Contains("共同风格", prompt);
    }

    [Fact]
    public async Task RecordPlayedTrack_DeduplicatesAndFeedsFutureRecommendationContext()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("英伦摇滚");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var creep = new Track { Id = "creep", Title = "Creep", Artist = "Radiohead" };
        var oasis = new Track { Id = "oasis", Title = "Wonderwall", Artist = "Oasis" };
        service.RecordPlayedTrack(creep);
        service.RecordPlayedTrack(oasis);
        service.RecordPlayedTrack(creep);

        await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        Assert.Equal(new[] { "Wonderwall", "Creep" }, service.RecentlyPlayed.Select(track => track.Title));
        var prompt = Assert.Single(prompts);
        Assert.Contains("Creep - Radiohead", prompt);
        Assert.Contains("Wonderwall - Oasis", prompt);
    }

    [Fact]
    public async Task CreateProgramAsync_SerializesConcurrentProgramMutations()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);
        var concurrentCalls = 0;
        var maxConcurrentCalls = 0;
        var sync = new object();
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(async () =>
            {
                var active = Interlocked.Increment(ref concurrentCalls);
                lock (sync)
                    maxConcurrentCalls = System.Math.Max(maxConcurrentCalls, active);
                await Task.Delay(40);
                Interlocked.Decrement(ref concurrentCalls);
                return "ambient";
            });
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        await Task.WhenAll(
            service.CreateProgramAsync(new RecommendationRequest { UserIntent = "first" }),
            service.CreateProgramAsync(new RecommendationRequest { UserIntent = "second" }));

        Assert.Equal(1, maxConcurrentCalls);
    }

    [Fact]
    public async Task CreateProgramAsync_English_LocalizesPromptAndProgramContent()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("british rock");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:test", Title = "Test", Artist = "Artist", Source = "网易" }
            });
        search.Setup(x => x.GetPlayUrlAsync("netease:test"))
            .ReturnsAsync("https://example.com/test.mp3");

        try
        {
            AppLanguage.Apply("en");
            var program = await service.CreateProgramAsync(new RecommendationRequest
            {
                UserIntent = "Continue current station"
            });

            Assert.Contains("Generate 3 short music-search queries", Assert.Single(prompts));
            Assert.StartsWith("Tuned for you:", program.Title);
            Assert.StartsWith("I've lined up", program.DjOpening);
            var item = Assert.Single(program.Tracks);
            Assert.Equal("NetEase Cloud Music", item.Source);
            Assert.DoesNotContain(item.Reason, character => character is >= '\u4e00' and <= '\u9fff');
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public void ApplyLocalization_TranslatesStoredContinueStationIntent()
    {
        var program = new RadioProgram
        {
            Context = new ListeningContext
            {
                UserIntent = "继续当前电台",
                TimeOfDay = "night"
            },
            Tracks =
            [
                new RecommendedTrack
                {
                    Track = new Track { Title = "Test", Artist = "Artist" },
                    IsPlayable = true,
                    Source = "网易"
                }
            ]
        };

        try
        {
            AppLanguage.Apply("en");
            RecommendationService.ApplyLocalization(program);

            Assert.Equal("Tuned for you: Continue current station", program.Title);
            Assert.DoesNotMatch("[\\u4e00-\\u9fff]", program.Tracks[0].Reason);
            Assert.Equal("NetEase Cloud Music", program.Tracks[0].Source);
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }
    [Fact]
    public void SanitizeSearchQueries_FiltersHostChatterAndKeepsKeywords()
    {
        // LLM 带人设时不守"只输出关键词"约定：台词按标点切开后，开场白碎片会混进搜索词
        // （线上实例：拿"哈喽～我是小音"当关键词搜出蹦迪曲并直接播放）
        var candidates = new[]
        {
            "哈喽～我是小音！",
            "根据你的口味",
            "为你准备了几首适合下午的歌",
            "1. 轻音乐",
            "久石让 钢琴",
            "日语 流行"
        };

        var result = RecommendationService.SanitizeSearchQueries(candidates);

        Assert.Equal(new[] { "轻音乐", "久石让 钢琴", "日语 流行" }, result);
    }

    [Fact]
    public void SanitizeSearchQueries_AllChatter_ReturnsEmptyForFallback()
    {
        var result = RecommendationService.SanitizeSearchQueries(new[] { "你好呀！", "我们继续听歌吧。" });
        Assert.Empty(result);
    }

    [Fact]
    public void SanitizeSearchQueries_KeepsEraKeywordsAndLongAsciiQueries()
    {
        // 年代关键词不能被列表序号清洗削头；纯 ASCII 关键词按更宽的长度上限；
        // 句尾语气词（吧/呢/哦…）是解说行尾巴，关键词不会这样收尾
        var result = RecommendationService.SanitizeSearchQueries(new[]
        {
            "90s city pop",
            "2020 华语金曲",
            "japanese city pop 80s funk",
            "陪你度过这个下午吧"
        });

        Assert.Equal(new[] { "90s city pop", "2020 华语金曲", "japanese city pop 80s funk" }, result);
    }

    // ---------- 长期收听画像注入 ----------

    private sealed class FakeListeningProfile : IListeningProfileService
    {
        public ListenerProfileSnapshot Snapshot { get; set; } = ListenerProfileSnapshot.Empty;
        public bool Enabled { get; set; } = true;
        public int RefreshDigestCalls;

        public ListenerProfileSnapshot GetSnapshot()
            => Enabled ? Snapshot : ListenerProfileSnapshot.Empty;

        public void RecordEvent(ListeningEventData eventData) { }
        public void NotifyPlaybackStarted(Track track) { }
        public void NotifyPlaybackEndedNaturally(Track track) { }
        public void NotifyPositionSampled(Track track, TimeSpan position) { }
        public void NotifyTrackSwitched(Track? next) { }
        public void NotifyPlaybackPaused() { }
        public void Reset() { }
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshDigestAsync(CancellationToken cancellationToken)
        {
            RefreshDigestCalls++;
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ListenerProfileSnapshot BuildThresholdSnapshot(
        string? digestText = null,
        IReadOnlyList<ProfileMusicRef>? blacklist = null)
        => new()
        {
            TotalEventsIngested = ListeningProfileService.InjectionMinEvents,
            TopArtists = new[]
            {
                new ArtistAffinity { Artist = "周杰伦", Score = 5, PlayCount = 10 },
                new ArtistAffinity { Artist = "陈绮贞", Score = 3, PlayCount = 6 },
                new ArtistAffinity { Artist = "五月天", Score = 2, PlayCount = 4 },
            },
            AvoidArtists = new[] { new ArtistAffinity { Artist = "差评歌手", Score = -3 } },
            DislikeBlacklist = blacklist ?? Array.Empty<ProfileMusicRef>(),
            Digest = digestText == null ? null : new TasteDigestData { Text = digestText, Language = "zh" },
        };

    [Fact]
    public async Task ProfileInjection_AddsDigestArtistsAndExplorationToPrompt()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var profile = new FakeListeningProfile
        {
            Snapshot = BuildThresholdSnapshot(digestText: "偏爱华语流行与 City Pop"),
        };
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("华语 流行\nCity pop\n新方向 关键词");
        SetupSearchWithPlayableTracks(search);
        var service = new RecommendationService(llm.Object, search.Object, profile);

        await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        var prompt = Assert.Single(prompts);
        // digest 与常听/回避歌手进入提示词；探索要求仅在画像注入时出现
        Assert.Contains("偏爱华语流行与 City Pop", prompt);
        Assert.Contains("周杰伦", prompt);
        Assert.Contains("差评歌手", prompt);
        Assert.True(prompt.Contains("画像之外") || prompt.Contains("adjacent to this profile"),
            $"prompt should require one exploratory query: {prompt}");
        // 节目单生成路径触发后台归纳
        Assert.True(profile.RefreshDigestCalls >= 1);
    }

    [Fact]
    public async Task ProfileBlacklist_ExcludesDislikedTrackByMusicIdentity()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var profile = new FakeListeningProfile
        {
            Snapshot = BuildThresholdSnapshot(blacklist: new[] { new ProfileMusicRef("不爱听", "某人") }),
        };
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("华语 流行");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync(new List<OnlineTrack>
            {
                // 与黑名单同曲不同源：仅凭音乐身份即应排除
                new() { Id = "kugou:123", Title = "不爱听", Artist = "某人", Source = "kugou" },
                new() { Id = "netease:456", Title = "保留曲", Artist = "别的歌手", Source = "netease" },
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");
        var service = new RecommendationService(llm.Object, search.Object, profile);

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "华语 流行" });

        Assert.DoesNotContain(program.Tracks, item => MusicIdentity.IsSameMusicIdentity(item.Track.Title, item.Track.Artist, "不爱听", "某人"));
        Assert.Contains(program.Tracks, item => item.Track.SourceId == "netease:456");
    }

    [Fact]
    public async Task ProfileBelowColdStartThreshold_PromptStaysUnchanged()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var profile = new FakeListeningProfile
        {
            Snapshot = new ListenerProfileSnapshot
            {
                TotalEventsIngested = 5,
                TopArtists = new[] { new ArtistAffinity { Artist = "周杰伦", Score = 1 } },
                Digest = new TasteDigestData { Text = "过早的结论", Language = "zh" },
            },
        };
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("华语 流行");
        SetupSearchWithPlayableTracks(search);
        var service = new RecommendationService(llm.Object, search.Object, profile);

        await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        var prompt = Assert.Single(prompts);
        Assert.DoesNotContain("过早的结论", prompt);
        Assert.DoesNotContain("长期常听歌手", prompt);
        Assert.DoesNotContain("长期回避歌手", prompt);
        Assert.False(prompt.Contains("画像之外") || prompt.Contains("adjacent to this profile"));
    }

    [Fact]
    public async Task ProfileDisabled_SnapshotEmptyAndBehaviorMatchesCurrent()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var profile = new FakeListeningProfile
        {
            Enabled = false,
            Snapshot = BuildThresholdSnapshot(digestText: "不该出现"),
        };
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("华语 流行");
        SetupSearchWithPlayableTracks(search);
        var service = new RecommendationService(llm.Object, search.Object, profile);

        await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "继续当前电台" });

        Assert.DoesNotContain("不该出现", Assert.Single(prompts));
    }

    [Fact]
    public async Task ProfileBlacklist_AppliesBelowColdStartThreshold()
    {
        // 黑名单不走冷启动门槛（Dislike 是显式信号）：候选搜索与节目单内筛选（IsDisliked）口径一致
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var profile = new FakeListeningProfile
        {
            Snapshot = new ListenerProfileSnapshot
            {
                TotalEventsIngested = 3,
                TopArtists = new[] { new ArtistAffinity { Artist = "某人", Score = -3 } },
                DislikeBlacklist = new[] { new ProfileMusicRef("不爱听", "某人") },
            },
        };
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:1", Title = "不爱听", Artist = "某人", Source = "netease" },
                new() { Id = "netease:2", Title = "保留曲", Artist = "别人", Source = "netease" },
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");
        var service = new RecommendationService(llm.Object, search.Object, profile);

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "华语" });
        Assert.DoesNotContain(program.Tracks, item => item.Track.SourceId == "netease:1");

        var next = await service.GetNextTrackAsync(new RecommendationRequest());
        Assert.NotEqual("netease:1", next?.SourceId);
    }

    private static void SetupSearchWithPlayableTracks(Mock<IMusicSearchService> search)
    {
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync((string query, int _) => new List<OnlineTrack>
            {
                new() { Id = $"netease:{query}:1", Title = $"{query} A", Artist = "Artist A", Source = "netease" },
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");
    }
}
