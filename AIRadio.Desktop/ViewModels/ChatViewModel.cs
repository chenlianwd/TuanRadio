using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class ChatViewModel : ViewModelBase, IDisposable
{
    private readonly IDJService _djService;
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly ISttService _sttService;
    private readonly IRecommendationService? _recommendationService;
    private readonly Action<Track>? _trackAdded;
    private readonly IDisposable _ttsSub;
    private readonly IDisposable _ttsCommandSub;
    private readonly IDisposable _ttsErrorSub;
    private readonly IDisposable _stateSub;
    private readonly Action _onLanguageChanged;
    private string? _pendingCommand;
    private Track? _pendingRecommendedTrack;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _tempWavPath;
    private bool _isPlayingSong;
    private bool _sendAfterHoldToTalk;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;
    [Reactive] public bool HasFailure { get; set; }
    private bool _isStatusNoticeDismissed;
    private string _failureStatusText = "AI ERROR";
    // 保留未本地化的原始失败信息，语言切换时按当前语言重译常驻/可召回通知
    private ApiFailureInfo? _failureInfo;
    private IDisposable? _statusAutoDismissSub;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [Reactive] public string InputText { get; set; } = string.Empty;
    [Reactive] public bool IsProcessing { get; set; }
    [Reactive] public bool IsListening { get; set; }
    [Reactive] public bool IsRecognizing { get; set; }
    [Reactive] public bool IsSpeaking { get; set; }
    [Reactive] public bool IsConversationMode { get; set; }
    [Reactive] public string DjEmotion { get; set; } = "neutral";
    [Reactive] public string StatusText { get; set; } = "READY";
    [Reactive] public string StatusHeadline { get; set; } = string.Empty;
    [Reactive] public string StatusDetail { get; set; } = string.Empty;
    [Reactive] public string StatusRecoveryHint { get; set; } = string.Empty;
    [Reactive] public bool ShowStatusNotice { get; set; }
    [Reactive] public bool ShowStatusRecall { get; set; }
    [Reactive] public string MicButtonText { get; set; } = "HOLD";

    public event Action<string, string>? DjVisualCue; // expression, motion

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVoiceInputCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConversationModeCommand { get; }
    public ReactiveCommand<Unit, Unit> DismissStatusNoticeCommand { get; }
    public ReactiveCommand<Unit, Unit> RestoreStatusNoticeCommand { get; }

    public ChatViewModel(IDJService djService, IAudioService audioService, IMusicSearchService musicSearchService, ISttService sttService, Action<Track>? trackAdded = null, IRecommendationService? recommendationService = null)
    {
        _djService = djService;
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _sttService = sttService;
        _recommendationService = recommendationService;
        _trackAdded = trackAdded;

        SendMessageCommand = ReactiveCommand.CreateFromTask(
            SendMessageAsync,
            this.WhenAnyValue(x => x.IsProcessing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(p => !p));

        ToggleVoiceInputCommand = ReactiveCommand.Create(ToggleVoiceInput);
        ToggleConversationModeCommand = ReactiveCommand.Create(ToggleConversationMode);
        DismissStatusNoticeCommand = ReactiveCommand.Create(DismissStatusNotice);
        RestoreStatusNoticeCommand = ReactiveCommand.Create(RestoreStatusNotice);

        // Listen for TTS completion to play song AFTER TTS finishes
        _ttsSub = _audioService.TtsStateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(playing =>
            {
                IsSpeaking = playing;
                RefreshStatus();
            });

        // Handle pending command after TTS ends (separate subscription to avoid async void)
        _ttsCommandSub = _audioService.TtsStateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(playing => !playing && _pendingCommand != null)
            .Select(_ =>
            {
                var cmd = _pendingCommand!;
                _pendingCommand = null;
                return cmd;
            })
            .SelectMany(cmd => Observable.FromAsync(() => ExecuteCommandAsync(cmd)))
            .Subscribe();

        _ttsErrorSub = _audioService.TtsError
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(message =>
            {
                SetFailureNotice(new ApiFailureInfo(
                    ApiFailureKind.InvalidResponse,
                    AppLanguage.T("语音播放失败", "Voice playback failed"),
                    message,
                    AppLanguage.T("可以在设置里暂时关闭语音播报，或检查系统默认音频输出设备。", "You can turn off voice playback in Settings for now, or check the default audio output device.")));

                // TTS failed — still execute pending command so user action is not lost
                if (_pendingCommand != null)
                {
                    var cmd = _pendingCommand;
                    _pendingCommand = null;
                    _ = ExecuteCommandAsync(cmd).ContinueWith(
                        t => Log.Warning(t.Exception, "ExecuteCommand failed after TTS error"),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            });

        // Listen for track end to restart listening in conversation mode
        _stateSub = _audioService.StateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(state =>
            {
                if (IsConversationMode && !IsProcessing && !IsListening &&
                    state == Models.PlaybackState.Ended)
                {
                    StartListening();
                }
            });

        _onLanguageChanged = () =>
        {
            foreach (var message in Messages)
                message.RefreshLocalization();
            if (HasFailure && _failureInfo != null)
            {
                var localized = ApiFailureLocalization.ForCurrentLanguage(_failureInfo);
                StatusHeadline = localized.Title;
                StatusDetail = localized.Detail;
                StatusRecoveryHint = localized.RecoveryHint;
            }
            RefreshStatus();
        };
        AppLanguage.Changed += _onLanguageChanged;
    }

    public void AddAssistantMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = text
        });
    }

    private void ToggleVoiceInput()
    {
        if (IsConversationMode)
        {
            IsConversationMode = false;
            StopListening();
            return;
        }
        if (IsListening)
            StopListening();
        else
            StartListening();
    }

    public void BeginHoldToTalk()
    {
        if (!IsListening && !IsRecognizing && !IsProcessing)
        {
            _sendAfterHoldToTalk = true;
            StartListening();
        }
    }

    public void EndHoldToTalk()
    {
        if (IsListening)
            StopListening();
    }

    private void ToggleConversationMode()
    {
        if (IsConversationMode)
        {
            IsConversationMode = false;
            StopListening();
        }
        else
        {
            IsConversationMode = true;
            StartListening();
        }
    }

    private void StartListening()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        WaveInEvent? waveIn = null;
        WaveFileWriter? writer = null;
        string? wavPath = null;
        EventHandler<WaveInEventArgs>? dataHandler = null;
        EventHandler<StoppedEventArgs>? stoppedHandler = null;
        try
        {
            wavPath = Path.Combine(Path.GetTempPath(), $"stt_{Guid.NewGuid():N}.wav");

            waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1)
            };
            writer = new WaveFileWriter(wavPath, waveIn.WaveFormat);
            _tempWavPath = wavPath;
            _waveIn = waveIn;
            _waveWriter = writer;

            dataHandler = (_, e) =>
            {
                try { writer.Write(e.Buffer, 0, e.BytesRecorded); }
                catch (ObjectDisposedException) { }
            };

            stoppedHandler = (_, _) =>
            {
                waveIn.DataAvailable -= dataHandler;
                waveIn.RecordingStopped -= stoppedHandler;
                try { writer.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Failed to close WAV writer"); }
                try { waveIn.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Failed to close recording device"); }

                if (ReferenceEquals(_waveIn, waveIn))
                {
                    _waveIn = null;
                    _waveWriter = null;
                    _tempWavPath = null;
                }

                if (Volatile.Read(ref _disposed) == 0)
                {
                    _ = RecognizeFromWavAsync(wavPath, _lifetimeCts.Token).ContinueWith(
                        t => Log.Warning(t.Exception, "RecognizeFromWav failed"),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
                else
                {
                    try { File.Delete(wavPath); } catch { }
                }
            };

            waveIn.DataAvailable += dataHandler;
            waveIn.RecordingStopped += stoppedHandler;
            waveIn.StartRecording();
            IsListening = true;
            IsRecognizing = false;
            MicButtonText = "HOLD";
            RefreshStatus();
            Log.Information("Mic recording started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start mic recording");
            if (waveIn != null)
            {
                if (dataHandler != null)
                    waveIn.DataAvailable -= dataHandler;
                if (stoppedHandler != null)
                    waveIn.RecordingStopped -= stoppedHandler;
            }
            try { writer?.Dispose(); } catch { }
            try { waveIn?.Dispose(); } catch { }
            if (!string.IsNullOrWhiteSpace(wavPath))
            {
                try { File.Delete(wavPath); } catch { }
            }
            if (ReferenceEquals(_waveIn, waveIn))
            {
                _waveIn = null;
                _waveWriter = null;
                _tempWavPath = null;
            }
            IsListening = false;
            IsRecognizing = false;
            MicButtonText = "HOLD";
            StatusText = "MIC ERROR";
        }
    }

    private void StopListening()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            _waveIn?.StopRecording();
            IsListening = false;
            IsRecognizing = true;
            MicButtonText = "HOLD";
            RefreshStatus();
            Log.Information("Mic recording stopped");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping mic");
            IsListening = false;
            IsRecognizing = false;
            MicButtonText = "HOLD";
            StatusText = "MIC ERROR";
        }
    }

    private async Task RecognizeFromWavAsync(string wavPath, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _sttService.TranscribeAsync(wavPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(text))
            {
                var sendAfterRecognition = false;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        return;

                    InputText = text;
                    sendAfterRecognition = IsConversationMode || _sendAfterHoldToTalk;
                });
                Log.Information("Speech recognized: {Text}", text);

                // Hold-to-talk should feel like talking to the DJ directly.
                if (sendAfterRecognition)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            SendMessageCommand.Execute().Subscribe(
                                _ => { },
                                error => Log.Warning(error, "Voice message send failed"));
                        }
                    });
                }
            }
            else
            {
                Log.Warning("No speech recognized");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        StatusText = "NO SPEECH";
                        if (IsConversationMode)
                            StartListening();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Debug("Speech recognition cancelled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Speech recognition failed");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                    StatusText = "STT ERROR";
            });
        }
        finally
        {
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        IsRecognizing = false;
                        MicButtonText = "HOLD";
                        RefreshStatus();
                        _sendAfterHoldToTalk = false;
                    }
                });
            }
            catch (Exception ex) { Log.Debug(ex, "Failed to update STT state during shutdown"); }
            try { File.Delete(wavPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete temp file"); }
        }
    }

    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var userMsg = new ChatMessage
        {
            Role = MessageRole.User,
            Content = InputText
        };
        Messages.Add(userMsg);
        var text = InputText;
        InputText = string.Empty;
        IsProcessing = true;
        RefreshStatus();
        SetWorkingNotice(AppLanguage.T("AI 正在回复", "AI is replying"), AppLanguage.T("正在请求 AI 服务，最多等待 30 秒。", "Requesting the AI service; waiting up to 30 seconds."));
        _pendingCommand = null;
        _pendingRecommendedTrack = null;

        try
        {
            await DjTtsInterop.StopTtsWithoutBlockingUiAsync(_audioService, _lifetimeCts.Token);
            if (IsFreshRecommendationRequest(text))
            {
                await RecommendFreshTrackAsync();
                return;
            }

            if (TryParseSongRequest(text, out var songQuery, out var requiresConfidentMatch, out var isArtistRequest) &&
                (!requiresConfidentMatch || await HasConfidentSongMatchAsync(songQuery)))
            {
                var displayText = isArtistRequest
                    ? AppLanguage.T($"好，我来找{songQuery}的歌。", $"OK, finding songs by {songQuery}.")
                    : AppLanguage.T($"好，我来找《{songQuery}》。", $"OK, let me find \"{songQuery}\".");
                var playCommand = isArtistRequest
                    ? $"play_artist:{songQuery}"
                    : $"play:{songQuery}";
                await RespondWithCommandAsync(displayText, playCommand, "happy");
                return;
            }

            var response = await _djService.GenerateChatResponseAsync(text);
            if (_djService.LastFailure is { } chatFailure)
            {
                AddFailureMessage(AppLanguage.T("AI 回复失败", "AI reply failed"), chatFailure);
                SetFailureNotice(chatFailure);
                return;
            }

            // LLM not configured — show setup prompt instead of raw message
            if (response.StartsWith("请先在设置中配置", StringComparison.Ordinal) ||
                response.StartsWith("Configure the AI service", StringComparison.OrdinalIgnoreCase))
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = AppLanguage.T("AI 服务尚未配置。请在设置中填写 API Key 后再试。", "AI service is not configured. Fill in your API key in Settings and try again.")
                });
                SetFailureNotice(new ApiFailureInfo(
                    ApiFailureKind.MissingApiKey,
                    AppLanguage.T("AI 服务未配置", "AI service not configured"),
                    AppLanguage.T("需要在设置中配置 LLM API Key 才能使用 AI 对话功能。", "An LLM API key is required in Settings to use AI chat."),
                    AppLanguage.T("打开设置页填写 API Key。", "Open Settings and enter the API key.")));
                return;
            }

            var parsed = ParseDjResponse(response);
            await RespondWithCommandAsync(parsed.DisplayText, parsed.Command, parsed.Emotion);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            Log.Debug("Chat request cancelled during shutdown");
        }
        catch (Exception ex)
        {
            var failure = ApiFailureLocalization.ForCurrentLanguage(ApiFailureInfo.FromException(ex));
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = AppLanguage.T($"AI 回复失败：{failure.Title}。{failure.RecoveryHint}", $"AI reply failed: {failure.Title}. {failure.RecoveryHint}")
            });
            SetFailureNotice(failure);
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                IsProcessing = false;
                RefreshStatus();
            }
        }
    }

    private async Task RespondWithCommandAsync(string displayText, string? command, string emotion)
    {
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = displayText
        });
        DjEmotion = emotion;
        DjVisualCue?.Invoke(MapExpression(DjEmotion), MapMotion(DjEmotion));

        if (command != null && !_djService.TtsEnabled)
            await ExecuteCommandAsync(command);
        else if (command != null && _djService.TtsEnabled)
            _pendingCommand = command;

        var ttsText = StripEmoji(displayText);
        if (_djService.TtsEnabled && !string.IsNullOrWhiteSpace(ttsText))
        {
            StatusText = "VOICE...";
            SetWorkingNotice(AppLanguage.T("正在生成语音", "Generating voice"), AppLanguage.T("AI 文字已返回，正在调用语音服务。", "AI text is back; calling the voice service."));
            var speechData = await DjTtsInterop.GenerateSpeechAsync(_djService, ttsText, _lifetimeCts.Token);
            if (speechData is { Length: > 0 })
            {
                _audioService.PlayTtsAudio(speechData);
            }
            else
            {
                if (_djService.LastFailure is { } speechFailure)
                    SetFailureNotice(speechFailure);
                else
                    SetFailureNotice(new ApiFailureInfo(
                        ApiFailureKind.InvalidResponse,
                        AppLanguage.T("语音生成失败", "Voice generation failed"),
                        AppLanguage.T("语音服务没有返回可播放的音频数据。", "The voice service returned no playable audio."),
                        AppLanguage.T("检查 API Key、账号权限和 TTS 额度后重试。", "Check the API key, account permissions and TTS quota, then retry.")));
                Log.Warning("TTS returned empty audio");
                if (_pendingCommand != null)
                {
                    var cmd = _pendingCommand;
                    _pendingCommand = null;
                    await ExecuteCommandAsync(cmd);
                }
            }
        }
    }

    private async Task SpeakAsync(string displayText)
    {
        if (!_djService.TtsEnabled)
        {
            StatusText = "VOICE OFF";
            return;
        }

        var ttsText = StripEmoji(displayText);
        if (string.IsNullOrWhiteSpace(ttsText)) return;

        StatusText = "VOICE...";
        SetWorkingNotice(AppLanguage.T("正在生成语音", "Generating voice"), AppLanguage.T("正在调用语音服务。", "Calling the voice service."));
        var speechData = await DjTtsInterop.GenerateSpeechAsync(_djService, ttsText, _lifetimeCts.Token);
        if (speechData is { Length: > 0 })
        {
            _audioService.PlayTtsAudio(speechData);
        }
        else
        {
            if (_djService.LastFailure is { } speechFailure)
                SetFailureNotice(speechFailure);
            else
                SetFailureNotice(new ApiFailureInfo(
                    ApiFailureKind.InvalidResponse,
                    AppLanguage.T("语音生成失败", "Voice generation failed"),
                    AppLanguage.T("语音服务没有返回可播放的音频数据。", "The voice service returned no playable audio."),
                    AppLanguage.T("检查 API Key、账号权限和 TTS 额度后重试。", "Check the API key, account permissions and TTS quota, then retry.")));
            Log.Warning("TTS returned empty audio");
        }
    }

    private void RefreshStatus()
    {
        if (HasFailure && !IsProcessing && !IsSpeaking && !IsRecognizing && !IsListening)
        {
            StatusText = _failureStatusText;
            ShowStatusNotice = !_isStatusNoticeDismissed;
            ShowStatusRecall = _isStatusNoticeDismissed;
            return;
        }

        StatusText = IsSpeaking ? "SPEAKING"
            : IsRecognizing ? "RECOGNIZING"
            : IsListening ? "LISTENING"
            : IsProcessing ? "THINKING"
            : IsConversationMode ? "CONVERSATION"
            : "READY";

        if (IsProcessing)
        {
            SetWorkingNotice(AppLanguage.T("AI 正在回复", "AI is replying"), AppLanguage.T("正在请求 AI 服务，最多等待 30 秒。", "Requesting the AI service; waiting up to 30 seconds."));
        }
        else if (IsSpeaking)
        {
            SetWorkingNotice(AppLanguage.T("正在播报语音", "Speaking"), AppLanguage.T("AI 回复已生成，正在播放 TTS 音频。", "AI reply is ready; playing TTS audio."));
        }
        else
        {
            ShowStatusNotice = false;
            ShowStatusRecall = false;
        }
    }

    private void SetWorkingNotice(string headline, string detail)
    {
        HasFailure = false;
        _failureInfo = null;
        _isStatusNoticeDismissed = false;
        StatusHeadline = headline;
        StatusDetail = detail;
        StatusRecoveryHint = string.Empty;
        ShowStatusNotice = true;
        ShowStatusRecall = false;
    }

    private void SetFailureNotice(ApiFailureInfo failure)
    {
        _failureInfo = failure;
        failure = ApiFailureLocalization.ForCurrentLanguage(failure);
        HasFailure = true;
        _isStatusNoticeDismissed = false;
        _failureStatusText = failure.Kind switch
        {
            ApiFailureKind.MissingApiKey => "API KEY MISSING",
            ApiFailureKind.Authentication => "API AUTH ERROR",
            ApiFailureKind.Timeout => "AI TIMEOUT",
            ApiFailureKind.Network => "NETWORK ERROR",
            ApiFailureKind.RateLimited => "API LIMITED",
            _ => "AI ERROR"
        };
        StatusText = _failureStatusText;
        StatusHeadline = failure.Title;
        StatusDetail = failure.Detail;
        StatusRecoveryHint = failure.RecoveryHint;
        ShowStatusNotice = true;
        ShowStatusRecall = false;
        ScheduleStatusNoticeAutoDismiss();
    }

    private void DismissStatusNotice()
    {
        if (string.IsNullOrWhiteSpace(StatusHeadline))
            return;

        _isStatusNoticeDismissed = true;
        ShowStatusNotice = false;
        ShowStatusRecall = HasFailure;
    }

    private void RestoreStatusNotice()
    {
        if (string.IsNullOrWhiteSpace(StatusHeadline))
            return;

        _isStatusNoticeDismissed = false;
        ShowStatusNotice = true;
        ShowStatusRecall = false;
    }

    private void ScheduleStatusNoticeAutoDismiss()
    {
        _statusAutoDismissSub?.Dispose();
        _statusAutoDismissSub = Observable
            .Timer(TimeSpan.FromSeconds(6), RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (HasFailure && ShowStatusNotice)
                    DismissStatusNotice();
            });
    }

    private void AddFailureMessage(string prefix, ApiFailureInfo failure)
    {
        failure = ApiFailureLocalization.ForCurrentLanguage(failure);
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = AppLanguage.T(
                $"{prefix}：{failure.Title}。{failure.RecoveryHint}",
                $"{prefix}: {failure.Title}. {failure.RecoveryHint}")
        });
    }

    public static DjResponse ParseDjResponse(string response)
    {
        var emotion = "neutral";
        var emotionMatch = Regex.Match(response, @"\[(happy|sad|calm|neutral|angry|surprised)\]", RegexOptions.IgnoreCase);
        if (emotionMatch.Success)
            emotion = emotionMatch.Groups[1].Value.ToLowerInvariant();

        var displayText = Regex.Replace(response, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);
        string? command = null;

        var jsonMatch = Regex.Match(displayText, @"<cmd>\s*(\{.*?\})\s*</cmd>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (jsonMatch.Success)
        {
            command = ParseJsonCommand(jsonMatch.Groups[1].Value);
            displayText = displayText.Remove(jsonMatch.Index, jsonMatch.Length);
        }
        else
        {
            var legacyMatch = Regex.Match(displayText, @"【(play:.+?|next|pause|resume)】\s*$", RegexOptions.IgnoreCase);
            if (legacyMatch.Success)
            {
                displayText = displayText[..legacyMatch.Index];
                command = legacyMatch.Groups[1].Value;
            }
        }

        return new DjResponse(displayText.Trim(), command, emotion);
    }

    private static string? ParseJsonCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var actionElement))
                return null;
            var action = actionElement.GetString()?.Trim().ToLowerInvariant();
            return action switch
            {
                "play" when root.TryGetProperty("query", out var query) => $"play:{query.GetString()?.Trim()}",
                "next" => "next",
                "pause" => "pause",
                "resume" => "resume",
                "recommend_more" => "recommend_more",
                "change_mood" when root.TryGetProperty("mood", out var mood) => $"change_mood:{mood.GetString()?.Trim()}",
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> HasConfidentSongMatchAsync(string query)
    {
        try
        {
            var results = await SearchMusicAsync(query, 3, _lifetimeCts.Token);
            return results.Count > 0 && IsConfidentMusicMatch(query, results[0]);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preflight song request: {Query}", query);
            return false;
        }
    }

    private async Task RecommendFreshTrackAsync()
    {
        var current = _audioService.CurrentTrack ?? _audioService.Playlist.LastOrDefault();
        var recentlyPlayed = GetRecentlyPlayedSnapshot();
        if (current != null)
        {
            current.Tag = new RecommendationContext
            {
                Favorites = _audioService.Playlist.Where(t => t.IsFavorite).ToList(),
                RecentlyPlayed = recentlyPlayed,
                ExcludedTracks = _audioService.Playlist.Concat(recentlyPlayed).ToList()
            };
        }

        // 节目单推荐优先（会话级氛围偏好在节目单路径生效），失败/无结果再回退 DJ 单曲推荐
        Track? recommended = null;
        try
        {
            recommended = await RequestProgramRecommendationAsync(current, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Program recommendation failed for chat request, falling back to DJ single-track");
        }

        if (recommended == null)
            recommended = await DjTtsInterop.RequestDjRecommendationAsync(_djService, current, _lifetimeCts.Token);

        if (recommended == null)
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = AppLanguage.T("暂时没找到新的可播放推荐。你可以给我一个风格或歌手关键词，我继续帮你找。", "No new playable recommendations right now. Give me a genre or artist keyword and I'll keep looking.")
            });
            return;
        }

        if (!_audioService.Playlist.Any(t => IsSameTrack(t, recommended)))
        {
            if (_trackAdded != null)
                _trackAdded(recommended);
            else
                _audioService.AddTracks(new[] { recommended });
        }

        // 经 pending 机制在 TTS 播完后切歌，与 play: 指令的行为一致
        _pendingRecommendedTrack = recommended;
        var displayText = AppLanguage.T($"给你推荐《{recommended.Title}》 - {recommended.DisplayArtist}。", $"Here's a pick for you: \"{recommended.Title}\" - {recommended.DisplayArtist}.");
        await RespondWithCommandAsync(displayText, "play_recommended", "happy");
    }

    private void PlayPendingRecommendedTrack()
    {
        var track = _pendingRecommendedTrack;
        _pendingRecommendedTrack = null;
        if (track == null) return;

        // TTS 期间列表可能变化，播放前重查索引，避免旧索引指向错误曲目
        var index = FindAudioTrackIndex(track.SourceId ?? track.Id, track.FilePath);
        if (index >= 0)
            _audioService.PlayAtIndex(index);
    }

    private Task<Track?> RequestProgramRecommendationAsync(
        Track? current,
        CancellationToken cancellationToken)
    {
        if (_recommendationService == null)
            return Task.FromResult<Track?>(null);

        var recentlyPlayed = GetRecentlyPlayedSnapshot();
        var request = new RecommendationRequest
        {
            UserIntentKey = RecommendationIntentKeys.ContinueStation,
            CurrentTrack = current,
            Favorites = _audioService.Playlist.Where(t => t.IsFavorite).ToList(),
            Playlist = _audioService.Playlist.ToList(),
            RecentlyPlayed = recentlyPlayed,
            ExcludedTracks = _audioService.Playlist.Concat(recentlyPlayed).ToList()
        };

        return _recommendationService is RecommendationService recommendationService
            ? recommendationService.GetNextTrackAsync(request, cancellationToken)
            : _recommendationService.GetNextTrackAsync(request).WaitAsync(cancellationToken);
    }

    private List<Track> GetRecentlyPlayedSnapshot()
        => _recommendationService?.RecentlyPlayed?.ToList() ?? new List<Track>();

    private static bool IsFreshRecommendationRequest(string text)
    {
        var normalized = NormalizeSongQuery(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        if (Regex.IsMatch(normalized, @"^(?:请|麻烦|帮我|给我)?\s*(?:播放|放|听|我想听|想听)\s+"))
            return false;

        return Regex.IsMatch(
            normalized,
            @"(推荐|推歌|换一首|来一首|来首|下一首|同类型|类似|相似|新歌|没听过|别的).*(歌|歌曲|音乐)?",
            RegexOptions.IgnoreCase);
    }

    private static bool TryParseSongRequest(
        string text,
        out string query,
        out bool requiresConfidentMatch,
        out bool isArtistRequest)
    {
        query = string.Empty;
        requiresConfidentMatch = false;
        isArtistRequest = false;
        var normalized = NormalizeSongQuery(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var explicitMatch = Regex.Match(
            normalized,
            @"^(?:请|麻烦|帮我|给我)?\s*(?:播放|放一下|放下|放|听一下|听下|听|来一首|来首|点一首|点首|我想听|想听)\s*(?:一首|首)?\s*(?<query>.+)$",
            RegexOptions.IgnoreCase);
        if (explicitMatch.Success)
        {
            query = NormalizeMusicSearchQuery(explicitMatch.Groups["query"].Value, out isArtistRequest);
            return !string.IsNullOrWhiteSpace(query) && !IsGenericMusicRequest(query);
        }

        if (!LooksLikeBareSongTitle(normalized))
            return false;

        query = normalized;
        requiresConfidentMatch = true;
        return true;
    }

    private static string NormalizeMusicSearchQuery(string query, out bool isArtistRequest)
    {
        isArtistRequest = false;
        var normalized = NormalizeSongQuery(query);
        var artistMatch = Regex.Match(
            normalized,
            @"^(?<artist>.+?)\s*的\s*(?:歌|歌曲|音乐)$",
            RegexOptions.IgnoreCase);

        if (!artistMatch.Success)
            return normalized;

        var artist = NormalizeSongQuery(artistMatch.Groups["artist"].Value);
        if (string.IsNullOrWhiteSpace(artist) || IsGenericMusicRequest(artist))
            return normalized;

        isArtistRequest = true;
        return artist;
    }

    private static string NormalizeSongQuery(string text)
    {
        var value = text.Trim();
        value = Regex.Replace(value, @"^[\s""'“”‘’《<]+|[\s""'“”‘’》>。.!！]+$", "");
        value = Regex.Replace(value, @"\s+", " ");
        return value.Trim();
    }

    private static bool LooksLikeBareSongTitle(string text)
    {
        if (text.Length is < 1 or > 24)
            return false;
        if (Regex.IsMatch(text, @"[?？,，。.!！;；:]"))
            return false;
        if (Regex.IsMatch(text, @"^(为什么|怎么|如何|你好|谢谢|再见)"))
            return false;
        if (Regex.IsMatch(text, @"^(换|切|跳过|下一首|上一首|换歌|换一首|切歌|暂停|继续|播放|停止|快进|后退)"))
            return false;

        return Regex.IsMatch(text, @"^[\p{IsCJKUnifiedIdeographs}A-Za-z0-9\s\-'&.]+$");
    }

    private static bool IsGenericMusicRequest(string text)
    {
        return Regex.IsMatch(text, @"^(歌|歌曲|音乐|一首歌|首歌|点歌)$", RegexOptions.IgnoreCase);
    }

    private static bool IsConfidentMusicMatch(string query, OnlineTrack track)
    {
        var normalizedQuery = NormalizeForMusicCompare(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return false;

        var title = NormalizeForMusicCompare(track.Title);
        var artist = NormalizeForMusicCompare(track.Artist);
        if (normalizedQuery == title || normalizedQuery == artist)
            return true;

        return normalizedQuery.Length >= 5 &&
               (title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                normalizedQuery.Contains(title, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForMusicCompare(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), @"[\s""'“”‘’《》<>。.!！?？,，;；:\-_/\\]+", "");
    }

    private static string StripEmoji(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // First strip emotion tags like [happy] [neutral] etc
        text = Regex.Replace(text, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);

        var sb = new System.Text.StringBuilder(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var codePoint = char.ConvertToUtf32(element, 0);
            // Skip emoji and symbol ranges
            if (codePoint >= 0x1F600 && codePoint <= 0x1F64F) continue; // emoticons
            if (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) continue; // symbols & pictographs
            if (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) continue; // transport & map
            if (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) continue; // supplemental
            if (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) continue; // chess symbols
            if (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) continue; // extended-A
            if (codePoint >= 0x2600 && codePoint <= 0x26FF) continue;   // misc symbols
            if (codePoint >= 0x2700 && codePoint <= 0x27BF) continue;   // dingbats
            if (codePoint >= 0xFE00 && codePoint <= 0xFE0F) continue;   // variation selectors
            if (codePoint == 0x200D) continue;                          // zero-width joiner
            if (codePoint >= 0xE0020 && codePoint <= 0xE007F) continue; // tag characters
            sb.Append(element);
        }
        return sb.ToString().Trim();
    }

    private async Task ExecuteCommandAsync(string command)
    {
        try
        {
            if (command.StartsWith("play_artist:", StringComparison.OrdinalIgnoreCase))
            {
                var query = command["play_artist:".Length..].Trim();
                await PlaySongAsync(query, preferArtistMatch: true);
            }
            else if (command.StartsWith("play:"))
            {
                var query = command["play:".Length..].Trim();
                await PlaySongAsync(query);
            }
            else if (command == "next")
            {
                _audioService.Next();
            }
            else if (command == "pause")
            {
                _audioService.Pause();
            }
            else if (command == "resume")
            {
                _audioService.Play();
            }
            else if (command == "recommend_more")
            {
                await RecommendFreshTrackAsync();
            }
            else if (command == "play_recommended")
            {
                PlayPendingRecommendedTrack();
            }
            else if (command.StartsWith("change_mood:", StringComparison.OrdinalIgnoreCase))
            {
                var mood = command["change_mood:".Length..].Trim();
                // 会话级氛围偏好：真正影响后续节目单的意图检测与搜索词
                _recommendationService?.SetMoodBias(mood);
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = string.IsNullOrWhiteSpace(mood)
                        ? AppLanguage.T("我会调整接下来的推荐方向。", "I'll adjust what I play next.")
                        : AppLanguage.T($"我会把接下来的推荐调成 {mood} 一点。", $"I'll tune the next picks toward {mood}.")
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to execute DJ command: {Command}", command);
        }
    }

    private async Task PlaySongAsync(string query, bool preferArtistMatch = false)
    {
        if (_isPlayingSong) return;
        _isPlayingSong = true;
        try
        {
            var searchQuery = NormalizeMusicSearchQuery(query, out var normalizedArtistRequest);
            preferArtistMatch |= normalizedArtistRequest;
            Log.Information("DJ play request: {Query}", searchQuery);

            var results = await SearchMusicAsync(searchQuery, 5, _lifetimeCts.Token);
            Log.Debug("DJ search returned {Count} results", results.Count);
            if (results.Count == 0)
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = AppLanguage.T("没找到这首歌，换个关键词试试？", "Couldn't find that track; try another keyword?")
                });
                return;
            }

            var track = SelectBestTrack(searchQuery, results, preferArtistMatch);
            Log.Debug("DJ got track: {Track}, fetching URL...", track.Title);
            var url = await ResolvePlayUrlAsync(track, _lifetimeCts.Token);
            Log.Debug("DJ got URL: {Url}", url != null ? "present" : "null");
            if (url == null)
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = AppLanguage.T("这首歌暂时无法播放，换一首吧？", "That track can't be played right now; try another?")
                });
                return;
            }

            var existingIndex = FindAudioTrackIndex(track.Id, url);
            if (existingIndex >= 0)
            {
                Log.Debug("Track already in playlist at index {Index}, playing", existingIndex);
                _audioService.PlayAtIndex(existingIndex);
                return;
            }

            var t = track.ToTrack(url);
            Log.Debug("Adding track to playlist and playing...");
            if (_trackAdded != null)
                _trackAdded(t);
            else
                _audioService.AddTracks(new[] { t });

            var index = FindAudioTrackIndex(t.SourceId ?? t.Id, t.FilePath);
            if (index < 0)
            {
                _audioService.AddTracks(new[] { t });
                index = _audioService.Playlist.Count - 1;
            }
            _audioService.PlayAtIndex(index);
            Log.Information("DJ track play initiated: {Track}", t);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            Log.Debug("DJ play request cancelled during shutdown");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlaySongAsync failed for query: {Query}", query);
        }
        finally
        {
            _isPlayingSong = false;
        }
    }

    private static OnlineTrack SelectBestTrack(
        string query,
        IReadOnlyList<OnlineTrack> results,
        bool preferArtistMatch)
    {
        if (!preferArtistMatch)
            return results[0];

        var normalizedQuery = NormalizeForMusicCompare(query);
        if (normalizedQuery.Length >= 2)
        {
            var artists = results
                .Select(track => (Track: track, Artist: NormalizeForMusicCompare(track.Artist)))
                .Where(candidate => candidate.Artist.Length > 0)
                .ToList();
            var artistMatch = artists
                .FirstOrDefault(candidate => candidate.Artist.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                .Track
                ?? artists
                    .FirstOrDefault(candidate => candidate.Artist.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    .Track
                ?? artists
                    .FirstOrDefault(candidate => normalizedQuery.Contains(candidate.Artist, StringComparison.OrdinalIgnoreCase))
                    .Track;

            if (artistMatch != null)
                return artistMatch;
        }

        return results[0];
    }

    private Task<System.Collections.Generic.List<OnlineTrack>> SearchMusicAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (_musicSearchService is MultiSourceMusicService multi)
            return multi.SearchAsync(query, limit, cancellationToken);

        return _musicSearchService.SearchAsync(query, limit)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private Task<string?> ResolvePlayUrlAsync(
        OnlineTrack track,
        CancellationToken cancellationToken)
    {
        if (_musicSearchService is MultiSourceMusicService multi)
            return multi.GetPlayUrlAsync(track, cancellationToken);

        return _musicSearchService.GetPlayUrlAsync(track.Id)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private int FindAudioTrackIndex(string sourceId, string filePath)
    {
        for (int i = 0; i < _audioService.Playlist.Count; i++)
        {
            var item = _audioService.Playlist[i];
            if (!string.IsNullOrWhiteSpace(sourceId) && item.SourceId == sourceId)
                return i;
            if (!string.IsNullOrWhiteSpace(filePath) && item.FilePath == filePath)
                return i;
        }

        return -1;
    }

    private static bool IsSameTrack(Track left, Track right)
    {
        if (!string.IsNullOrWhiteSpace(left.SourceId) && left.SourceId == right.SourceId)
            return true;
        if (!string.IsNullOrWhiteSpace(left.FilePath) && left.FilePath == right.FilePath)
            return true;
        return NormalizeForMusicCompare(left.Title) == NormalizeForMusicCompare(right.Title) &&
               (string.IsNullOrWhiteSpace(left.Artist) ||
                string.IsNullOrWhiteSpace(right.Artist) ||
                NormalizeForMusicCompare(left.Artist) == NormalizeForMusicCompare(right.Artist));
    }

    private static string MapExpression(string emotion) => emotion switch
    {
        "happy" => "smile",
        "sad" => "droopy",
        "angry" => "droopy",
        "surprised" => "smile",
        _ => "idle"
    };

    private static string MapMotion(string emotion) => emotion switch
    {
        "happy" => "wave",
        "surprised" => "wave",
        "sad" => "nod",
        "angry" => "nod",
        _ => "idle"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        _ttsSub.Dispose();
        _ttsCommandSub.Dispose();
        _ttsErrorSub.Dispose();
        _stateSub.Dispose();
        AppLanguage.Changed -= _onLanguageChanged;
        _statusAutoDismissSub?.Dispose();

        try { _waveIn?.StopRecording(); } catch (Exception ex) { Log.Debug(ex, "Failed to stop recording during shutdown"); }
        try { _waveWriter?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Failed to dispose WAV writer during shutdown"); }
        try { _waveIn?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Failed to dispose recording device during shutdown"); }
        _waveWriter = null;
        _waveIn = null;
        if (!string.IsNullOrWhiteSpace(_tempWavPath))
        {
            try { File.Delete(_tempWavPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete temp file"); }
        }
        _tempWavPath = null;
    }
}

public sealed record DjResponse(string DisplayText, string? Command, string Emotion);
