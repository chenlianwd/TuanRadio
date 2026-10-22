using System;
using System.Reactive.Disposables;
using AIRadio.Desktop.Models;
using Avalonia.Controls;
using Serilog;
using Windows.Media;
using Windows.Storage.Streams;

namespace AIRadio.Desktop.Services;

/// <summary>
/// Windows 系统媒体传输控件（SMTC）集成：把播放状态/曲目元数据/封面/进度同步到
/// 音量浮层、锁屏与任务栏媒体控件，并接收系统的播放/暂停/上一首/下一首按钮事件——
/// 耳机与键盘媒体键由系统按上报的 PlaybackStatus 翻译成 Play/Pause 事件路由过来
/// （枚举无 PlayPauseToggle 成员，enable Play+Pause 即具备单键翻译能力）。
/// 挂载失败（headless 测试/异常环境）一律静默降级为系统面板不可用，不阻断启动
/// （同 KillOnCloseJob 降级语义）；AudioService 的播放方法全同步且任意线程安全，
/// 无需 UI 调度；SMTC 为 WinRT agile 对象，属性写点仍统一兜异常防个别 setter
/// 击穿 LibVLC 原生回调线程。
/// </summary>
internal sealed class SmtcService : IDisposable
{
    /// <summary>进度偏差超过该秒数视为发生 Seek，立即推送 timeline（系统浮层不保证内插，不能只靠周期推送）。</summary>
    internal const double SeekDriftSeconds = 1.5;

    /// <summary>周期兜底推送间隔：官方建议约每 5 秒，PositionChanged 事件流约 500ms 一发。</summary>
    internal const int PeriodicPushEventCount = 10;

    private readonly IAudioService _audio;
    private readonly CompositeDisposable _subscriptions = new();
    private SystemMediaTransportControls? _smtc;
    private TimeSpan _trackDuration;
    private TimeSpan _lastPushedPosition;
    private DateTimeOffset _lastPushedAt = DateTimeOffset.Now;
    private int _eventsSincePush;
    private bool _disposed;

    public SmtcService(IAudioService audio) => _audio = audio;

    /// <summary>窗口 Show 之后（HWND 已创建）在 UI 线程调用；失败仅记 Debug 日志。</summary>
    public bool TryAttach(Window window)
    {
        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return false;

            var smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;

            _subscriptions.Add(_audio.StateChanged.Subscribe(OnStateChanged));
            _subscriptions.Add(_audio.TrackChanged.Subscribe(OnTrackChanged));
            _subscriptions.Add(_audio.PositionChanged.Subscribe(OnPositionChanged));
            // 组合内兜底解订（Dispose 显式先行解订，见 Dispose；CompositeDisposable 为正序释放，
            // 不能靠添加顺序保证按钮入口先切断）
            _subscriptions.Add(Disposable.Create(() => smtc.ButtonPressed -= OnButtonPressed));
            smtc.ButtonPressed += OnButtonPressed;

            // 挂载时可能已在播放（开台即播），主动同步一次现状；后续靠事件流
            TryWrite(() => smtc.PlaybackStatus = _audio.IsPlaying
                ? MediaPlaybackStatus.Playing
                : MediaPlaybackStatus.Stopped, "initial status");
            // 全部就绪后才发布实例：失败路径不发布（半途加入的订阅惰性早退无害），也不破坏先前已挂载的实例
            _smtc = smtc;
            OnTrackChanged(_audio.CurrentTrack);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SMTC attach failed; system media controls stay disabled");
            return false;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            HandleButton(args.Button);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SMTC button dispatch failed");
        }
    }

    /// <summary>internal 供测试直驱；Play/Pause 幂等（AudioService.Play 在 Playing 态早退），系统翻译与本地状态瞬时不一致也无害。</summary>
    internal void HandleButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play:
                _audio.Play();
                break;
            case SystemMediaTransportControlsButton.Pause:
                _audio.Pause();
                break;
            case SystemMediaTransportControlsButton.Next:
                _audio.Next();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _audio.Previous();
                break;
        }
    }

    /// <summary>Ended 归并 Stopped（自然播完待续播的瞬间面板收起，续播后恢复）。</summary>
    internal static MediaPlaybackStatus MapPlaybackStatus(PlaybackState state) => state switch
    {
        PlaybackState.Playing => MediaPlaybackStatus.Playing,
        PlaybackState.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Stopped,
    };

    private void OnStateChanged(PlaybackState state)
    {
        var smtc = _smtc;
        if (smtc == null)
            return;
        TryWrite(() => smtc.PlaybackStatus = MapPlaybackStatus(state), "playback status");
    }

    private void OnTrackChanged(Track? track)
    {
        _trackDuration = track?.Duration ?? TimeSpan.Zero;
        ResetTimelineBaseline();

        var smtc = _smtc;
        if (smtc == null)
            return;
        TryWrite(() =>
        {
            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            var music = updater.MusicProperties;
            music.Title = track?.Title ?? string.Empty;
            music.Artist = track?.Artist ?? string.Empty;
            music.AlbumTitle = track?.Album ?? string.Empty;
            // 无封面显式置 null，防上一首的封面残留系统面板
            updater.Thumbnail = track?.CoverArt is { Length: > 0 } cover ? BuildThumbnail(cover) : null;
            updater.Update();
            // 无条件清零推一次：EndTime=0 系统不渲染进度条，恰好清掉上一首的进度残留
            // （在线曲时长未知时不能带着上曲的 EndTime/Position 整曲不动）
            PushTimeline(TimeSpan.Zero);
        }, "metadata");
    }

    /// <summary>internal 供测试直测（封面写出错会静默降级为无封面，须锚定字节往返正确）。</summary>
    internal static RandomAccessStreamReference? BuildThumbnail(byte[] cover)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(cover);
                // StoreAsync 把 writer 缓冲提交进流；FlushAsync 只冲底层流不提交缓冲，
                // 缺 StoreAsync 会得到空流（封面静默丢失）
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream(); // 解除关联再释放，防 dispose 连带关闭底层流
            }
            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SMTC thumbnail build failed");
            return null;
        }
    }

    /// <summary>
    /// PositionChanged 仅 Playing 且位置变化时发射（暂停期无事件，恢复后首个事件
    /// 因基准老化会多推一次正确的 Position，顺带重置基线，无害）。
    /// </summary>
    private void OnPositionChanged(TimeSpan position)
    {
        if (_smtc == null || _trackDuration <= TimeSpan.Zero)
            return;

        var expected = _lastPushedPosition + (DateTimeOffset.Now - _lastPushedAt);
        if (!ShouldPushTimeline(position, expected, _eventsSincePush))
        {
            _eventsSincePush++;
            return;
        }
        if (PushTimeline(position))
        {
            _lastPushedPosition = position;
            _lastPushedAt = DateTimeOffset.Now;
            _eventsSincePush = 0;
        }
    }

    /// <summary>internal 供测试直驱：Seek 偏差立即推 + 事件计数周期兜底（系统浮层不保证内插）。</summary>
    internal static bool ShouldPushTimeline(TimeSpan position, TimeSpan expectedPosition, int eventsSinceLastPush)
        => Math.Abs((position - expectedPosition).TotalSeconds) > SeekDriftSeconds
           || eventsSinceLastPush >= PeriodicPushEventCount;

    private void ResetTimelineBaseline()
    {
        _lastPushedPosition = TimeSpan.Zero;
        _lastPushedAt = DateTimeOffset.Now;
        _eventsSincePush = 0;
    }

    /// <summary>不设 MinSeekTime/MaxSeekTime、不订阅拖动事件：浮层进度条只展示，Start/End/Position 三值齐才显示。</summary>
    private bool PushTimeline(TimeSpan position)
    {
        var smtc = _smtc;
        if (smtc == null)
            return false;
        try
        {
            smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = _trackDuration,
                Position = position,
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SMTC timeline update failed");
            return false;
        }
    }

    private static void TryWrite(Action write, string what)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SMTC {What} update failed", what);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var smtc = _smtc;
        _smtc = null;
        // 显式先切断系统按钮入口再解 Rx 订阅（等值委托重复解订 no-throw，与组合内兜底共存安全）
        if (smtc != null)
            TryWrite(() => smtc.ButtonPressed -= OnButtonPressed, "detach buttons");
        _subscriptions.Dispose();

        if (smtc == null)
            return;
        TryWrite(() => smtc.PlaybackStatus = MediaPlaybackStatus.Closed, "close");
        // SystemMediaTransportControls 不实现 IClosable：状态置 Closed 即关闭语义，
        // 投影对象的 COM 引用由终结器释放
    }
}
