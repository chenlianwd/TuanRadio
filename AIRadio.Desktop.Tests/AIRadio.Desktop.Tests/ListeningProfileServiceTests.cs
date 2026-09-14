using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("偏好华语流行与 City Pop，深夜偏安静。");
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

    [Fact]
    public async Task Digest_FailuresBackOffAfterThreeStrikes()
    {
        _llm.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
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
}
