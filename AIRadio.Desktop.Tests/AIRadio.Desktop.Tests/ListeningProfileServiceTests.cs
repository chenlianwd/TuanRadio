using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ListeningProfileServiceTests : IDisposable
{
    private readonly Mock<ILLMService> _llm = new();
    private readonly string _profileFile;
    private DateTime _now = new(2026, 9, 14, 12, 0, 0);

    public ListeningProfileServiceTests()
    {
        _profileFile = Path.Combine(Path.GetTempPath(), $"airadio-profile-{Guid.NewGuid():N}.json");
        // digest/impression 走 JSON 输出契约：默认返回锚定主解析路径（纯文本回退另有专项用例）
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("{\"summary\":\"偏好华语流行与 City Pop，深夜偏安静。\",\"tags\":[\"华语流行\",\"City Pop\",\"深夜\"]}");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_profileFile))
                File.Delete(_profileFile);
        }
        catch
        {
        }
    }

    private static void AssertEmptySnapshot(ListenerProfileSnapshot snapshot)
    {
        Assert.Equal(0, snapshot.TotalEventsIngested);
        Assert.Empty(snapshot.TopArtists);
        Assert.Empty(snapshot.AvoidArtists);
        Assert.Empty(snapshot.DislikeBlacklist);
        Assert.Empty(snapshot.MoodUsage);
        Assert.Null(snapshot.Digest);
        Assert.False(snapshot.MeetsInjectionThreshold);
    }

    private ListeningProfileService CreateService()
        => new(_llm.Object, _profileFile, () => _now);

    private static Track TrackOf(string title, string artist, int durationSeconds = 300)
        => new()
        {
            Title = title,
            Artist = artist,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            SourceId = $"netease:{title}"
        };

    private async Task<ListeningProfileService> CreateLoadedServiceAsync()
    {
        var service = CreateService();
        await service.LoadAsync(CancellationToken.None);
        return service;
    }

    // ---------- 统计聚合与权重 ----------

    [Fact]
    public async Task Snapshot_AggregatesWeightsPerArtist()
    {
        var service = await CreateLoadedServiceAsync();
        // 完整听一首（Played + Completed = +1.2）+ 点赞（+2）
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        service.NotifyPlaybackEndedNaturally(TrackOf("晴天", "周杰伦"));
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Like, Title = "晴天", Artist = "周杰伦" });
        // 跳过另一首（Played +0.2 + Skipped −0.8 = −0.6）
        service.NotifyPlaybackStarted(TrackOf("微风", "陈绮贞"));
        service.NotifyPositionSampled(TrackOf("微风", "陈绮贞"), TimeSpan.FromSeconds(30));
        service.NotifyTrackSwitched(TrackOf("其他", "别人"));

        var snapshot = service.GetSnapshot();

        var jay = Assert.Single(snapshot.TopArtists, a => a.Artist == "周杰伦");
        Assert.Equal(1.2 + 2.0, jay.Score, precision: 6);
        Assert.Equal(2, jay.PlayCount);
        // 陈绮贞净得分为负（−0.6），不进 TopArtists，也未低过 AvoidArtists 的 −1 门槛
        Assert.DoesNotContain(snapshot.TopArtists, a => a.Artist == "陈绮贞");
        Assert.DoesNotContain(snapshot.AvoidArtists, a => a.Artist == "陈绮贞");
    }

    [Fact]
    public async Task Snapshot_MoodEventsDoNotAffectArtistScore()
    {
        var service = await CreateLoadedServiceAsync();
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Calmer, Title = "晴天", Artist = "周杰伦" });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Energetic, Title = "晴天", Artist = "周杰伦" });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.MoodSet, Detail = "calm" });

        var snapshot = service.GetSnapshot();

        var jay = Assert.Single(snapshot.TopArtists, a => a.Artist == "周杰伦");
        Assert.Equal(0.2, jay.Score, precision: 6);
        // mood 历史：Calmer→calm、Energetic→energetic、MoodSet→Detail
        Assert.Equal(2, snapshot.MoodUsage["calm"]);
        Assert.Equal(1, snapshot.MoodUsage["energetic"]);
    }

    [Fact]
    public async Task Snapshot_DecaysOldEventsAndClampsArtistFloor()
    {
        var service = await CreateLoadedServiceAsync();
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        // 30 天后： Played 权重衰减一半
        _now = _now.AddDays(30);
        var snapshot = service.GetSnapshot();
        Assert.Equal(0.1, Assert.Single(snapshot.TopArtists).Score, precision: 6);

        // 多次 Dislike 连坐歌手分但有 −5 下限（黑名单走曲目级）
        var decayed = service;
        for (var i = 0; i < 5; i++)
            decayed.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = $"歌{i}", Artist = "差评歌手" });
        var after = decayed.GetSnapshot();
        var bad = Assert.Single(after.AvoidArtists, a => a.Artist == "差评歌手");
        Assert.Equal(-5.0, bad.Score, precision: 6);
    }

    // ---------- 黑名单 ----------

    [Fact]
    public async Task Blacklist_StoresLatestDislikeAndExpires()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "不爱听", Artist = "某人" });
        Assert.Single(service.GetSnapshot().DislikeBlacklist);

        // 179 天：未过期；181 天：过期
        _now = _now.AddDays(179);
        Assert.Single(service.GetSnapshot().DislikeBlacklist);
        _now = _now.AddDays(2);
        Assert.Empty(service.GetSnapshot().DislikeBlacklist);
    }

    [Fact]
    public async Task Blacklist_MatchesViaMusicIdentity()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "不爱听", Artist = "某人" });

        var snapshot = service.GetSnapshot();
        var entry = Assert.Single(snapshot.DislikeBlacklist);
        // 黑名单匹配走音乐身份：跨源同名同曲（标题归一化后一致）视为同一首
        Assert.True(MusicIdentity.IsSameMusicIdentity(entry.Title, entry.Artist, "不爱听", "某人"));
    }

    [Fact]
    public async Task Blacklist_EmptyArtistDislikeIsNotStored()
    {
        // 空歌手条目若入黑名单，会仅凭标题匹配所有同名曲（翻唱/伴奏），误伤面过大；
        // "未知艺术家"是具体字面值（只匹配同样标注的曲目），不属于此列
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "同名曲", Artist = "" });

        Assert.Empty(service.GetSnapshot().DislikeBlacklist);
    }

    // ---------- 跳过判定 ----------

    [Fact]
    public async Task Skip_RecordsSkippedEventExactly()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("长歌", "歌手", durationSeconds: 300);
        service.NotifyPlaybackStarted(track);
        _now = _now.AddSeconds(30);
        service.NotifyPositionSampled(track, TimeSpan.FromSeconds(60));
        service.NotifyTrackSwitched(TrackOf("下一首", "下一歌手"));

        // Played + Skipped 共 2 条事件
        Assert.Equal(2, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Skip_NotRecorded_AtBoundaryConditions()
    {
        var service = await CreateLoadedServiceAsync();

        // 进度恰 30%（不含）
        var t1 = TrackOf("歌1", "A", 300);
        service.NotifyPlaybackStarted(t1);
        service.NotifyPositionSampled(t1, TimeSpan.FromSeconds(90));
        service.NotifyTrackSwitched(TrackOf("别的1", "B"));
        _now = _now.AddMinutes(11);

        // 已播 <10s
        var t2 = TrackOf("歌2", "A", 300);
        service.NotifyPlaybackStarted(t2);
        service.NotifyPositionSampled(t2, TimeSpan.FromSeconds(9));
        service.NotifyTrackSwitched(TrackOf("别的2", "B"));
        _now = _now.AddMinutes(11);

        // 曲目时长 <60s
        var t3 = TrackOf("歌3", "A", 45);
        service.NotifyPlaybackStarted(t3);
        service.NotifyPositionSampled(t3, TimeSpan.FromSeconds(8));
        service.NotifyTrackSwitched(TrackOf("别的3", "B"));

        // 三次切换各记 1 条 Played，无 Skipped
        Assert.Equal(3, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Skip_NotRecorded_ForSameIdentityReplayOrNonPlayingSwitch()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("长歌", "歌手", 300);

        // 非播放态 TrackChanged（载入/删除场景）：无事件
        service.NotifyTrackSwitched(TrackOf("新曲", "新歌手"));
        Assert.Equal(0, service.GetSnapshot().TotalEventsIngested);

        // 播放后同曲重放（URL 恢复）：不记跳过且保留判定状态
        service.NotifyPlaybackStarted(track);
        service.NotifyPositionSampled(track, TimeSpan.FromSeconds(60));
        service.NotifyTrackSwitched(TrackOf("长歌", "歌手", 300));
        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);

        // 随后真正换曲：跳过按原样本判定
        service.NotifyTrackSwitched(TrackOf("别的", "别人"));
        Assert.Equal(2, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Skip_NotRecorded_WithoutPositionSampleForTrack()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("长歌", "歌手", 300);
        service.NotifyPlaybackStarted(track);
        // 无本曲样本（残留样本属于其它身份）
        service.NotifyPositionSampled(TrackOf("旧歌", "旧歌手"), TimeSpan.FromSeconds(999));
        service.NotifyTrackSwitched(TrackOf("别的", "别人"));

        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Skip_NotRecorded_AfterPause()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("长歌", "歌手", 300);
        service.NotifyPlaybackStarted(track);
        service.NotifyPositionSampled(track, TimeSpan.FromSeconds(60));
        service.NotifyPlaybackPaused();
        service.NotifyTrackSwitched(TrackOf("别的", "别人"));

        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Completed_ClearsPendingSkipState()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("长歌", "歌手", 300);
        service.NotifyPlaybackStarted(track);
        service.NotifyPositionSampled(track, TimeSpan.FromSeconds(60));
        // 自然播完优先于自动续播换曲
        service.NotifyPlaybackEndedNaturally(track);
        service.NotifyTrackSwitched(TrackOf("自动下一首", "电台"));

        // Played + Completed，无 Skipped
        Assert.Equal(2, service.GetSnapshot().TotalEventsIngested);
    }

    // ---------- Played 去重与事件上限 ----------

    [Fact]
    public async Task Played_DedupedWithinTenMinuteWindow()
    {
        var service = await CreateLoadedServiceAsync();
        var track = TrackOf("晴天", "周杰伦");

        service.NotifyPlaybackStarted(track); // 暂停后恢复会再次进入 Playing
        _now = _now.AddMinutes(2);
        service.NotifyPlaybackStarted(track);
        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);

        _now = _now.AddMinutes(9); // 距首次 11 分钟
        service.NotifyPlaybackStarted(track);
        Assert.Equal(2, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task EventCap_PrunesListButCounterStaysMonotonic()
    {
        var service = await CreateLoadedServiceAsync();
        for (var i = 0; i < ListeningProfileService.MaxEvents + 50; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = $"歌{i}",
                Artist = "歌手",
                Time = _now.AddSeconds(i),
            });

        var snapshot = service.GetSnapshot();
        Assert.Equal(ListeningProfileService.MaxEvents + 50, snapshot.TotalEventsIngested);
    }

    // ---------- 开关 ----------

    [Fact]
    public async Task Disabled_StopsIngestAndReturnsEmptySnapshot()
    {
        var service = await CreateLoadedServiceAsync();
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        Assert.Single(service.GetSnapshot().TopArtists);

        service.Enabled = false;
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Like, Title = "晴天", Artist = "周杰伦" });
        AssertEmptySnapshot(service.GetSnapshot());

        // 重开后数据仍在
        service.Enabled = true;
        Assert.Single(service.GetSnapshot().TopArtists);
    }

    // ---------- digest ----------

    private async Task SeedDigestReadyServiceAsync(ListeningProfileService service)
    {
        for (var i = 0; i < ListeningProfileService.InjectionMinEvents; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Completed,
                Title = $"歌{i % 10}",
                Artist = $"歌手{i % 3}",
                Time = _now.AddMinutes(-i),
            });
    }

    [Fact]
    public async Task Digest_GeneratesOnceAndRespectsWatermark()
    {
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        await service.RefreshDigestAsync(CancellationToken.None);
        Assert.NotNull(service.GetSnapshot().Digest);

        // 无新增事件再触发：不再调用 LLM
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);

        // 水位 +25：再次归纳
        for (var i = 0; i < ListeningProfileService.InjectionMinEvents + 25; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = $"新歌{i % 5}",
                Artist = $"歌手{i % 3}",
                Time = _now.AddMinutes(-i),
            });
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Digest_BelowThreshold_DoesNotCallLlm()
    {
        var service = await CreateLoadedServiceAsync();
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));

        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Never);
        Assert.False(service.GetSnapshot().MeetsInjectionThreshold);
    }

    // 语言切换触发的归纳失败后不锁死：语言差异维度与水位/老化触发同待遇，由连续失败
    // 退避统一限流，下一个事件可重试（曾有 _languageRegenDone 前置置位，失败后整会话
    // 不再因语言差异重试）
    [Fact]
    public async Task Digest_LanguageSwitch_FailureDoesNotLockRetry()
    {
        var original = AppLanguage.Current;
        var calls = 0;
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(() =>
            {
                calls++;
                return calls == 2
                    ? Task.FromException<string>(new InvalidOperationException("boom"))
                    : Task.FromResult("摘要");
            });
        try
        {
            AppLanguage.Apply("zh");
            var service = await CreateLoadedServiceAsync();
            await SeedDigestReadyServiceAsync(service);

            // zh 首次归纳成功
            await service.RefreshDigestAsync(CancellationToken.None);
            Assert.NotNull(service.GetSnapshot().Digest);

            // 切英文触发语言差异归纳，LLM 失败
            AppLanguage.Apply("en");
            await service.RefreshDigestAsync(CancellationToken.None);
            _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(2));

            // 无水位/老化变化，语言差异仍成立：失败不锁死，新事件后重试并成功
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = "新歌",
                Artist = "歌手",
                Time = _now,
            });
            await service.RefreshDigestAsync(CancellationToken.None);
            _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));
        }
        finally
        {
            AppLanguage.Apply(original);
        }
    }

    [Fact]
    public async Task Digest_FailuresBackOffAfterThreeStrikes()
    {        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        await service.RefreshDigestAsync(CancellationToken.None);
        await service.RefreshDigestAsync(CancellationToken.None);
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));

        // 退避期内不再调用
        _now = _now.AddHours(1);
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));

        // 24 小时后恢复尝试
        _now = _now.AddHours(24);
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Digest_ConcurrentRefreshRunsOnce()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(() => gate.Task);
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        var first = service.RefreshDigestAsync(CancellationToken.None);
        await Task.Yield();
        var second = service.RefreshDigestAsync(CancellationToken.None);
        await second; // 在途时到达的触发直接返回

        gate.SetResult("摘要");
        await first;

        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
        Assert.NotNull(service.GetSnapshot().Digest);
    }

    [Fact]
    public async Task Digest_ResultAfterResetIsDiscarded()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(() => gate.Task);
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        var refresh = service.RefreshDigestAsync(CancellationToken.None);
        await Task.Yield();
        service.Reset(); // 归纳在途时清除画像
        gate.SetResult("过期摘要");
        await refresh;

        Assert.Null(service.GetSnapshot().Digest);
    }

    // ---------- 手动重新归纳（force） ----------

    [Fact]
    public async Task RefreshDigest_Force_BypassesWatermarkAndAge()
    {
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        await service.RefreshDigestAsync(CancellationToken.None);
        Assert.NotNull(service.GetSnapshot().Digest);

        // 无水位/老化/语言变化：非 force 不再调 LLM（既有触发语义锚定）
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);

        // force 绕过触发条件仍归纳
        await service.RefreshDigestAsync(CancellationToken.None, force: true);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RefreshDigest_Force_BypassesBackoffWindow()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        for (var i = 0; i < 3; i++)
            await service.RefreshDigestAsync(CancellationToken.None);
        var calls = 3;

        // 退避窗内非 force 不再调用
        _now = _now.AddHours(1);
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(calls));

        // force 穿透退避窗口重试
        await service.RefreshDigestAsync(CancellationToken.None, force: true);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(calls + 1));
    }

    [Fact]
    public async Task RefreshDigest_ForceFailure_CountsIntoSharedBackoff()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        // force 失败与自动触发共享同一退避计数
        for (var i = 0; i < 3; i++)
            await service.RefreshDigestAsync(CancellationToken.None, force: true);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));

        // 退避窗内非 force 被挡；24 小时后恢复
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));
        _now = _now.AddHours(24);
        await service.RefreshDigestAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(4));
    }

    [Fact]
    public async Task RefreshDigest_Force_BelowThreshold_DoesNotCallLlm()
    {
        var service = await CreateLoadedServiceAsync();
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));

        await service.RefreshDigestAsync(CancellationToken.None, force: true);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDigest_Force_WhileInFlight_ReturnsWithoutSecondCall()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(() => gate.Task);
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);

        var first = service.RefreshDigestAsync(CancellationToken.None);
        await Task.Yield();
        // 在途归纳未完成时 force 到达：直接返回不抛、不二次调 LLM
        var forced = service.RefreshDigestAsync(CancellationToken.None, force: true);
        await forced;

        gate.SetResult("摘要");
        await first;

        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
        Assert.NotNull(service.GetSnapshot().Digest);
    }

    // ---------- 黑名单单条移除 ----------

    [Fact]
    public async Task RemoveDislikeEvents_RemovesMatchingVariantsAndKeepsOthers()
    {
        var service = await CreateLoadedServiceAsync();
        // 播种顺序影响增量 clamp：正向事件先积累，Dislike 收尾压入回避名单。
        // 同键两条（raw 含书名号/尾随空白，归一化折叠同键）+ 同歌手其它标题 + 同标题其它歌手；
        // Skipped 与 Completed 是必须保留的非 Dislike 事件
        for (var i = 0; i < 4; i++)
            service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Completed, Title = "七里香", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Skipped, Title = "晴天", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "《晴天》 ", Artist = "周杰伦", Time = _now.AddMinutes(1) });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "告白气球", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "林俊杰", Time = _now });

        var before = service.GetSnapshot();
        // 周杰伦：+4 −0.8 → +3.2，三连 Dislike 压到 −5.8 → 增量 clamp −5，进回避名单；
        // 同键 Dislike 折叠后黑名单 3 条
        Assert.Contains(before.AvoidArtists, a => a.Artist == "周杰伦");
        Assert.Equal(3, before.DislikeBlacklist.Count);

        Assert.True(service.RemoveDislikeEvents("晴天", "周杰伦"));

        var after = service.GetSnapshot();
        // 「只赦免这首歌」：同歌手其它标题、同标题其它歌手的 Dislike 均保留
        Assert.Equal(2, after.DislikeBlacklist.Count);
        Assert.Contains(after.DislikeBlacklist, x => x.Title == "告白气球" && x.Artist == "周杰伦");
        Assert.Contains(after.DislikeBlacklist, x => x.Title == "晴天" && x.Artist == "林俊杰");
        // Skipped(−0.8) 保留：移除两条 Dislike 后 −3 −0.8 +4 = +0.2（若误删 Skipped 则为 +1.0）
        var jay = Assert.Single(after.TopArtists, a => a.Artist == "周杰伦");
        Assert.Equal(0.2, jay.Score, precision: 6);
        Assert.DoesNotContain(after.AvoidArtists, a => a.Artist == "周杰伦");
    }

    [Fact]
    public async Task RemoveDislikeEvents_EmptyOrNullArtist_ReturnsFalse()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "周杰伦", Time = _now });

        // 空歌手键永不匹配（镜像黑名单构建条件）；null 入参安全
        Assert.False(service.RemoveDislikeEvents("晴天", " "));
        Assert.False(service.RemoveDislikeEvents(null, null));
        Assert.Single(service.GetSnapshot().DislikeBlacklist);
    }

    [Fact]
    public async Task RemoveDislikeEvents_EmptyArtistEvent_GuardBlocksRemoval()
    {
        // 空歌手前置守卫的变异锚：没有守卫时空歌手事件会被谓词匹配删掉并返回 true
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "", Time = _now });
        // 空歌手本就不入黑名单（构建条件），但事件本体保留
        Assert.Empty(service.GetSnapshot().DislikeBlacklist);
        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);

        Assert.False(service.RemoveDislikeEvents("晴天", ""));
        Assert.Equal(1, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task RemoveDislikeEvents_MarksDirtyForDisposeFlush()
    {
        // 移除必须自己置 _dirty：主动落盘清掉 dirty 后移除、再无新事件时，
        // 只有移除置的 dirty 才能让退出兜底落盘带上删除（否则移除静默丢失）
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "告白气球", Artist = "周杰伦", Time = _now });
        await service.FlushAsync(CancellationToken.None);

        Assert.True(service.RemoveDislikeEvents("晴天", "周杰伦"));
        service.Dispose(); // 同步有界兜底落盘（FlushAsync().Wait）

        var reloaded = new ListeningProfileService(_llm.Object, _profileFile, () => _now);
        await reloaded.LoadAsync(CancellationToken.None);
        var entry = Assert.Single(reloaded.GetSnapshot().DislikeBlacklist);
        Assert.Equal("告白气球", entry.Title);
        reloaded.Dispose();
    }

    [Fact]
    public async Task RemoveDislikeEvents_NoMatchingEvent_ReturnsFalse()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "周杰伦", Time = _now });

        Assert.False(service.RemoveDislikeEvents("七里香", "周杰伦"));
        Assert.False(service.RemoveDislikeEvents("晴天", "林俊杰"));
        Assert.Single(service.GetSnapshot().DislikeBlacklist);
    }

    [Fact]
    public async Task RemoveDislikeEvents_EmptyTitleEntry_IsRemovable()
    {
        // 空标题是合法黑名单键（构建条件只要求歌手非空），不得被守卫拦截成永久不可移除
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "", Artist = "周杰伦", Time = _now });
        Assert.Single(service.GetSnapshot().DislikeBlacklist);

        Assert.True(service.RemoveDislikeEvents("", "周杰伦"));
        Assert.Empty(service.GetSnapshot().DislikeBlacklist);
    }

    [Fact]
    public async Task RemoveDislikeEvents_PersistsAfterFlushAndReload()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "晴天", Artist = "周杰伦", Time = _now });
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "告白气球", Artist = "周杰伦", Time = _now });
        Assert.True(service.RemoveDislikeEvents("晴天", "周杰伦"));
        await service.FlushAsync(CancellationToken.None);
        service.Dispose();

        var reloaded = new ListeningProfileService(_llm.Object, _profileFile, () => _now);
        await reloaded.LoadAsync(CancellationToken.None);
        var entry = Assert.Single(reloaded.GetSnapshot().DislikeBlacklist);
        Assert.Equal("告白气球", entry.Title);
        reloaded.Dispose();
    }

    // ---------- 持久化 ----------

    [Fact]
    public async Task Persistence_RoundTripsEventsDigestAndCounter()
    {
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);
        service.RecordEvent(new ListeningEventData { Type = ListeningEventType.Dislike, Title = "不爱听", Artist = "差评歌手" });
        await service.RefreshDigestAsync(CancellationToken.None);
        await service.FlushAsync(CancellationToken.None);
        service.Dispose();

        var reloaded = new ListeningProfileService(_llm.Object, _profileFile, () => _now);
        await reloaded.LoadAsync(CancellationToken.None);
        var snapshot = reloaded.GetSnapshot();

        Assert.Equal(ListeningProfileService.InjectionMinEvents + 1, snapshot.TotalEventsIngested);
        Assert.Single(snapshot.DislikeBlacklist);
        Assert.NotNull(snapshot.Digest);
        Assert.True(snapshot.MeetsInjectionThreshold);
    }

    [Fact]
    public async Task Persistence_CorruptFileDegradesToEmptyAndOverwrites()
    {
        await File.WriteAllTextAsync(_profileFile, "{ not valid json !!");
        var service = CreateService();
        await service.LoadAsync(CancellationToken.None);

        AssertEmptySnapshot(service.GetSnapshot());

        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        await service.FlushAsync(CancellationToken.None);
        Assert.True(File.Exists(_profileFile));

        var reloaded = new ListeningProfileService(_llm.Object, _profileFile, () => _now);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Equal(1, reloaded.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Persistence_FutureVersionRunsEmptyAndRefusesIngest()
    {
        await File.WriteAllTextAsync(_profileFile,
            """{ "Version": 99, "TotalEventsIngested": 5, "Events": [], "Digest": null }""");
        var service = CreateService();
        await service.LoadAsync(CancellationToken.None);

        AssertEmptySnapshot(service.GetSnapshot());
        service.NotifyPlaybackStarted(TrackOf("晴天", "周杰伦"));
        Assert.Equal(0, service.GetSnapshot().TotalEventsIngested);
    }

    [Fact]
    public async Task Reset_ClearsMemoryAndDeletesFile()
    {
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);
        await service.FlushAsync(CancellationToken.None);
        Assert.True(File.Exists(_profileFile));

        service.Reset();

        AssertEmptySnapshot(service.GetSnapshot());
        Assert.False(File.Exists(_profileFile));
    }

    // ---------- digest JSON 结构化输出 ----------

    [Fact]
    public async Task Digest_ParsesJsonOutputIntoTextAndStyleTags()
    {
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);
        await service.RefreshDigestAsync(CancellationToken.None);
        var digest = service.GetSnapshot().Digest;
        Assert.NotNull(digest);
        // JSON 输出契约：Text 恒为解析后的 summary 纯文本（绝不存原始 JSON 串），tags 逐个落位
        Assert.Equal("偏好华语流行与 City Pop，深夜偏安静。", digest.Text);
        Assert.Equal(new List<string> { "华语流行", "City Pop", "深夜" }, digest.StyleTags);
    }

    [Fact]
    public async Task Digest_PlainTextReplyFallsBackToTextWithoutTags()
    {
        // 本地小模型偶尔回纯文本：回退不损失归纳（tags 空即可），不算失败
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("纯文本归纳：偏爱华语流行。");
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service);
        await service.RefreshDigestAsync(CancellationToken.None);
        var digest = service.GetSnapshot().Digest;
        Assert.NotNull(digest);
        Assert.Equal("纯文本归纳：偏爱华语流行。", digest.Text);
        Assert.Empty(digest.StyleTags);
    }

    // ---------- LLM 归纳输出宽容解析（digest 与 impression 共用） ----------

    [Theory]
    [InlineData("{\"summary\":\"总评\",\"tags\":[\"古风\",\"流行\"]}", "总评", "古风|流行")]
    [InlineData("{\"summary\":\"总评\",\"traits\":[\"直接\"]}", "总评", "直接")]
    [InlineData("```json\n{\"summary\":\"围栏内\",\"tags\":[\"a\"]}\n```", "围栏内", "a")]
    [InlineData("纯文本没有花括号", "纯文本没有花括号", "")]
    [InlineData("前缀 {\"summary\":\"嵌在中间\",\"tags\":[\"x\"]} 后缀", "嵌在中间", "x")]
    [InlineData("", "", "")]
    public void ParseLlmSummary_ExtractsSummaryAndTags(string reply, string expectedSummary, string expectedTags)
    {
        var parsed = ListeningProfileService.ParseLlmSummary(reply, 400, 8, 10);
        Assert.Equal(expectedSummary, parsed.Summary);
        Assert.Equal(
            expectedTags.Length == 0 ? new List<string>() : expectedTags.Split('|').ToList(),
            parsed.Tags);
    }

    [Theory]
    [InlineData("{bad json", "{bad json")]
    public void ParseLlmSummary_UnparsableTextFallsBackToWholeText(string reply, string fallbackText)
    {
        // 花括号切取的子串不是合法 JSON（纯文本含花括号）：整串回退纯文本（原文作 summary）
        var parsed = ListeningProfileService.ParseLlmSummary(reply, 400, 8, 10);
        Assert.Equal(fallbackText, parsed.Summary);
        Assert.Empty(parsed.Tags);
    }

    [Theory]
    [InlineData("{\"summary\":123}")]
    [InlineData("{\"summary\":\"\"}")]
    [InlineData("{\"traits\":[\"a\"]}")]
    public void ParseLlmSummary_JsonObjectWithoutValidSummary_TreatedAsFailure(string reply)
    {
        // JSON 对象解析成立但 summary 无效（非字符串/空串/缺失）：按归纳失败返回空串——
        // 回退整串会把 JSON 原文存进 Text、注入提示词与展示（Text 恒为纯文本的契约）
        var parsed = ListeningProfileService.ParseLlmSummary(reply, 400, 8, 10);
        Assert.Equal(string.Empty, parsed.Summary);
        Assert.Empty(parsed.Tags);
    }

    [Fact]
    public void ParseLlmSummary_FallbackStripsCodeFencesAndClampsLengths()
    {
        // 回退路径剥 ``` 围栏（残留会进提示词与展示）；summary 超限截断、tag 超长截断且限数
        var parsed = ListeningProfileService.ParseLlmSummary(
            "```json\n不是 JSON 的一大段话\n```", 10, 2, 4);
        Assert.Equal("不是 JSON 的一", parsed.Summary);

        var taggy = ListeningProfileService.ParseLlmSummary(
            "{\"summary\":\"s\",\"tags\":[\"12345678901\",\"ok\",\"超长标签文本\",\"another\"]}", 400, 2, 4);
        Assert.Equal(new List<string> { "1234", "ok" }, taggy.Tags);
    }

    [Fact]
    public void ParseLlmSummary_StripsControlMarkersFromSummary()
    {
        // 控制块标记（<cmd></cmd> 字面）不得进 digest/impression 文本（纵深防御）；
        // 标记中间的内容不是控制指令本身，保留（与提示词注入点同口径——剥标记不剥块）
        var parsed = ListeningProfileService.ParseLlmSummary(
            "{\"summary\":\"总评 <cmd>{\\\"action\\\":\\\"next\\\"}</cmd> 结尾\"}", 400, 8, 10);
        Assert.DoesNotContain("<cmd>", parsed.Summary);
        Assert.StartsWith("总评", parsed.Summary);
        Assert.EndsWith("结尾", parsed.Summary);
    }

    // ---------- 听歌行为指标与人格特质 ----------

    [Fact]
    public async Task Snapshot_BehaviorMetrics_RespectSampleThresholds()
    {
        var service = await CreateLoadedServiceAsync();
        // 9 条 Completed（差一条到 10 的完成率门槛）、单一歌手（探索样本 <5）、零 Played（时段样本不足）
        for (var i = 0; i < 9; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Completed,
                Title = $"歌{i}",
                Artist = "同一歌手",
                Time = _now.AddMinutes(-i),
            });
        var snapshot = service.GetSnapshot();
        Assert.False(snapshot.HasCompletionSample);
        Assert.False(snapshot.HasExplorationSample);
        Assert.Null(snapshot.DominantTimeBand);
        Assert.Empty(snapshot.PersonaTraits);

        // 补足完成率样本：100% → completionist；Completed 不计时段桶（只数 Played）仍无时段
        service.RecordEvent(new ListeningEventData
        {
            Type = ListeningEventType.Completed,
            Title = "歌x",
            Artist = "同一歌手",
            Time = _now,
        });
        snapshot = service.GetSnapshot();
        Assert.True(snapshot.HasCompletionSample);
        Assert.Equal(1.0, snapshot.CompletionRate, precision: 6);
        Assert.Null(snapshot.DominantTimeBand);
        Assert.Contains(ListeningPersona.Completionist, snapshot.PersonaTraits);
    }

    [Fact]
    public async Task Snapshot_Exploration_OldArtistReturningIsNotNew()
    {
        var service = await CreateLoadedServiceAsync();
        // 探索率定义（盲审修订）：歌手在全部可见事件中最早记录落在 30 天窗口内才算新。
        // 31 天前常听过（最早记录在窗口外）、窗口内回访 → 不算新歌手
        var old = new DateTime(2026, 8, 10, 12, 0, 0);
        service.RecordEvent(new ListeningEventData
        {
            Type = ListeningEventType.Completed, Title = "老歌", Artist = "老歌手", Time = old,
        });
        // 老歌手窗口内回访（否则根本不进窗口歌手集合，测试就失去"回访不判新"的意义）
        service.RecordEvent(new ListeningEventData
        {
            Type = ListeningEventType.Completed, Title = "老歌2", Artist = "老歌手", Time = _now.AddHours(-3),
        });
        for (var i = 0; i < 5; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Completed,
                Title = $"窗口歌{i}",
                Artist = $"窗口歌手{i}",
                Time = _now.AddHours(-i),
            });
        // 窗口歌手共 6 个（5 新 + 老歌手回访），新歌手占比 5/6
        var snapshot = service.GetSnapshot();
        Assert.True(snapshot.HasExplorationSample);
        Assert.Equal(5.0 / 6, snapshot.NewArtistRatio, precision: 6);
        Assert.Contains(ListeningPersona.Explorer, snapshot.PersonaTraits);
    }

    [Fact]
    public async Task Snapshot_DominantTimeBand_NightPlaysGetNightOwl()
    {
        var service = await CreateLoadedServiceAsync();
        // 10 次 Played：8 次深夜 23 点 + 2 次中午（_now 是 12 点 → afternoon）
        var nightTime = new DateTime(2026, 9, 20, 23, 30, 0);
        for (var i = 0; i < 8; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = $"夜歌{i}",
                Artist = $"夜歌手{i}",
                Time = nightTime.AddMinutes(-i),
            });
        for (var i = 0; i < 2; i++)
            service.RecordEvent(new ListeningEventData
            {
                Type = ListeningEventType.Played,
                Title = $"日歌{i}",
                Artist = $"日歌手{i}",
                Time = _now.AddHours(-i),
            });
        var snapshot = service.GetSnapshot();
        Assert.Equal("night", snapshot.DominantTimeBand);
        Assert.Contains(ListeningPersona.NightOwl, snapshot.PersonaTraits);
    }

    [Theory]
    [InlineData(0.70, ListeningPersona.Completionist)]
    [InlineData(0.69, "")]
    [InlineData(0.40, ListeningPersona.Skipper)]
    [InlineData(0.41, "")]
    public void DerivePersonaTraits_CompletionRateBoundaries(double rate, string expected)
    {
        var traits = ListeningPersona.DerivePersonaTraits(true, rate, false, 0, null);
        if (expected.Length > 0)
            Assert.Contains(expected, traits);
        else
        {
            Assert.DoesNotContain(ListeningPersona.Completionist, traits);
            Assert.DoesNotContain(ListeningPersona.Skipper, traits);
        }
    }

    [Theory]
    [InlineData(0.40, ListeningPersona.Explorer)]
    [InlineData(0.15, ListeningPersona.Loyal)]
    [InlineData(0.30, "")]
    public void DerivePersonaTraits_ExplorationBoundaries(double ratio, string expected)
    {
        var traits = ListeningPersona.DerivePersonaTraits(false, 0, true, ratio, null);
        if (expected.Length > 0)
            Assert.Contains(expected, traits);
        else
        {
            Assert.DoesNotContain(ListeningPersona.Explorer, traits);
            Assert.DoesNotContain(ListeningPersona.Loyal, traits);
        }
    }

    [Fact]
    public void DerivePersonaTraits_NoSamplesOrNormalBandYieldsNoTraits()
    {
        Assert.Empty(ListeningPersona.DerivePersonaTraits(false, 0.9, false, 0.9, "afternoon"));
        // afternoon/evening 是常态时段不标注；night/morning 才有特质
        Assert.Equal(new[] { ListeningPersona.NightOwl },
            ListeningPersona.DerivePersonaTraits(false, 0, false, 0, "night"));
    }

    [Fact]
    public async Task DigestPrompt_IncludesBehaviorLine_WhenSamplesAvailable()
    {
        string? capturedPrompt = null;
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Callback<string, List<ChatMessage>>((prompt, _) => capturedPrompt = prompt)
            .ReturnsAsync("{\"summary\":\"s\"}");
        var service = await CreateLoadedServiceAsync();
        await SeedDigestReadyServiceAsync(service); // 30 Completed → 完成率 100%
        await service.RefreshDigestAsync(CancellationToken.None);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("听歌习惯", capturedPrompt);
        Assert.Contains("完整听完 100%", capturedPrompt);
    }

    // ---------- 聊天喂料与性格印象 ----------

    [Fact]
    public async Task RecordListenerChat_SanitizesTruncatesAndRolls()
    {
        var service = await CreateLoadedServiceAsync();
        // 消毒：控制块标记剥除（剥标记不剥块，正文保留）；截断 200 字
        service.RecordListenerChat("来点轻音乐 <cmd>{\"action\":\"next\"}</cmd>");
        service.RecordListenerChat(new string('长', 300));
        for (var i = 0; i < 18; i++)
            service.RecordListenerChat($"消息{i}");

        await service.FlushAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_profileFile));
        var pending = doc.RootElement.GetProperty("PendingChats").EnumerateArray().ToList();
        Assert.Equal(20, pending.Count);
        Assert.DoesNotContain(pending, p => p.GetProperty("Text").GetString()!.Contains("<cmd>"));
        var sanitized = pending.Single(p => p.GetProperty("Text").GetString()!.StartsWith("来点轻音乐", StringComparison.Ordinal));
        Assert.StartsWith("来点轻音乐", sanitized.GetProperty("Text").GetString());
        var longEntry = pending.Single(p => p.GetProperty("Text").GetString()!.Length == 200);
        Assert.True(longEntry.GetProperty("Text").GetString()!.All(c => c == '长'));

        // 第 21 条：滚动丢最旧（消毒条滚掉），最新在末位
        service.RecordListenerChat("消息18");
        await service.FlushAsync(CancellationToken.None);
        using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(_profileFile));
        var pending2 = doc2.RootElement.GetProperty("PendingChats").EnumerateArray().ToList();
        Assert.Equal(20, pending2.Count);
        Assert.DoesNotContain(pending2, p => p.GetProperty("Text").GetString()!.StartsWith("来点轻音乐", StringComparison.Ordinal));
        Assert.Equal("消息18", pending2[^1].GetProperty("Text").GetString());
    }

    [Fact]
    public async Task RecordListenerChat_Disabled_DoesNotPersist()
    {
        var service = await CreateLoadedServiceAsync();
        service.RecordListenerChat("正常消息");
        await service.FlushAsync(CancellationToken.None);
        service.Enabled = false;
        // 关闭「学习我的口味」：聊天原文比收听事件更敏感，停采契约严格适用——不落盘
        service.RecordListenerChat("私密的聊天内容");
        service.Enabled = true;

        await service.FlushAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_profileFile));
        var texts = doc.RootElement.GetProperty("PendingChats").EnumerateArray()
            .Select(p => p.GetProperty("Text").GetString()).ToList();
        Assert.Equal(new[] { "正常消息" }, texts);
    }

    [Fact]
    public async Task Impression_TriggeredAtWatermark_AndParsed()
    {
        var service = await CreateLoadedServiceAsync();
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("{\"summary\":\"直接爽快，偏好点歌。\",\"traits\":[\"直接\",\"爱点歌\"]}");

        // 15 条：不足水位不归纳
        for (var i = 0; i < 15; i++)
        {
            service.RecordListenerChat($"消息{i}");
            await service.RefreshImpressionAsync(CancellationToken.None);
        }
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Never);

        service.RecordListenerChat("消息15");
        await service.RefreshImpressionAsync(CancellationToken.None);

        var impression = service.GetSnapshot().Impression;
        Assert.NotNull(impression);
        Assert.Equal("直接爽快，偏好点歌。", impression.Text);
        Assert.Equal(new List<string> { "直接", "爱点歌" }, impression.Traits);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);
    }

    [Fact]
    public async Task Impression_SuccessClearsToWatermark_InFlightMessagesSurvive()
    {
        // 归纳（≤15s）在途时用户继续聊天：新消息既没进本次归纳、也必须留在素材区待下轮
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(async (string prompt, List<ChatMessage> _) =>
            {
                await gate.Task;
                return "{\"summary\":\"归纳结果\"}";
            });
        var service = await CreateLoadedServiceAsync();
        for (var i = 0; i < 16; i++)
            service.RecordListenerChat($"消息{i}");

        var run = service.RefreshImpressionAsync(CancellationToken.None);
        for (var i = 0; i < 100 && !gate.Task.IsCompleted; i++)
        {
            // LLM 已挂起在门上（等待若干轮确保归纳进入调用段）后喂新条
            if (i == 30) service.RecordListenerChat("归纳期间的新消息");
            await Task.Delay(5);
        }
        gate.SetResult();
        await run;

        Assert.NotNull(service.GetSnapshot().Impression);
        await service.FlushAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_profileFile));
        // 清到水位：16 条被消费，在途新条保留——整表 Clear 会把它一并丢掉
        var pending = doc.RootElement.GetProperty("PendingChats").EnumerateArray().ToList();
        var survived = Assert.Single(pending);
        Assert.Equal("归纳期间的新消息", survived.GetProperty("Text").GetString());
    }

    [Fact]
    public async Task Impression_FailureBackoff_ThreeStrikesThen24h()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("   "); // 空输出 → 归纳失败
        var service = await CreateLoadedServiceAsync();

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 16; i++)
                service.RecordListenerChat($"第{round}轮消息{i}");
            await service.RefreshImpressionAsync(CancellationToken.None);
        }
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));
        Assert.Null(service.GetSnapshot().Impression);

        // 第 4 轮素材充足但退避期内：不调 LLM
        for (var i = 0; i < 16; i++)
            service.RecordListenerChat($"退避期消息{i}");
        await service.RefreshImpressionAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(3));

        // 25 小时后退避解除：可重试
        _now = _now.AddHours(25);
        await service.RefreshImpressionAsync(CancellationToken.None);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Impression_LanguageSwitchTriggersRegeneration_WhenMaterialExists()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("{\"summary\":\"中文印象\"}");
        var service = await CreateLoadedServiceAsync();
        for (var i = 0; i < 16; i++)
            service.RecordListenerChat($"消息{i}");
        await service.RefreshImpressionAsync(CancellationToken.None);
        Assert.Equal("zh", service.GetSnapshot().Impression!.Language);

        try
        {
            AppLanguage.Apply("en");
            // 语言差异 + 无新素材：归纳输入要求有消息（纯翻译旧印象价值低且易幻觉），不触发
            await service.RefreshImpressionAsync(CancellationToken.None);
            _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Once);

            // 有新素材：语言差异触发重归纳
            service.RecordListenerChat("english message");
            await service.RefreshImpressionAsync(CancellationToken.None);
            _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Exactly(2));
            Assert.Equal("en", service.GetSnapshot().Impression!.Language);
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public async Task Impression_ResetClearsImpressionAndWindow()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("{\"summary\":\"印象\"}");
        var service = await CreateLoadedServiceAsync();
        for (var i = 0; i < 16; i++)
            service.RecordListenerChat($"消息{i}");
        await service.RefreshImpressionAsync(CancellationToken.None);
        Assert.NotNull(service.GetSnapshot().Impression);
        service.RecordListenerChat("归纳后又聊的");
        // Reset 前先证明窗口确实在落盘（归纳后新消息待下轮）
        await service.FlushAsync(CancellationToken.None);
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_profileFile)))
        {
            Assert.Single(doc.RootElement.GetProperty("PendingChats").EnumerateArray());
        }

        // Reset 连坐：印象清空、窗口清空（文件整体删除）
        service.Reset();
        Assert.Null(service.GetSnapshot().Impression);
        Assert.False(File.Exists(_profileFile));
    }

    [Fact]
    public async Task Impression_ForceWithoutMaterial_IsNoOp()
    {
        // force（手动重新归纳）也要求有素材：无消息的归纳是空转
        var service = await CreateLoadedServiceAsync();
        await service.RefreshImpressionAsync(CancellationToken.None, force: true);
        _llm.Verify(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()), Times.Never);
        Assert.Null(service.GetSnapshot().Impression);
    }
}
