using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using System.Reactive.Linq;
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
    public void RemoveDislikeFeedback_RemovesMatchingVersionsAcrossSourcesAndPreservesOtherFeedback()
    {
        var service = new RecommendationService(new Mock<ILLMService>().Object, new Mock<IMusicSearchService>().Object);
        var dislike = new UserMusicFeedback { TrackId = "netease:a", Title = "Song", Artist = "Artist", Action = MusicFeedbackAction.Dislike };
        var sameSong = new UserMusicFeedback { TrackId = "kugou:a", Title = "Ｓｏｎｇ", Artist = "ARTIST", Action = MusicFeedbackAction.Dislike };
        var like = new UserMusicFeedback { TrackId = "netease:a", Title = "Song", Artist = "Artist", Action = MusicFeedbackAction.Like };
        var otherArtist = new UserMusicFeedback { TrackId = "netease:b", Title = "Song", Artist = "Other", Action = MusicFeedbackAction.Dislike };
        var otherSong = new UserMusicFeedback { TrackId = "netease:c", Title = "Other", Artist = "Artist", Action = MusicFeedbackAction.Dislike };
        foreach (var feedback in new[] { dislike, sameSong, like, otherArtist, otherSong })
            service.RecordFeedback(feedback);

        Assert.False(service.RemoveDislikeFeedback("Song", ""));
        Assert.True(service.RemoveDislikeFeedback("song", "artist"));
        Assert.Equal(new[] { like, otherArtist, otherSong }, service.FeedbackHistory);
        Assert.False(service.RemoveDislikeFeedback("song", "artist"));
    }

    [Fact]
    public async Task RemoveBlacklistItem_AllowsTrackInNewProgramWithinSameSession()
    {
        var llm = new Mock<ILLMService>();
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>())).ReturnsAsync("ambient");
        var search = new Mock<IMusicSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:forgiven", Title = "Forgiven", Artist = "Artist", Source = "netease" },
                new() { Id = "netease:other", Title = "Other", Artist = "Other Artist", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>())).ReturnsAsync((string id) => $"https://example.com/{id}.mp3");
        var profileFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"airadio-forgive-{System.Guid.NewGuid():N}.json");
        try
        {
            using var profile = new ListeningProfileService(llm.Object, profileFile);
            await profile.LoadAsync(CancellationToken.None);
            var service = new RecommendationService(llm.Object, search.Object, profile);
            service.RecordFeedback(new UserMusicFeedback
            {
                TrackId = "netease:forgiven", Title = "Forgiven", Artist = "Artist", Action = MusicFeedbackAction.Dislike
            });
            profile.RecordEvent(new ListeningEventData
            {
                SourceId = "netease:forgiven", Title = "Forgiven", Artist = "Artist", Type = ListeningEventType.Dislike
            });
            var request = new RecommendationRequest { UserIntent = "ambient" };
            var before = await service.CreateProgramAsync(request);
            Assert.DoesNotContain(before.Tracks, x => x.Track.SourceId == "netease:forgiven");

            using var vm = new SettingsViewModel(llm.Object, new Mock<ISecureStorage>().Object, profileFile + ".settings",
                listeningProfile: profile, recommendationService: service);
            await vm.RemoveBlacklistItemCommand.Execute(new ProfileBlacklistItem("Forgiven", "Artist", "Forgiven - Artist"));
            Assert.Empty(profile.GetSnapshot().DislikeBlacklist);
            var after = await service.CreateProgramAsync(request);

            Assert.Contains(after.Tracks, x => x.Track.SourceId == "netease:forgiven");
        }
        finally
        {
            System.IO.File.Delete(profileFile);
            System.IO.File.Delete(profileFile + ".bak");
        }
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
        // 搜索词必须指向具体歌曲而非风格氛围短语（氛围词会被当歌名匹配到 AIGC 同名曲）
        Assert.Contains("真实存在的具体歌曲", prompt);
        Assert.Contains("氛围描述短语", prompt);
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
        public Task RefreshDigestAsync(CancellationToken cancellationToken, bool force = false)
        {
            RefreshDigestCalls++;
            return Task.CompletedTask;
        }
        public void RecordListenerChat(string userMessage) { }
        public Task RefreshImpressionAsync(CancellationToken cancellationToken, bool force = false) => Task.CompletedTask;
        public bool RemoveDislikeEvents(string? title, string? artist) => false;
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

    private static List<OnlineTrack> MakeTracksForQuery(string query, int count) =>
        Enumerable.Range(1, count)
            .Select(i => new OnlineTrack
            {
                Id = $"netease:{query}:{i}",
                Title = $"{query} song {i}",
                Artist = $"Artist {query}",
                Source = "netease"
            })
            .ToList();

    [Fact]
    public async Task CreateProgramAsync_LimitsSlotsPerQuery_NoSingleQueryMonopolizes()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);

        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("qone\nqtwo\nqthree");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync((string query, int _) => MakeTracksForQuery(query, 4));
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");

        var program = await service.CreateProgramAsync(new RecommendationRequest());

        Assert.Equal(5, program.Tracks.Count);
        Assert.All(program.Tracks, item => Assert.True(item.IsPlayable));
        // 单查询限量：首个搜索词（往往锚当前在播歌手）最多占 2 席，其余席位流向后续查询
        Assert.Equal(2, program.Tracks.Count(item => item.Track.SourceId!.StartsWith("netease:qone:")));
        Assert.Equal(2, program.Tracks.Count(item => item.Track.SourceId!.StartsWith("netease:qtwo:")));
        // 第三词恰 1 席由全局 5 席上限保证，非限量证据
        Assert.Equal(1, program.Tracks.Count(item => item.Track.SourceId!.StartsWith("netease:qthree:")));
    }

    [Fact]
    public async Task CreateProgramAsync_BackfillsDeferredCandidates_DeduplicatesAndDefersResolution()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);

        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("qone\nqtwo");
        search.Setup(x => x.SearchAsync("qone", 8))
            .ReturnsAsync(Enumerable.Range(1, 5)
                .Select(i => new OnlineTrack
                {
                    Id = $"netease:qone:{i}",
                    Title = $"Song {i}",
                    Artist = "Artist One",
                    Source = "netease"
                })
                .ToList());
        // 与 qone:4 同 Id：Pass 1 抢先入单，回填阶段的 t4 被查重挡掉且不再解析
        search.Setup(x => x.SearchAsync("qtwo", 8))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:qone:4", Title = "R Track", Artist = "Artist Two", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");

        var program = await service.CreateProgramAsync(new RecommendationRequest());

        Assert.Equal(5, program.Tracks.Count);
        Assert.All(program.Tracks, item => Assert.True(item.IsPlayable));
        Assert.Equal(5, program.Tracks.Select(item => item.Track.SourceId).Distinct().Count());
        // qone 限量 2 + 回填 2，qtwo 的同曲占第 5 席
        Assert.Equal(
            new HashSet<string> { "Song 1", "Song 2", "Song 3", "Song 5", "R Track" },
            program.Tracks.Select(item => item.Track.Title).ToHashSet());
        // 惰性解析 + 回填查重在解析之前：同曲全程只解析一次（qtwo 抢先入单那次）
        search.Verify(x => x.GetPlayUrlAsync("netease:qone:4"), Times.Once);
    }

    [Fact]
    public async Task CreateProgramAsync_BackfillResolutionFailure_KeepsPlaceholderEntry()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);

        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("qone");
        search.Setup(x => x.SearchAsync("qone", 8))
            .ReturnsAsync(Enumerable.Range(1, 5)
                .Select(i => new OnlineTrack
                {
                    Id = $"netease:qone:{i}",
                    Title = $"Song {i}",
                    Artist = "Artist One",
                    Source = "netease"
                })
                .ToList());
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id == "netease:qone:5" ? null : $"http://example.com/{id}.mp3");

        var program = await service.CreateProgramAsync(new RecommendationRequest());

        // 延迟候选在回填阶段解析失败：按既有占位契约保留展示条目
        Assert.Equal(4, program.Tracks.Count(item => item.IsPlayable));
        var placeholder = Assert.Single(program.Tracks, item => !item.IsPlayable);
        Assert.Equal("Song 5", placeholder.Track.Title);
        Assert.False(string.IsNullOrWhiteSpace(placeholder.Reason));
    }

    [Fact]
    public async Task CreateProgramAsync_BackfillStopsAtProgramSize_NoExtraResolutions()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(llm.Object, search.Object);

        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("qone\nqtwo");
        search.Setup(x => x.SearchAsync("qone", 8))
            .ReturnsAsync((string query, int _) => MakeTracksForQuery(query, 7));
        search.Setup(x => x.SearchAsync("qtwo", 8))
            .ReturnsAsync((string query, int _) => MakeTracksForQuery(query, 1));
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");

        var program = await service.CreateProgramAsync(new RecommendationRequest());

        Assert.Equal(5, program.Tracks.Count);
        Assert.All(program.Tracks, item => Assert.True(item.IsPlayable));
        Assert.Equal(4, program.Tracks.Count(item => item.Track.SourceId!.StartsWith("netease:qone:")));
        // 凑满即停：多余延迟候选不再解析
        search.Verify(x => x.GetPlayUrlAsync(It.IsAny<string>()), Times.Exactly(5));
    }

    [Fact]
    public async Task GenerateQueries_DiversityInstruction_ForksByIntentKey()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());
        var prompts = new List<string>();
        llm.Setup(x => x.ChatAsync(Capture.In(prompts), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("anyquery");
        var service = new RecommendationService(llm.Object, search.Object);

        // UserIntentKey 必须显式设置：分叉刻意 key-only（生产构造点恒设 key），无 key 不命中
        await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.ContinueStation
        });
        Assert.Contains("不同方向", Assert.Single(prompts));
        Assert.Contains("风格参照", prompts[0]);

        prompts.Clear();
        await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.SimilarToCurrent
        });
        Assert.DoesNotContain("不同方向", Assert.Single(prompts));

        try
        {
            AppLanguage.Apply("en");
            prompts.Clear();
            await service.CreateProgramAsync(new RecommendationRequest
            {
                UserIntentKey = RecommendationIntentKeys.ContinueStation
            });
            Assert.Contains("style reference", Assert.Single(prompts));

            prompts.Clear();
            await service.CreateProgramAsync(new RecommendationRequest
            {
                UserIntentKey = RecommendationIntentKeys.SimilarToCurrent
            });
            Assert.DoesNotContain("style reference", Assert.Single(prompts));
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    private static RecommendationService CreateServiceWithFallbackPath(
        Mock<ILLMService> llm,
        Mock<IMusicSearchService> search,
        List<string> capturedQueries)
    {
        llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("llm down"));
        // 空结果：全部 fallback 词都会被搜索且无解析调用，捕获完整、顺序稳定
        search.Setup(x => x.SearchAsync(Capture.In(capturedQueries), 8))
            .ReturnsAsync(new List<OnlineTrack>());
        var service = new RecommendationService(llm.Object, search.Object);
        service.RecordPlayedTrack(new Track { Title = "歌一", Artist = "ArtistA" });
        service.RecordPlayedTrack(new Track { Title = "歌二", Artist = "ArtistB" });
        service.RecordPlayedTrack(new Track { Title = "歌三", Artist = "ArtistC" });
        return service;
    }

    [Fact]
    public async Task BuildFallbackQueries_ContinueStation_LeadsWithRecentArtistsInsteadOfIntentText()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var queries = new List<string>();
        var service = CreateServiceWithFallbackPath(llm, search, queries);

        await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.ContinueStation,
            CurrentTrack = new Track { Title = "不可一世", Artist = "Beyond" }
        });

        // 意图文案是 UI 文案不是歌名，不得再当搜索词
        Assert.DoesNotContain(queries, query => query.Contains("继续当前电台"));
        var recentIdx = queries.FindIndex(query => query.EndsWith("相似风格"));
        var currentIdx = queries.FindIndex(query => query.EndsWith("相似歌曲"));
        Assert.True(recentIdx >= 0, "应包含最近歌手方向词");
        Assert.True(currentIdx >= 0, "应包含当前歌手词");
        Assert.True(recentIdx < currentIdx, "继续电台意图下最近歌手词应打头");
    }

    [Fact]
    public async Task BuildFallbackQueries_SimilarToCurrent_KeepsCurrentArtistFirst()
    {
        var llm = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var queries = new List<string>();
        var service = CreateServiceWithFallbackPath(llm, search, queries);

        await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.SimilarToCurrent,
            CurrentTrack = new Track { Title = "不可一世", Artist = "Beyond" }
        });

        // SIM 语义=以当前歌为锚：当前歌手词仍打头。
        // 全词匹配防谓词漂移（画像词同以"相似歌曲"结尾；recentArtists>1 时画像词缺席）
        var currentIdx = queries.FindIndex(query => query == "Beyond 相似歌曲");
        var recentIdx = queries.FindIndex(query => query.EndsWith("相似风格"));
        Assert.True(currentIdx >= 0, "应包含当前歌手词");
        Assert.True(recentIdx >= 0, "应包含最近歌手方向词");
        Assert.True(currentIdx < recentIdx, "SIM 意图下当前歌手词应打头");
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
