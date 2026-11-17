using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.WebRemote;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private IWebRemoteService? _webRemoteService;
    private CompositeDisposable? _webRemoteSubs;
    private bool _webRemoteInitialized;
    private bool _syncingWebRemoteSetting;
    private volatile WebRemoteSnapshot _latestWebRemoteSnapshot = new();
    private CancellationTokenSource? _webRemoteCopyHintCts;

    [Reactive] public partial bool IsWebRemoteRunning { get; private set; }
    [Reactive] public partial int WebRemoteClientCount { get; private set; }
    [Reactive] public partial string WebRemoteUrl { get; private set; } = string.Empty;
    [Reactive] public partial string WebRemoteStatusText { get; private set; } = AppLanguage.Get("S_WebRemoteStopped");
    [Reactive] public partial string WebRemoteTooltip { get; private set; } = AppLanguage.Get("S_WebRemoteTitle");
    [Reactive] public partial string WebRemoteToggleButtonText { get; private set; } = AppLanguage.Get("S_WebRemoteStartNow");
    [Reactive] public partial IImage? WebRemoteQrImage { get; private set; }
    [Reactive] public partial string WebRemoteCopyButtonText { get; private set; } = AppLanguage.Get("S_WebRemoteCopyUrl");

    public ReactiveCommand<Unit, Unit>? ToggleWebRemoteCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? RefreshWebRemoteTokenCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? CopyWebRemoteUrlCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? OpenWebRemoteSettingsCommand { get; private set; }

    private void InitializeWebRemote(IWebRemoteService? webRemoteService)
    {
        _webRemoteService = webRemoteService;

        ToggleWebRemoteCommand = ReactiveCommand.CreateFromTask(ToggleWebRemoteAsync);
        RefreshWebRemoteTokenCommand = ReactiveCommand.CreateFromTask(RefreshWebRemoteTokenAsync);
        CopyWebRemoteUrlCommand = ReactiveCommand.CreateFromTask(CopyWebRemoteUrlAsync);
        OpenWebRemoteSettingsCommand = ReactiveCommand.Create(() =>
        {
            CloseTransientOverlays();
            IsSettingsOpen = true;
            SettingsVM.TabIndex = 3;
        });

        var subs = new CompositeDisposable();
        _webRemoteSubs = subs;

        if (_webRemoteService != null)
        {
            _webRemoteService.ConfigureHandlers(
                () => _latestWebRemoteSnapshot,
                DispatchWebRemoteCommandAsync);

            Action onStatusChanged = () =>
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    if (!IsDisposed) RefreshWebRemoteUiState();
                });
            _webRemoteService.StatusChanged += onStatusChanged;
            subs.Add(Disposable.Create(() =>
            {
                if (_webRemoteService != null)
                    _webRemoteService.StatusChanged -= onStatusChanged;
            }));
        }

        // 高频进度变化走节流推播（100ms 合并），曲目/状态/歌词/聊天走即时推播
        subs.Add(PlayerVM.WhenAnyValue(x => x.CurrentSeconds)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: false)));

        subs.Add(PlayerVM.WhenAnyValue(
                x => x.TrackTitle,
                x => x.TrackArtist,
                x => x.TrackSource,
                x => x.TrackQualityText,
                x => x.TrackIsPreview,
                x => x.IsPlaying)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(PlayerVM.WhenAnyValue(
                x => x.IsLiveStream,
                x => x.TotalSeconds,
                x => x.Volume,
                x => x.IsShuffled,
                x => x.RepeatMode)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(this.WhenAnyValue(
                x => x.IsCurrentFavorite,
                x => x.CurrentState,
                x => x.CurrentSceneMode,
                x => x.IsSleepTimerActive)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(this.WhenAnyValue(
                x => x.SleepTimerDisplayStatus,
                x => x.SleepTimerTargetMinutes,
                x => x.CanStopAfterCurrentTrack,
                x => x.SelectedCharacter)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        subs.Add(ChatVM.WhenAnyValue(x => x.IsSpeaking, x => x.IsProcessing)
            .Subscribe(_ => UpdateWebRemoteSnapshot(immediate: true)));

        NotifyCollectionChangedEventHandler onChatChanged = (_, _) =>
            UpdateWebRemoteSnapshot(immediate: true);
        ChatVM.Messages.CollectionChanged += onChatChanged;
        subs.Add(Disposable.Create(() => ChatVM.Messages.CollectionChanged -= onChatChanged));

        Action onLyricsChanged = () => UpdateWebRemoteSnapshot(immediate: true);
        LyricsVM.LyricsChanged += onLyricsChanged;
        subs.Add(Disposable.Create(() => LyricsVM.LyricsChanged -= onLyricsChanged));

        // 监听设置页启停开关与端口修改（仅在 LoadLocalStateAsync 完毕后接管，避免加载瞬间重复启停）
        subs.Add(SettingsVM.WhenAnyValue(x => x.WebRemoteEnabled, x => x.WebRemotePort)
            .Skip(1)
            .Subscribe(cfg =>
            {
                if (!_webRemoteInitialized || _syncingWebRemoteSetting) return;
                _ = ApplyWebRemoteSettingsAsync(cfg.Item1, cfg.Item2);
            }));

        UpdateWebRemoteSnapshot(immediate: false);
        RefreshWebRemoteUiState();
    }

    private async Task RestoreWebRemoteAfterLoadAsync(CancellationToken ct)
    {
        if (IsDisposed) return;
        _webRemoteInitialized = true;
        if (SettingsVM.WebRemoteEnabled)
        {
            await ApplyWebRemoteSettingsAsync(true, SettingsVM.WebRemotePort, ct);
        }
        else
        {
            RefreshWebRemoteUiState();
        }
    }

    private async Task ToggleWebRemoteAsync()
    {
        var next = !SettingsVM.WebRemoteEnabled;
        _syncingWebRemoteSetting = true;
        try
        {
            SettingsVM.WebRemoteEnabled = next;
        }
        finally
        {
            _syncingWebRemoteSetting = false;
        }
        SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        await ApplyWebRemoteSettingsAsync(next, SettingsVM.WebRemotePort);
    }

    private async Task RefreshWebRemoteTokenAsync()
    {
        var freshToken = LanAddressResolver.GenerateAccessToken();
        SettingsVM.WebRemoteToken = freshToken;
        if (_webRemoteService != null)
            await _webRemoteService.RotateTokenAsync(freshToken);
        SettingsVM.SaveUiStateCommand.Execute().Subscribe();
        RefreshWebRemoteUiState();
    }

    private async Task CopyWebRemoteUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(WebRemoteUrl)) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(WebRemoteUrl);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Copy WebRemote URL failed");
        }

        _webRemoteCopyHintCts?.Cancel();
        _webRemoteCopyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _webRemoteCopyHintCts = cts;
        WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopied");
        try
        {
            await Task.Delay(1800, cts.Token);
            if (!cts.IsCancellationRequested && !IsDisposed)
                WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopyUrl");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyWebRemoteSettingsAsync(bool enabled, int port, CancellationToken ct = default)
    {
        if (_webRemoteService == null || IsDisposed)
            return;

        if (!enabled)
        {
            await _webRemoteService.StopAsync();
            RefreshWebRemoteUiState();
            return;
        }

        if (string.IsNullOrWhiteSpace(SettingsVM.WebRemoteToken))
            SettingsVM.WebRemoteToken = LanAddressResolver.GenerateAccessToken();

        var ok = await _webRemoteService.StartAsync(port, SettingsVM.WebRemoteToken, cancellationToken: ct);
        if (ok && _webRemoteService.Port != port)
        {
            _syncingWebRemoteSetting = true;
            SettingsVM.WebRemotePort = _webRemoteService.Port;
            SettingsVM.WebRemotePortText = _webRemoteService.Port.ToString();
            _syncingWebRemoteSetting = false;
        }
        else if (!ok)
        {
            _syncingWebRemoteSetting = true;
            SettingsVM.WebRemoteEnabled = false;
            _syncingWebRemoteSetting = false;
        }
        RefreshWebRemoteUiState();
    }

    private void RefreshWebRemoteUiState()
    {
        var running = _webRemoteService is { IsRunning: true };
        IsWebRemoteRunning = running;
        WebRemoteClientCount = _webRemoteService?.ConnectedClientCount ?? 0;
        WebRemoteToggleButtonText = AppLanguage.Get(running ? "S_WebRemoteStopNow" : "S_WebRemoteStartNow");

        if (running && _webRemoteService != null)
        {
            var url = _webRemoteService.PrimaryUrl;
            if (!string.Equals(WebRemoteUrl, url, StringComparison.Ordinal))
            {
                WebRemoteUrl = url;
                try
                {
                    WebRemoteQrImage = QrCodeGenerator.RenderBitmap(url, modulePixels: 5, quietModules: 4);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "QR bitmap generation failed");
                    WebRemoteQrImage = null;
                }
            }

            var clients = WebRemoteClientCount;
            WebRemoteStatusText = AppLanguage.T(
                $"运行中 · {clients} 台设备已连接",
                $"Running · {clients} connected");
            WebRemoteTooltip = AppLanguage.T(
                $"局域网 Web 遥控（运行中，{clients} 台已连接）",
                $"LAN Web Remote (running, {clients} connected)");
        }
        else
        {
            WebRemoteUrl = string.Empty;
            WebRemoteQrImage = null;
            var err = _webRemoteService?.LastError;
            WebRemoteStatusText = string.IsNullOrWhiteSpace(err)
                ? AppLanguage.Get("S_WebRemoteStopped")
                : AppLanguage.T($"启动失败：{err}", $"Start failed: {err}");
            WebRemoteTooltip = AppLanguage.Get("S_WebRemoteTitle");
        }

        if (_webRemoteCopyHintCts == null || _webRemoteCopyHintCts.IsCancellationRequested)
            WebRemoteCopyButtonText = AppLanguage.Get("S_WebRemoteCopyUrl");
    }

    private void UpdateWebRemoteSnapshot(bool immediate)
    {
        var currentTrack = _audioService.CurrentTrack;
        var hasTrack = currentTrack != null;
        var lyricLines = (LyricsVM.Lines ?? Array.Empty<LyricLine>())
            .Select(l => new WebRemoteLyricLine((long)l.Time.TotalMilliseconds, l.Text))
            .ToArray();
        var recentMsgs = ChatVM.Messages
            .TakeLast(30)
            .Select(m => new WebRemoteChatMessage(
                m.Role == Models.MessageRole.User ? "user" : "assistant",
                m.AuthorName,
                m.Content,
                m.Timestamp.ToString("HH:mm")))
            .ToArray();

            var accentColor = Converters.CharacterAccentConverter.GetAccent(SelectedCharacter);
            var accentHex = $"#{accentColor.R:X2}{accentColor.G:X2}{accentColor.B:X2}";

        _latestWebRemoteSnapshot = new WebRemoteSnapshot
        {
            Language = SettingsVM.SelectedLanguage == "en" ? "en" : "zh",
            Title = hasTrack ? PlayerVM.TrackTitle : AppLanguage.T("未播放", "Nothing playing"),
            Artist = hasTrack ? PlayerVM.TrackArtist : string.Empty,
            Source = hasTrack ? PlayerVM.TrackSource : string.Empty,
            Quality = hasTrack ? PlayerVM.TrackQualityText : string.Empty,
            IsPreview = hasTrack && PlayerVM.TrackIsPreview,
            HasTrack = hasTrack,
            IsPlaying = PlayerVM.IsPlaying,
            IsLiveStream = PlayerVM.IsLiveStream,
            IsPodcast = currentTrack?.IsPodcastEpisode ?? false,
            IsFavorite = IsCurrentFavorite,
            PositionSeconds = PlayerVM.CurrentSeconds,
            DurationSeconds = PlayerVM.TotalSeconds,
            CanSeek = hasTrack && !PlayerVM.IsLiveStream && PlayerVM.TotalSeconds > 0,
            Volume = PlayerVM.Volume,
            IsShuffled = PlayerVM.IsShuffled,
            RepeatMode = PlayerVM.RepeatMode,
            RepeatLabel = PlayerVM.RepeatText,
            RadioState = CurrentState.ToString(),
            SceneMode = CurrentSceneMode.ToString(),
            IsFocusMode = IsFocusRadioMode,
            IsSleepTimerActive = IsSleepTimerActive,
            SleepTimerText = SleepTimerDisplayStatus,
            CanStopAfterCurrentTrack = CanStopAfterCurrentTrack,
            SleepTimerMinutes = SleepTimerTargetMinutes,
            DjName = SelectedCharacter?.DisplayName ?? _djService.DjName,
            DjAccentHex = accentHex,
            IsDjSpeaking = ChatVM.IsSpeaking,
            IsDjProcessing = ChatVM.IsProcessing,
            HasLyrics = LyricsVM.HasLyrics,
            LyricStatusText = LyricsVM.LyricStatusText,
            CurrentLyricIndex = LyricsVM.CurrentLineIndex,
            LyricLines = lyricLines,
            Messages = recentMsgs,
            ServerTimeUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _webRemoteService?.NotifyStateChanged(immediate);
    }

    private Task<WebRemoteCommandResult> DispatchWebRemoteCommandAsync(WebRemoteCommand cmd)
    {
        if (IsDisposed)
            return Task.FromResult(WebRemoteCommandResult.Fail("disposed"));

        var tcs = new TaskCompletionSource<WebRemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            try
            {
                var result = ExecuteWebRemoteCommandOnMainThread(cmd);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebRemote command dispatch error: {Action}", cmd.Action);
                tcs.TrySetResult(WebRemoteCommandResult.Fail("command_failed"));
            }
        });
        return tcs.Task;
    }

    private WebRemoteCommandResult ExecuteWebRemoteCommandOnMainThread(WebRemoteCommand cmd)
    {
        if (IsDisposed)
            return WebRemoteCommandResult.Fail("disposed");

        switch (cmd.Action?.Trim().ToLowerInvariant())
        {
            case "toggle_play":
                PlayerVM.PlayCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "previous":
                PlayerVM.PreviousCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "next":
                PlayerVM.NextCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "toggle_favorite":
                ToggleCurrentFavoriteCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "toggle_shuffle":
                PlayerVM.ToggleShuffleCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "cycle_repeat":
                PlayerVM.ToggleRepeatCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "set_volume":
                if (cmd.Value is not double vol || double.IsNaN(vol) || double.IsInfinity(vol))
                    return WebRemoteCommandResult.Fail("invalid_volume");
                var clampedVol = (float)Math.Clamp(vol, 0.0, 1.0);
                _audioService.Volume = clampedVol;
                PlayerVM.Volume = clampedVol;
                UpdateWebRemoteSnapshot(immediate: true);
                return WebRemoteCommandResult.Success();

            case "seek":
                if (cmd.Value is not double sec || double.IsNaN(sec) || double.IsInfinity(sec))
                    return WebRemoteCommandResult.Fail("invalid_position");
                if (_audioService.CurrentTrack is { IsLiveStream: true })
                    return WebRemoteCommandResult.Fail("live_stream_not_seekable");
                var maxDur = _audioService.Duration.TotalSeconds;
                var clampedSec = maxDur > 0 ? Math.Clamp(sec, 0.0, maxDur) : Math.Max(0.0, sec);
                _audioService.Seek(TimeSpan.FromSeconds(clampedSec));
                return WebRemoteCommandResult.Success();

            case "toggle_focus_mode":
                ToggleFocusModeCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            case "set_sleep_timer":
                if (cmd.IntValue is not int mins)
                    return WebRemoteCommandResult.Fail("invalid_minutes");
                if (mins is 0 or 15 or 30 or 45 or 60 or 90 or -1)
                {
                    SetSleepTimerCommand.Execute(mins).Subscribe();
                    return WebRemoteCommandResult.Success();
                }
                return WebRemoteCommandResult.Fail("unsupported_duration");

            case "feedback":
                switch (cmd.Text?.Trim().ToLowerInvariant())
                {
                    case "like":
                        if (_audioService.CurrentTrack != null && !IsCurrentFavorite)
                            ToggleCurrentFavoriteCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "dislike":
                        DislikeCurrentTrackCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "similar":
                        SimilarToCurrentTrackCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "calm":
                        CalmerRecommendationCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "energetic":
                        EnergeticRecommendationCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    case "story":
                        TellSongStoryCommand.Execute().Subscribe();
                        return WebRemoteCommandResult.Success();
                    default:
                        return WebRemoteCommandResult.Fail("unknown_feedback");
                }

            case "chat":
                var chatText = cmd.Text?.Trim();
                if (string.IsNullOrWhiteSpace(chatText))
                    return WebRemoteCommandResult.Fail("empty_message");
                if (chatText.Length > 500)
                    chatText = chatText[..500];
                if (ChatVM.IsProcessing)
                    return WebRemoteCommandResult.Fail("dj_busy");
                ChatVM.InputText = chatText;
                ChatVM.SendMessageCommand.Execute().Subscribe();
                return WebRemoteCommandResult.Success();

            default:
                return WebRemoteCommandResult.Fail("unknown_action");
        }
    }

    private void DisposeWebRemote()
    {
        _webRemoteCopyHintCts?.Cancel();
        _webRemoteCopyHintCts?.Dispose();
        _webRemoteCopyHintCts = null;
        _webRemoteSubs?.Dispose();
        _webRemoteSubs = null;
        try { _webRemoteService?.StopAsync().Wait(TimeSpan.FromMilliseconds(400)); } catch { }
    }
}
