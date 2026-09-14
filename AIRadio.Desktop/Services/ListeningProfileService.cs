using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 长期收听画像实现（设计文档：docs/plans/2026-09-14-long-term-listener-profile-design.md）。
/// 事件为唯一持久化事实，统计（歌手亲和度/黑名单/氛围）在快照时由事件现算，衰减相对当前时刻生效。
/// </summary>
public sealed class ListeningProfileService : IListeningProfileService, IDisposable
{
    public const int MaxEvents = 1000;
    public const int InjectionMinEvents = 30;
    public const int InjectionMinArtists = 3;
    private const double HalfLifeDays = 30.0;
    private const int BlacklistExpiryDays = 180;
    private const int BlacklistMaxEntries = 100;
    private const int MoodWindowDays = 90;
    private const double SkipRatioThreshold = 0.3;
    private const int SkipMinPlayedSeconds = 10;
    private const int SkipMinTrackSeconds = 60;
    private static readonly TimeSpan PlayedDedupWindow = TimeSpan.FromMinutes(10);
    private const long DigestWatermarkEvents = 25;
    private static readonly TimeSpan DigestMaxAge = TimeSpan.FromDays(7);
    private const int DigestMaxConsecutiveFailures = 3;
    private static readonly TimeSpan DigestBackoff = TimeSpan.FromHours(24);
    private static readonly TimeSpan DigestCallTimeout = TimeSpan.FromSeconds(15);
    private const int DigestMaxLength = 400;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private static readonly string DefaultProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILLMService _llm;
    private readonly string _profileFile;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _digestGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private List<ListeningEventData> _events = new();
    private long _totalEventsIngested;
    private TasteDigestData? _digest;
    private bool _enabled = true;
    private bool _loaded;
    private bool _futureFormatSkipped;

    // digest 触发与退避（会话级；失败计数从持久化 digest 播种）
    private bool _digestInProgress;
    private int _digestFailureCount;
    private DateTime? _lastDigestFailureAt;
    private bool _languageRegenDone;

    // 跳过判定瞬时状态（全部在 _gate 内）；"已播 ≥10s"用进度样本衡量（暂停不计时）
    private string? _playingKey;
    private string? _playingTitle;
    private string? _playingArtist;
    private TimeSpan _playingDuration;
    private string? _sampleKey;
    private TimeSpan _samplePosition;

    // Played 去重窗口
    private string? _lastPlayedKey;
    private DateTime _lastPlayedAt;

    private Timer? _saveDebounce;
    private volatile bool _dirty;
    private int _epoch;
    private int _disposed;

    public ListeningProfileService(ILLMService llm, string? profileFile = null, Func<DateTime>? clock = null)
    {
        _llm = llm;
        _profileFile = profileFile ?? Path.Combine(DefaultProfileDir, "listener-profile.json");
        _clock = clock ?? (() => DateTime.Now);
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) _enabled = value; }
    }

    public ListenerProfileSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            if (!_enabled)
                return ListenerProfileSnapshot.Empty;
            return BuildSnapshotLocked(_clock());
        }
    }

    public void RecordEvent(ListeningEventData eventData)
    {
        if (eventData == null) return;
        lock (_gate)
        {
            if (!CanIngestLocked())
                return;
            IngestLocked(new ListeningEventData
            {
                Type = eventData.Type,
                Title = eventData.Title ?? string.Empty,
                Artist = eventData.Artist ?? string.Empty,
                SourceId = eventData.SourceId,
                Detail = eventData.Detail,
                Time = eventData.Time == default ? _clock() : eventData.Time,
            });
        }
    }

    public void NotifyPlaybackStarted(Track track)
    {
        if (track == null) return;
        var key = BuildIdentityKey(track);
        if (key == null) return;
        lock (_gate)
        {
            if (!CanIngestLocked())
                return;

            var now = _clock();
            // 暂停恢复/短暂重播会再次进入 Playing 态：同曲 10 分钟窗口内不重复记曝光
            if (key != _lastPlayedKey || now - _lastPlayedAt >= PlayedDedupWindow)
            {
                IngestLocked(new ListeningEventData
                {
                    Type = ListeningEventType.Played,
                    Title = track.Title,
                    Artist = track.Artist,
                    SourceId = track.SourceId,
                    Time = now,
                });
                _lastPlayedKey = key;
                _lastPlayedAt = now;
            }

            _playingKey = key;
            _playingTitle = track.Title;
            _playingArtist = track.Artist;
            _playingDuration = track.Duration;
        }
    }

    public void NotifyPlaybackEndedNaturally(Track track)
    {
        if (track == null) return;
        lock (_gate)
        {
            if (!CanIngestLocked())
                return;

            IngestLocked(new ListeningEventData
            {
                Type = ListeningEventType.Completed,
                Title = track.Title,
                Artist = track.Artist,
                SourceId = track.SourceId,
                Time = _clock(),
            });

            // TrackEnded 携带的曲目与待判前曲一致时才算"播完的是它"；无论如何换曲判定状态已无意义
            if (_playingKey == BuildIdentityKey(track))
                ClearPlayingStateLocked();
        }
    }

    public void NotifyPositionSampled(Track track, TimeSpan position)
    {
        if (track == null) return;
        var key = BuildIdentityKey(track);
        if (key == null) return;
        lock (_gate)
        {
            // 样本与曲目身份绑定：换曲后残留的旧样本不得记到新曲头上
            if (_sampleKey != key)
            {
                _sampleKey = key;
                _samplePosition = TimeSpan.Zero;
            }
            if (position > _samplePosition)
                _samplePosition = position;
        }
    }

    public void NotifyTrackSwitched(Track? next)
    {
        lock (_gate)
        {
            if (_playingKey == null)
            {
                // 无待判前曲（未播/已暂停/已自然结束）：新曲样本从零开始
                _sampleKey = null;
                _samplePosition = TimeSpan.Zero;
                return;
            }

            var nextKey = BuildIdentityKey(next);
            if (nextKey == _playingKey)
            {
                // 同曲重放（URL 失效恢复/重试）：保持判定状态等真正的换曲
                return;
            }

            if (CanIngestLocked() &&
                _sampleKey == _playingKey &&
                _playingDuration >= TimeSpan.FromSeconds(SkipMinTrackSeconds) &&
                _samplePosition >= TimeSpan.FromSeconds(SkipMinPlayedSeconds) &&
                _samplePosition < TimeSpan.FromMilliseconds(_playingDuration.TotalMilliseconds * SkipRatioThreshold))
            {
                IngestLocked(new ListeningEventData
                {
                    Type = ListeningEventType.Skipped,
                    Title = _playingTitle ?? string.Empty,
                    Artist = _playingArtist ?? string.Empty,
                    Time = _clock(),
                });
            }

            ClearPlayingStateLocked();
        }
    }

    public void NotifyPlaybackPaused()
    {
        lock (_gate)
            ClearPlayingStateLocked();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _events = new List<ListeningEventData>();
            _totalEventsIngested = 0;
            _digest = null;
            _digestFailureCount = 0;
            _lastDigestFailureAt = null;
            _languageRegenDone = false;
            _futureFormatSkipped = false;
            _epoch++;
            _dirty = false;
            ClearPlayingStateLocked();
            _lastPlayedKey = null;
        }

        // 先等在途防抖写盘结束再删文件：写盘方可能已过 dirty 检查且快照的是
        // 清除前的旧状态，与删除并发会把旧画像复活到磁盘。
        var gateHeld = false;
        try
        {
            gateHeld = _saveGate.Wait(TimeSpan.FromSeconds(2));
            if (!gateHeld)
                Log.Warning("Listener profile save still in flight during reset; stale write may recreate the file");
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (File.Exists(_profileFile))
                File.Delete(_profileFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete listener profile file {Path}", _profileFile);
        }
        finally
        {
            if (gateHeld)
                _saveGate.Release();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ListenerProfileData? data = null;
            if (File.Exists(_profileFile))
            {
                var json = await File.ReadAllTextAsync(_profileFile, cancellationToken);
                data = JsonSerializer.Deserialize<ListenerProfileData>(json, JsonOptions);
            }

            lock (_gate)
            {
                _loaded = true;
                if (data == null)
                    return;

                if (data.Version > CurrentVersion)
                {
                    // 未来版本：空画像运行且本会话拒写，防降级运行销毁新版数据（对齐 playlist 先例）
                    _futureFormatSkipped = true;
                    Log.Warning("Listener profile uses newer format {Version}, running with empty profile", data.Version);
                    return;
                }

                _events = data.Events ?? new List<ListeningEventData>();
                _totalEventsIngested = Math.Max(data.TotalEventsIngested, _events.Count);
                _digest = data.Digest;
                _digestFailureCount = data.Digest?.ConsecutiveFailures ?? 0;
                _dirty = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 损坏按空画像运行并允许覆写：画像数据可再积累，价值低于歌单
            lock (_gate)
            {
                _loaded = true;
                _events = new List<ListeningEventData>();
                _totalEventsIngested = 0;
                _digest = null;
                _futureFormatSkipped = false;
            }
            Log.Warning(ex, "Failed to load listener profile, starting empty");
        }
    }

    public async Task RefreshDigestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshDigestCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 应用关闭：归纳中途取消属于正常路径
        }
        catch (Exception ex)
        {
            // fire-and-forget 调用方不观察异常：全量内吞，绝不抛出
            Log.Warning(ex, "Listener profile digest refresh failed");
        }
    }

    private async Task RefreshDigestCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsLlmUsable())
            return;

        DigestPromptInput input;
        lock (_gate)
        {
            if (!_enabled || !_loaded || _digestInProgress || _futureFormatSkipped)
                return;

            var now = _clock();
            if (_digestFailureCount >= DigestMaxConsecutiveFailures &&
                _lastDigestFailureAt is { } lastFailure && now - lastFailure < DigestBackoff)
                return;

            var snapshot = BuildSnapshotLocked(now);
            if (!snapshot.MeetsInjectionThreshold)
                return;

            var language = AppLanguage.Current;
            var need = _digest == null ||
                       _totalEventsIngested - _digest.EventWatermark >= DigestWatermarkEvents ||
                       now - _digest.GeneratedAt >= DigestMaxAge ||
                       (!string.Equals(_digest.Language, language, StringComparison.OrdinalIgnoreCase) && !_languageRegenDone);
            if (!need)
                return;

            if (_digest != null && !string.Equals(_digest.Language, language, StringComparison.OrdinalIgnoreCase))
                _languageRegenDone = true;

            _digestInProgress = true;
            input = BuildPromptInputLocked(snapshot, language);
        }

        try
        {
            await _digestGate.WaitAsync(cancellationToken);
        }
        catch
        {
            // 等门阶段取消（应用关闭）：_digestInProgress 不复位会让归纳永久停摆
            lock (_gate)
                _digestInProgress = false;
            throw;
        }

        try
        {
            int epoch;
            lock (_gate)
            {
                if (!_enabled || _disposed != 0)
                    return;
                epoch = _epoch;
            }

            var prompt = BuildDigestPrompt(input);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DigestCallTimeout);
            var text = await ChatAsync(prompt, timeoutCts.Token);
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
                throw new InvalidOperationException("LLM returned empty taste digest");
            if (text.Length > DigestMaxLength)
                text = text[..DigestMaxLength];

            lock (_gate)
            {
                // Reset 递增过代次：过期结果不得写回已清空的画像
                if (_epoch != epoch)
                    return;
                _digest = new TasteDigestData
                {
                    Text = text,
                    Language = input.Language,
                    GeneratedAt = _clock(),
                    EventWatermark = _totalEventsIngested,
                    ConsecutiveFailures = 0,
                };
                _digestFailureCount = 0;
                _lastDigestFailureAt = null;
                _dirty = true;
                Log.Debug("Listener taste digest refreshed at watermark {Watermark}", _digest.EventWatermark);
            }
            ScheduleSave();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _digestFailureCount++;
                _lastDigestFailureAt = _clock();
                if (_digest != null)
                    _digest.ConsecutiveFailures = _digestFailureCount;
            }
            Log.Warning(ex, "Taste digest generation failed (count {Count})", _digestFailureCount);
        }
        finally
        {
            lock (_gate)
                _digestInProgress = false;
            _digestGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_dirty)
            return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            if (!_dirty)
                return;
            await SaveCoreAsync(cancellationToken);
            _dirty = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to flush listener profile");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _saveDebounce?.Dispose();
        _saveDebounce = null;
        try
        {
            // 退出兜底落盘：小文件（≤1000 事件）写入毫秒级，有界等待防设备异常拖死关闭
            FlushAsync(CancellationToken.None).Wait(FlushTimeout);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listener profile final flush failed");
        }
    }

    public const int CurrentVersion = 1;

    private bool CanIngestLocked() => _enabled && _loaded && !_futureFormatSkipped;

    private void IngestLocked(ListeningEventData eventData)
    {
        _events.Add(eventData);
        _totalEventsIngested++;
        if (_events.Count > MaxEvents)
            _events.RemoveRange(0, _events.Count - MaxEvents);
        _dirty = true;
        ScheduleSave();
    }

    private void ClearPlayingStateLocked()
    {
        _playingKey = null;
        _playingTitle = null;
        _playingArtist = null;
        _playingDuration = TimeSpan.Zero;
        _sampleKey = null;
        _samplePosition = TimeSpan.Zero;
    }

    private static string? BuildIdentityKey(Track? track)
    {
        if (track == null) return null;
        var title = MusicIdentity.NormalizeMusicText(track.Title);
        if (title.Length == 0) return null;
        return title + "|" + MusicIdentity.NormalizeMusicText(track.Artist);
    }

    private ListenerProfileSnapshot BuildSnapshotLocked(DateTime now)
    {
        if (!_loaded || _futureFormatSkipped)
            return ListenerProfileSnapshot.Empty;

        var artists = new Dictionary<string, ArtistAccumulator>(StringComparer.Ordinal);
        var dislikes = new Dictionary<string, DislikeAccumulator>(StringComparer.Ordinal);
        var moods = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var e in _events)
        {
            var ageDays = Math.Max(0.0, (now - e.Time).TotalDays);
            var decay = Math.Pow(0.5, ageDays / HalfLifeDays);

            var weight = e.Type switch
            {
                ListeningEventType.Played => 0.2,
                ListeningEventType.Completed => 1.0,
                ListeningEventType.Skipped => -0.8,
                ListeningEventType.Like => 2.0,
                ListeningEventType.Dislike => -3.0,
                ListeningEventType.Similar => 1.0,
                _ => 0.0,
            };

            var artistKey = MusicIdentity.NormalizeMusicText(e.Artist);
            if (weight != 0.0 && artistKey.Length > 0)
            {
                if (!artists.TryGetValue(artistKey, out var entry))
                    artists[artistKey] = entry = new ArtistAccumulator { DisplayName = e.Artist };
                entry.Score = Math.Max(-5.0, entry.Score + weight * decay);
                if (e.Type is ListeningEventType.Played or ListeningEventType.Completed)
                {
                    entry.PlayCount++;
                    entry.LastPlayedAt = e.Time;
                }
            }

            if (e.Type == ListeningEventType.Dislike && artistKey.Length > 0)
            {
                // 空歌手条目不入黑名单：IsSameMusicIdentity 的空歌手通配会仅凭标题
                // 排除所有同名曲（翻唱/伴奏），误伤面过大
                var key = MusicIdentity.NormalizeMusicText(e.Title) + "|" + artistKey;
                // 同一曲多次 Dislike 取最新时间（过期判定以最近一次为准）
                if (!dislikes.TryGetValue(key, out var prev) || e.Time > prev.Time)
                    dislikes[key] = new DislikeAccumulator { Time = e.Time, Title = e.Title, Artist = e.Artist };
            }

            if (ageDays <= MoodWindowDays)
            {
                var mood = e.Type switch
                {
                    ListeningEventType.Calmer => "calm",
                    ListeningEventType.Energetic => "energetic",
                    ListeningEventType.MoodSet when !string.IsNullOrWhiteSpace(e.Detail) => e.Detail,
                    _ => null,
                };
                if (mood != null)
                    moods[mood] = moods.TryGetValue(mood, out var count) ? count + 1 : 1;
            }
        }

        var blacklistExpiry = TimeSpan.FromDays(BlacklistExpiryDays);
        var blacklist = dislikes.Values
            .Where(x => now - x.Time <= blacklistExpiry)
            .OrderByDescending(x => x.Time)
            .Take(BlacklistMaxEntries)
            .Select(x => new ProfileMusicRef(x.Title, x.Artist))
            .ToList();

        var topArtists = artists
            .Select(kv => new ArtistAffinity
            {
                Artist = kv.Value.DisplayName,
                Score = kv.Value.Score,
                PlayCount = kv.Value.PlayCount,
                LastPlayedAt = kv.Value.LastPlayedAt,
            })
            .Where(a => a.Score > 0)
            .OrderByDescending(a => a.Score)
            .Take(8)
            .ToList();

        var avoidArtists = artists
            .Select(kv => new ArtistAffinity { Artist = kv.Value.DisplayName, Score = kv.Value.Score })
            .Where(a => a.Score < -1.0)
            .OrderBy(a => a.Score)
            .Take(3)
            .ToList();

        return new ListenerProfileSnapshot
        {
            TotalEventsIngested = _totalEventsIngested,
            TopArtists = topArtists,
            AvoidArtists = avoidArtists,
            DislikeBlacklist = blacklist,
            MoodUsage = moods,
            Digest = _digest,
        };
    }

    private void ScheduleSave()
    {
        _dirty = true;
        var debounce = _saveDebounce;
        if (debounce == null)
        {
            debounce = new Timer(_ => _ = FlushIfDirtyAsync(), null, SaveDebounce, Timeout.InfiniteTimeSpan);
            var winner = Interlocked.CompareExchange(ref _saveDebounce, debounce, null);
            if (winner != null)
            {
                debounce.Dispose();
                debounce = winner;
            }
        }
        try
        {
            debounce.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 并发：退出兜底落盘由 Dispose 内的 FlushAsync 负责，放弃本次防抖即可
        }
    }

    private async Task FlushIfDirtyAsync()
    {
        if (!_dirty)
            return;
        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listener profile debounced save failed");
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        ListenerProfileData data;
        lock (_gate)
        {
            if (_futureFormatSkipped || !_loaded)
                return;
            data = new ListenerProfileData
            {
                Version = CurrentVersion,
                TotalEventsIngested = _totalEventsIngested,
                Events = _events.ToList(),
                Digest = _digest,
            };
        }

        var json = JsonSerializer.Serialize(data, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_profileFile)!);
        // 同目录临时文件 + 原子替换，避免应用退出/磁盘异常时留下半份 JSON（沿用 playlist/settings 先例）
        var tempPath = _profileFile + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _profileFile, overwrite: true);
    }

    private static string BuildDigestPrompt(DigestPromptInput input) => AppLanguage.Current == "en"
        ? $"""
            Summarize this listener's music taste in 3-5 short sentences: preferred genres, languages, eras and moods.
            Output only the summary — no greetings or explanations.
            Favorite artists (by listening score): {string.Join(", ", input.TopArtists)}
            Avoided artists: {string.Join(", ", input.AvoidArtists)}
            Recently played: {string.Join("; ", input.RecentTracks)}
            Frequent moods: {string.Join(", ", input.MoodUsage)}
            """
        : $"""
            用 3-5 句话归纳这位听众的音乐口味：偏好的流派、语言、年代和氛围。
            只输出归纳结果本身：不要问候、开场白或任何解释。
            常听歌手（按收听分排序）：{string.Join("、", input.TopArtists)}
            回避歌手：{string.Join("、", input.AvoidArtists)}
            最近在听：{string.Join("；", input.RecentTracks)}
            常见氛围：{string.Join("、", input.MoodUsage)}
            """;

    private DigestPromptInput BuildPromptInputLocked(ListenerProfileSnapshot snapshot, string language)
    {
        var recentTracks = _events
            .Where(e => e.Type is ListeningEventType.Played or ListeningEventType.Completed)
            .Select(e => $"{e.Title} - {e.Artist}")
            .Reverse()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Reverse()
            .ToList();

        return new DigestPromptInput(
            language,
            snapshot.TopArtists.Select(a => a.Artist).ToList(),
            snapshot.AvoidArtists.Select(a => a.Artist).ToList(),
            recentTracks,
            snapshot.MoodUsage.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList());
    }

    private async Task<string> ChatAsync(string prompt, CancellationToken cancellationToken)
        // 与 RecommendationService.ChatAsync 同构：归纳必须走无人设调用，
        // DJ 人设会让模型回整段台词污染摘要
        => _llm is LLMService llm
            ? await llm.ChatRawAsync(prompt, cancellationToken)
            : await _llm.ChatAsync(prompt, new List<ChatMessage>()).WaitAsync(cancellationToken);

    private bool IsLlmUsable()
        // 未配置时 ChatAsync 返回"请先配置"提示文案，不能当摘要
        => !(_llm is LLMService llmService && !llmService.IsConfigured());

    private sealed class ArtistAccumulator
    {
        public double Score;
        public int PlayCount;
        public DateTime LastPlayedAt;
        public string DisplayName = string.Empty;
    }

    private sealed class DislikeAccumulator
    {
        public DateTime Time;
        public string Title = string.Empty;
        public string Artist = string.Empty;
    }

    private sealed record DigestPromptInput(
        string Language,
        List<string> TopArtists,
        List<string> AvoidArtists,
        List<string> RecentTracks,
        List<string> MoodUsage);

    private class ListenerProfileData
    {
        public int Version { get; set; } = CurrentVersion;
        public long TotalEventsIngested { get; set; }
        public List<ListeningEventData> Events { get; set; } = new();
        public TasteDigestData? Digest { get; set; }
    }
}
