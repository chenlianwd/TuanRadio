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
    // digest 的 JSON 结构化输出：tags 承载风格标签（流派/年代/语言/氛围）
    private const int DigestMaxStyleTags = 8;
    private const int DigestTagMaxLength = 10;
    // 听歌行为指标样本门槛（不足=无值，人格特质对应维度不标注）
    private const int BehaviorMinCompletionSamples = 10;
    private const int BehaviorMinPlayedSamples = 10;
    private const int BehaviorMinArtists = 5;
    private static readonly TimeSpan BehaviorWindow = TimeSpan.FromDays(30);
    // 聊天性格印象：素材窗口滚动上限/归纳触发水位/原文截断
    private const int ChatWindowMaxEntries = 20;
    private const int ImpressionTriggerMessages = 16;
    private const int ChatMessageMaxLength = 200;
    private const int ImpressionMaxLength = 200;
    private const int ImpressionMaxTraits = 6;
    private const int ImpressionTraitMaxLength = 8;
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
    private readonly Func<string, string, CancellationToken, Task> _writeAllTextAsync;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _digestGate = new(1, 1);
    private readonly SemaphoreSlim _impressionGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private List<ListeningEventData> _events = new();
    private long _totalEventsIngested;
    private TasteDigestData? _digest;
    private List<ChatWindowEntry> _pendingChats = new();
    private ListenerImpressionData? _impression;
    private bool _enabled = true;
    private bool _loaded;
    private bool _futureFormatSkipped;

    // digest 触发与退避（会话级；失败计数从持久化 digest 播种）
    private bool _digestInProgress;
    private int _digestFailureCount;
    private DateTime? _lastDigestFailureAt;

    // impression 触发与退避（会话级；失败计数从持久化 impression 播种；退避参数与 digest 共享——
    // 同类 LLM 归纳的限流策略一致即可）
    private bool _impressionInProgress;
    private int _impressionFailureCount;
    private DateTime? _lastImpressionFailureAt;

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
    private long _changeVersion;
    private int _disposed;

    /// <summary>创建本地画像服务，默认以异步文件写入保存快照。</summary>
    public ListeningProfileService(ILLMService llm, string? profileFile = null, Func<DateTime>? clock = null)
        : this(llm, profileFile, clock, File.WriteAllTextAsync)
    {
    }

    /// <summary>注入临时文件写入边界，便于验证慢磁盘与并发保存。</summary>
    internal ListeningProfileService(ILLMService llm, string? profileFile, Func<DateTime>? clock,
        Func<string, string, CancellationToken, Task> writeAllTextAsync)
    {
        _llm = llm;
        _profileFile = profileFile ?? Path.Combine(DefaultProfileDir, "listener-profile.json");
        _clock = clock ?? (() => DateTime.Now);
        _writeAllTextAsync = writeAllTextAsync;
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

    /// <summary>清空画像，并在同一把锁下作废旧快照及删除已发布文件。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _events = new List<ListeningEventData>();
            _totalEventsIngested = 0;
            _digest = null;
            _digestFailureCount = 0;
            _lastDigestFailureAt = null;
            _pendingChats = new List<ChatWindowEntry>();
            _impression = null;
            _impressionFailureCount = 0;
            _lastImpressionFailureAt = null;
            _futureFormatSkipped = false;
            _epoch++;
            _dirty = false;
            ClearPlayingStateLocked();
            _lastPlayedKey = null;
            // 保存方只在持有 _gate 且代次一致时发布临时文件，无需等待在途 I/O。
            try
            {
                File.Delete(_profileFile);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete listener profile file {Path}", _profileFile);
            }
        }
    }

    public bool RemoveDislikeEvents(string? title, string? artist)
    {
        var normalizedTitle = MusicIdentity.NormalizeMusicText(title ?? string.Empty);
        var normalizedArtist = MusicIdentity.NormalizeMusicText(artist ?? string.Empty);
        // 镜像黑名单构建条件：空歌手不入黑名单（BuildSnapshotLocked），空标题是合法键
        if (normalizedArtist.Length == 0)
            return false;

        lock (_gate)
        {
            // 未加载/未来版本跳过时事件列表为空，RemoveAll 自然返回 0，无需前置守卫；
            // 学习开关不拦截：暂停期数据保留，赦免同样成立（快照侧 Enabled=false 不出数据）
            var removed = _events.RemoveAll(e =>
                e.Type == ListeningEventType.Dislike &&
                string.Equals(MusicIdentity.NormalizeMusicText(e.Title), normalizedTitle, StringComparison.Ordinal) &&
                string.Equals(MusicIdentity.NormalizeMusicText(e.Artist), normalizedArtist, StringComparison.Ordinal));
            if (removed == 0)
                return false;
            _dirty = true;
            ScheduleSave();
            return true;
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
                _pendingChats = data.PendingChats ?? new List<ChatWindowEntry>();
                _impression = data.Impression;
                _impressionFailureCount = data.Impression?.ConsecutiveFailures ?? 0;
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
                _pendingChats = new List<ChatWindowEntry>();
                _impression = null;
                _futureFormatSkipped = false;
            }
            Log.Warning(ex, "Failed to load listener profile, starting empty");
        }
    }

    public async Task RefreshDigestAsync(CancellationToken cancellationToken, bool force = false)
    {
        try
        {
            await RefreshDigestCoreAsync(cancellationToken, force);
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

    private async Task RefreshDigestCoreAsync(CancellationToken cancellationToken, bool force)
    {
        if (!IsLlmUsable())
            return;

        DigestPromptInput input;
        lock (_gate)
        {
            if (!_enabled || !_loaded || _digestInProgress || _futureFormatSkipped)
                return;

            var now = _clock();
            // force（设置页手动重新归纳）跳过失败退避与下方触发条件判定；在途互斥不跳过
            // （撞上直接返回，由 VM 的 digest 前后对比反馈「未完成」引导稍后重试）
            if (!force && _digestFailureCount >= DigestMaxConsecutiveFailures &&
                _lastDigestFailureAt is { } lastFailure && now - lastFailure < DigestBackoff)
                return;

            var snapshot = BuildSnapshotLocked(now);
            if (!snapshot.MeetsInjectionThreshold)
                return;

            var language = AppLanguage.Current;
            // 语言差异触发归纳：成功即 _digest.Language 与当前语言一致（条件自满足），
            // 失败则与水位/老化触发同待遇——由连续失败退避统一限流，下一个事件可重试。
            // （曾有 _languageRegenDone 前置标记防重复扣费，但会让失败后的旧语言摘要
            // 整会话不再因语言差异重试，已移除）
            if (!force)
            {
                var need = _digest == null ||
                           _totalEventsIngested - _digest.EventWatermark >= DigestWatermarkEvents ||
                           now - _digest.GeneratedAt >= DigestMaxAge ||
                           !string.Equals(_digest.Language, language, StringComparison.OrdinalIgnoreCase);
                if (!need)
                    return;
            }

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
            var reply = await ChatAsync(prompt, timeoutCts.Token);
            // 宽容双路解析：JSON 优先（summary+tags），失败回退整串纯文本；空输出才算失败
            var parsed = ParseLlmSummary(reply, DigestMaxLength, DigestMaxStyleTags, DigestTagMaxLength);
            if (parsed.Summary.Length == 0)
                throw new InvalidOperationException("LLM returned empty taste digest");

            lock (_gate)
            {
                // Reset 递增过代次：过期结果不得写回已清空的画像
                if (_epoch != epoch || _disposed != 0)
                    return;
                _digest = new TasteDigestData
                {
                    Text = parsed.Summary,
                    StyleTags = parsed.Tags,
                    Language = input.Language,
                    GeneratedAt = _clock(),
                    EventWatermark = _totalEventsIngested,
                    ConsecutiveFailures = 0,
                };
                _digestFailureCount = 0;
                _lastDigestFailureAt = null;
                _dirty = true;
                ScheduleSave();
                Log.Debug("Listener taste digest refreshed at watermark {Watermark}", _digest.EventWatermark);
            }
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

    public void RecordListenerChat(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return;
        // 服务端自证入库内容干净：接口是公开面，不能假设调用方已消毒（纵深防御，
        // 与标题/话题同口径）；先消毒再 Trim（反序会留尾随空格）
        var text = PromptHygiene.StripControlMarkers(userMessage).Trim();
        if (text.Length == 0) return;
        if (text.Length > ChatMessageMaxLength)
            text = text[..ChatMessageMaxLength];

        lock (_gate)
        {
            // 学习开关关闭/未来版本拒读：聊天原文不落盘（比收听事件更敏感，停采契约严格适用）
            if (!CanIngestLocked())
                return;
            _pendingChats.Add(new ChatWindowEntry { Text = text, Time = _clock() });
            if (_pendingChats.Count > ChatWindowMaxEntries)
                _pendingChats.RemoveRange(0, _pendingChats.Count - ChatWindowMaxEntries);
            _dirty = true;
            ScheduleSave();
        }
    }

    public async Task RefreshImpressionAsync(CancellationToken cancellationToken, bool force = false)
    {
        try
        {
            await RefreshImpressionCoreAsync(cancellationToken, force);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listener profile impression refresh failed");
        }
    }

    private async Task RefreshImpressionCoreAsync(CancellationToken cancellationToken, bool force)
    {
        if (!IsLlmUsable())
            return;

        ImpressionPromptInput input;
        int inputCount;
        lock (_gate)
        {
            if (!_enabled || !_loaded || _impressionInProgress || _futureFormatSkipped)
                return;
            // 无素材的归纳是空转：force（手动重新归纳）也要求有待归纳消息，
            // 纯翻译旧印象价值低且易幻觉
            if (_pendingChats.Count == 0)
                return;

            var now = _clock();
            if (!force && _impressionFailureCount >= DigestMaxConsecutiveFailures &&
                _lastImpressionFailureAt is { } lastFailure && now - lastFailure < DigestBackoff)
                return;

            var language = AppLanguage.Current;
            if (!force)
            {
                // 触发=攒够素材水位；或已有印象但语言不符（配合最前方的素材检查：
                // 语言重归纳也要有新消息，纯翻译旧印象价值低且易幻觉）
                var need = _pendingChats.Count >= ImpressionTriggerMessages ||
                           (_impression != null &&
                            !string.Equals(_impression.Language, language, StringComparison.OrdinalIgnoreCase));
                if (!need)
                    return;
            }

            _impressionInProgress = true;
            // 归纳水位：LLM 调用（≤15s）期间新到的喂料必须保留待下轮，成功后清到这里
            inputCount = _pendingChats.Count;
            input = new ImpressionPromptInput(
                language,
                _pendingChats.Select(c => c.Text).ToList(),
                _impression?.Text,
                _impression?.Traits.ToList() ?? new List<string>());
        }

        try
        {
            await _impressionGate.WaitAsync(cancellationToken);
        }
        catch
        {
            lock (_gate)
                _impressionInProgress = false;
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

            var prompt = BuildImpressionPrompt(input);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DigestCallTimeout);
            var reply = await ChatAsync(prompt, timeoutCts.Token);
            var parsed = ParseLlmSummary(reply, ImpressionMaxLength, ImpressionMaxTraits, ImpressionTraitMaxLength);
            if (parsed.Summary.Length == 0)
                throw new InvalidOperationException("LLM returned empty listener impression");

            lock (_gate)
            {
                if (_epoch != epoch || _disposed != 0)
                    return;
                _impression = new ListenerImpressionData
                {
                    Text = parsed.Summary,
                    Traits = parsed.Tags,
                    Language = input.Language,
                    GeneratedAt = _clock(),
                    ConsecutiveFailures = 0,
                };
                _impressionFailureCount = 0;
                _lastImpressionFailureAt = null;
                // 清到归纳水位（digest 的「单调水位、数据不删」在暂存区模型下的投影）：
                // 整表 Clear 会把归纳在途新到的消息一并丢掉（既没进本次归纳也没留在素材区）
                if (_pendingChats.Count > inputCount)
                    _pendingChats.RemoveRange(0, inputCount);
                else
                    _pendingChats.Clear();
                _dirty = true;
                ScheduleSave();
                Log.Debug("Listener impression refreshed from {Count} chat messages", inputCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _impressionFailureCount++;
                _lastImpressionFailureAt = _clock();
                if (_impression != null)
                    _impression.ConsecutiveFailures = _impressionFailureCount;
            }
            Log.Warning(ex, "Listener impression generation failed (count {Count})", _impressionFailureCount);
        }
        finally
        {
            lock (_gate)
                _impressionInProgress = false;
            _impressionGate.Release();
        }
    }

    /// <summary>保存到最新变更版本；异步续体不依赖 UI，供退出时有界等待。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_dirty)
            return;
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_dirty)
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>停止采集与防抖，等待最后的画像快照落盘。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            _saveDebounce?.Dispose();
            _saveDebounce = null;
        }
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

    private bool CanIngestLocked() => _enabled && _loaded && !_futureFormatSkipped && _disposed == 0;

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

        // 听歌行为指标（与上列统计共用同一次事件遍历，GetSnapshot 是聊天/推荐热路径不加第二遍）
        var completedCount = 0;
        var skippedCount = 0;
        var playedCount = 0;
        var timeBands = new int[4]; // 0=night(22~5) 1=morning(5~11) 2=afternoon(11~17) 3=evening(17~22)
        var firstSeenArtist = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var windowArtists = new HashSet<string>(StringComparer.Ordinal);
        var windowStart = now - BehaviorWindow;

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

            // 行为指标累计（与歌手计分同一次遍历）
            if (e.Type == ListeningEventType.Completed) completedCount++;
            else if (e.Type == ListeningEventType.Skipped) skippedCount++;
            else if (e.Type == ListeningEventType.Played)
            {
                playedCount++;
                timeBands[TimeBandIndex(e.Time)]++;
            }

            if (artistKey.Length > 0)
            {
                // 全部可见事件中该歌手的最早记录（新歌手判定：最早记录也落在 30 天窗口内才算新）
                if (!firstSeenArtist.TryGetValue(artistKey, out var firstSeen) || e.Time < firstSeen)
                    firstSeenArtist[artistKey] = e.Time;
                if (e.Time >= windowStart)
                    windowArtists.Add(artistKey);
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

        var hasCompletion = completedCount + skippedCount >= BehaviorMinCompletionSamples;
        var completionRate = hasCompletion ? (double)completedCount / (completedCount + skippedCount) : 0.0;
        var hasExploration = windowArtists.Count >= BehaviorMinArtists;
        var newArtistCount = hasExploration
            ? windowArtists.Count(a => firstSeenArtist[a] >= windowStart)
            : 0;
        var newArtistRatio = hasExploration ? (double)newArtistCount / windowArtists.Count : 0.0;
        string? dominantBand = null;
        if (playedCount >= BehaviorMinPlayedSamples)
        {
            var best = 0;
            for (var i = 1; i < timeBands.Length; i++)
                if (timeBands[i] > timeBands[best])
                    best = i;
            dominantBand = best switch
            {
                0 => "night",
                1 => "morning",
                2 => "afternoon",
                _ => "evening",
            };
        }

        return new ListenerProfileSnapshot
        {
            TotalEventsIngested = _totalEventsIngested,
            TopArtists = topArtists,
            AvoidArtists = avoidArtists,
            DislikeBlacklist = blacklist,
            MoodUsage = moods,
            Digest = _digest,
            CompletionRate = completionRate,
            HasCompletionSample = hasCompletion,
            NewArtistRatio = newArtistRatio,
            HasExplorationSample = hasExploration,
            DominantTimeBand = dominantBand,
            PersonaTraits = ListeningPersona.DerivePersonaTraits(
                hasCompletion, completionRate, hasExploration, newArtistRatio, dominantBand),
            Impression = _impression,
        };
    }

    /// <summary>时段分桶：night=22~5 点，morning=5~11，afternoon=11~17，evening=17~22。</summary>
    private static int TimeBandIndex(DateTime time) => time.Hour switch
    {
        >= 22 or < 5 => 0,
        < 11 => 1,
        < 17 => 2,
        _ => 3,
    };

    /// <summary>在 _gate 内递增变更版本并安排防抖保存。</summary>
    private void ScheduleSave()
    {
        _changeVersion++;
        _dirty = true;
        if (_disposed != 0)
            return;
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

    /// <summary>保存一个版本；发布时核对清空代次，并仅清除对应版本的脏标记。</summary>
    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        ListenerProfileData data;
        long version;
        int epoch;
        lock (_gate)
        {
            if (_futureFormatSkipped || !_loaded)
            {
                _dirty = false;
                return;
            }
            version = _changeVersion;
            epoch = _epoch;
            data = new ListenerProfileData
            {
                Version = CurrentVersion,
                TotalEventsIngested = _totalEventsIngested,
                Events = _events.ToList(),
                Digest = _digest,
                PendingChats = _pendingChats.ToList(),
                Impression = _impression,
            };
        }

        var json = JsonSerializer.Serialize(data, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_profileFile)!);
        // 同目录临时文件 + 原子替换，避免应用退出/磁盘异常时留下半份 JSON（沿用 playlist/settings 先例）
        var tempPath = _profileFile + ".tmp";
        await _writeAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_epoch != epoch)
            {
                File.Delete(tempPath);
                return;
            }
            File.Move(tempPath, _profileFile, overwrite: true);
            if (_changeVersion == version)
                _dirty = false;
        }
    }

    private static string BuildDigestPrompt(DigestPromptInput input)
    {
        // 元数据消毒：歌手/曲目来自第三方音源，可能携带控制块标记，进提示词前剥除（纵深防御）
        string Join(string sep, IEnumerable<string> parts)
            => string.Join(sep, parts.Select(PromptHygiene.StripControlMarkers));

        var behaviorBlock = input.BehaviorLine == null ? string.Empty : $"\n{input.BehaviorLine}";
        // $$"""：单花括号是字面 JSON、{{…}} 才是插值（单 $ 下 JSON 的 {{}} 转义与插值定界冲突报 CS9006）
        return AppLanguage.Current == "en"
            ? $$"""
              Summarize this listener's music taste: preferred genres, languages, eras and moods.
              Output only JSON in this exact format: {"summary":"1-2 sentences (under 100 words)","tags":["up to 8 style tags, max 10 characters each"]} — no greetings, no explanations, nothing outside the JSON.{{behaviorBlock}}
              Favorite artists (by listening score): {{Join(", ", input.TopArtists)}}
              Avoided artists: {{Join(", ", input.AvoidArtists)}}
              Recently played: {{Join("; ", input.RecentTracks)}}
              Frequent moods: {{Join(", ", input.MoodUsage)}}
              """
            : $$"""
              归纳这位听众的音乐口味：偏好的流派、语言、年代和氛围。
              只输出 JSON，格式：{"summary":"1~2 句话总评（100 字内）","tags":["风格标签，最多 8 个、每个不超过 10 字"]}，不要问候、解释或 JSON 以外的任何内容。{{behaviorBlock}}
              常听歌手（按收听分排序）：{{Join("、", input.TopArtists)}}
              回避歌手：{{Join("、", input.AvoidArtists)}}
              最近在听：{{Join("；", input.RecentTracks)}}
              常见氛围：{{Join("、", input.MoodUsage)}}
              """;
    }

    private static string BuildImpressionPrompt(ImpressionPromptInput input)
    {
        var messages = string.Join("\n", input.Messages.Select(m => $"- {m}"));
        string previous;
        if (input.PreviousText == null)
        {
            previous = string.Empty;
        }
        else
        {
            // 旧标签一并给模型：增量更新时知道哪些维度已经确立过
            var traitList = input.PreviousTraits.Count > 0
                ? string.Join(input.Language == "en" ? ", " : "、", input.PreviousTraits)
                : null;
            previous = input.Language == "en"
                ? $"\nPrevious impression (update it incrementally, keep what still holds): {input.PreviousText}"
                  + (traitList == null ? string.Empty : $" (established traits: {traitList})")
                : $"\n旧印象（在此基础上增量更新，仍然成立的部分保留）：{input.PreviousText}"
                  + (traitList == null ? string.Empty : $"（已确立的特质：{traitList}）");
        }
        return input.Language == "en"
            ? $$"""
              Summarize this radio listener's personality and communication style from their recent chat messages to the DJ (tone, whether they prefer requesting songs or just chatting, topics they enjoy).
              Output only JSON in this exact format: {"summary":"2-3 sentences","traits":["up to 6 traits, max 8 characters each"]} — no greetings, no explanations, nothing outside the JSON.{{previous}}
              The listener recently said:
              {{messages}}
              """
            : $$"""
              根据这位电台听众最近发给 DJ 的聊天消息，归纳 TA 的性格与交流偏好（语气、偏好点歌还是闲聊、感兴趣的话题）。
              只输出 JSON，格式：{"summary":"2~3 句话印象","traits":["特质标签，最多 6 个、每个不超过 8 字"]}，不要问候、解释或 JSON 以外的任何内容。{{previous}}
              听众最近说：
              {{messages}}
              """;
    }

    /// <summary>
    /// 宽容解析 LLM 归纳输出（digest 与 impression 共用）：优先提取 JSON（{"summary":…,
    /// "tags"/"traits":[…]}，tags 与 traits 键名都认），失败回退整串纯文本作 summary；
    /// Summary 恒为消毒后纯文本（绝不存原始 JSON 串——Text 是注入链与测试的既有契约，
    /// 模型守了 JSON 格式但 summary 缺失/非字符串时按失败返回空串，不把 JSON 原文存进 Text）。
    /// Summary 为空串表示无效输出（调用方按归纳失败计退避）。
    /// </summary>
    internal readonly record struct LlmSummaryParse(string Summary, List<string> Tags);

    internal static LlmSummaryParse ParseLlmSummary(
        string? reply, int maxSummaryLength, int maxTags, int maxTagLength)
    {
        var text = (reply ?? string.Empty).Trim();
        if (text.Length == 0)
            return new LlmSummaryParse(string.Empty, new List<string>());

        var summary = string.Empty;
        List<string>? tags = null;
        var sawJsonObject = false;
        var start = text.IndexOf('{');
        if (start >= 0)
        {
            var end = text.LastIndexOf('}');
            if (end > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        sawJsonObject = true;
                        if (doc.RootElement.TryGetProperty("summary", out var summaryEl) &&
                            summaryEl.ValueKind == JsonValueKind.String)
                        {
                            summary = summaryEl.GetString() ?? string.Empty;
                            foreach (var tagKey in new[] { "tags", "traits" })
                            {
                                if (doc.RootElement.TryGetProperty(tagKey, out var tagsEl) &&
                                    tagsEl.ValueKind == JsonValueKind.Array)
                                {
                                    tags = tagsEl.EnumerateArray()
                                        .Where(t => t.ValueKind == JsonValueKind.String)
                                        .Select(t => (t.GetString() ?? string.Empty).Trim())
                                        .Where(s => s.Length > 0)
                                        .Take(maxTags)
                                        .Select(s => s.Length > maxTagLength ? s[..maxTagLength] : s)
                                        .ToList();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // 首末花括号切取的子串不是合法 JSON（纯文本含花括号等）：落入整串回退
                }
            }
        }

        if (summary.Length == 0)
        {
            // JSON 对象解析成立但 summary 无效（缺失/非字符串/空串）：模型试图守格式但没守住，
            // 按归纳失败计退避——回退整串会把 JSON 原文存进 Text、注入提示词与展示
            if (sawJsonObject)
                return new LlmSummaryParse(string.Empty, new List<string>());
            // 无可解析 JSON（纯文本回复）：整串回退，剥 Markdown 代码围栏防 ``` 残留
            summary = text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("```", string.Empty);
        }
        summary = PromptHygiene.StripControlMarkers(summary).Trim();
        if (summary.Length > maxSummaryLength)
            summary = summary[..maxSummaryLength];
        return new LlmSummaryParse(summary, tags ?? new List<string>());
    }

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

        // 听歌习惯行为行（有任一样本才拼，让 LLM 归纳总评时有"怎么听"的立体素材）
        string? behaviorLine = null;
        var isEn = language == "en";
        var parts = new List<string>();
        if (snapshot.HasCompletionSample)
            parts.Add(isEn
                ? $"finishes {(int)Math.Round(snapshot.CompletionRate * 100)}% of played tracks"
                : $"完整听完 {Math.Round(snapshot.CompletionRate * 100):0}% 的歌");
        if (snapshot.HasExplorationSample)
            parts.Add(isEn
                ? $"{(int)Math.Round(snapshot.NewArtistRatio * 100)}% of recent artists are new to them"
                : $"最近听的歌手里 {Math.Round(snapshot.NewArtistRatio * 100):0}% 是新面孔");
        if (snapshot.DominantTimeBand is { } band)
            parts.Add(isEn ? $"mostly listens in the {band}" : $"主要在{BandTextZh(band)}");
        if (parts.Count > 0)
            behaviorLine = (isEn ? "Listening behavior: " : "听歌习惯：") + string.Join(isEn ? "; " : "、", parts) + ".";

        return new DigestPromptInput(
            language,
            snapshot.TopArtists.Select(a => a.Artist).ToList(),
            snapshot.AvoidArtists.Select(a => a.Artist).ToList(),
            recentTracks,
            snapshot.MoodUsage.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList(),
            behaviorLine);
    }

    private static string BandTextZh(string band) => band switch
    {
        "night" => "深夜到凌晨听",
        "morning" => "清晨到上午听",
        "afternoon" => "下午听",
        _ => "晚间听",
    };

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
        List<string> MoodUsage,
        string? BehaviorLine = null);

    private sealed record ImpressionPromptInput(
        string Language,
        List<string> Messages,
        string? PreviousText,
        List<string> PreviousTraits);

    /// <summary>性格印象的归纳素材条目：归纳成功消费后清到水位，原文不长期保留。</summary>
    public class ChatWindowEntry
    {
        public string Text { get; set; } = string.Empty;
        public DateTime Time { get; set; }
    }

    private class ListenerProfileData
    {
        public int Version { get; set; } = CurrentVersion;
        public long TotalEventsIngested { get; set; }
        public List<ListeningEventData> Events { get; set; } = new();
        public TasteDigestData? Digest { get; set; }
        public List<ChatWindowEntry> PendingChats { get; set; } = new();
        public ListenerImpressionData? Impression { get; set; }
    }
}
