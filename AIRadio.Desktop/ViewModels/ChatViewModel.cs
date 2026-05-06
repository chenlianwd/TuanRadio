using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
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
    private readonly Action<Track>? _trackAdded;
    private readonly IDisposable _ttsSub;
    private readonly IDisposable _ttsErrorSub;
    private readonly IDisposable _stateSub;
    private string? _pendingCommand;

    private WaveInEvent? _waveIn;
    private string? _tempWavPath;
    private bool _isPlayingSong;
    private bool _sendAfterHoldToTalk;
    private bool _hasFailureNotice;
    private string _failureStatusText = "AI ERROR";

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
    [Reactive] public string MicButtonText { get; set; } = "HOLD";

    public event Action<string, string>? Live2DCommand; // expression, motion

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVoiceInputCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConversationModeCommand { get; }

    public ChatViewModel(IDJService djService, IAudioService audioService, IMusicSearchService musicSearchService, ISttService sttService, Action<Track>? trackAdded = null)
    {
        _djService = djService;
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _sttService = sttService;
        _trackAdded = trackAdded;

        SendMessageCommand = ReactiveCommand.CreateFromTask(
            SendMessageAsync,
            this.WhenAnyValue(x => x.IsProcessing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(p => !p));

        ToggleVoiceInputCommand = ReactiveCommand.Create(ToggleVoiceInput);
        ToggleConversationModeCommand = ReactiveCommand.Create(ToggleConversationMode);

        // Listen for TTS completion to play song AFTER TTS finishes
        _ttsSub = _audioService.TtsStateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async playing =>
            {
                IsSpeaking = playing;
                RefreshStatus();

                if (!playing && _pendingCommand != null)
                {
                    var cmd = _pendingCommand;
                    _pendingCommand = null;
                    await ExecuteCommandAsync(cmd);
                }
            });

        _ttsErrorSub = _audioService.TtsError
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(message => SetFailureNotice(new ApiFailureInfo(
                ApiFailureKind.InvalidResponse,
                "语音播放失败",
                message,
                "可以在设置里暂时关闭语音播报，或检查系统默认音频输出设备。")));

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
        try
        {
            _tempWavPath = Path.Combine(Path.GetTempPath(), $"stt_{Guid.NewGuid():N}.wav");

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1)
            };
            var writer = new WaveFileWriter(_tempWavPath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += (_, e) =>
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _waveIn.RecordingStopped += (_, _) =>
            {
                writer.Dispose();
                _waveIn?.Dispose();
                _waveIn = null;
                RecognizeFromWav(_tempWavPath);
            };

            _waveIn.StartRecording();
            IsListening = true;
            IsRecognizing = false;
            MicButtonText = "HOLD";
            RefreshStatus();
            Log.Information("Mic recording started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start mic recording");
            IsListening = false;
            IsRecognizing = false;
            MicButtonText = "HOLD";
            StatusText = "MIC ERROR";
        }
    }

    private void StopListening()
    {
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

    private async void RecognizeFromWav(string wavPath)
    {
        try
        {
            var text = await _sttService.TranscribeAsync(wavPath);

            if (!string.IsNullOrWhiteSpace(text))
            {
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    InputText = text;
                });
                Log.Information("Speech recognized: {Text}", text);

                // Hold-to-talk should feel like talking to the DJ directly.
                if (IsConversationMode || _sendAfterHoldToTalk)
                {
                    await SendMessageAsync();
                }
            }
            else
            {
                Log.Warning("No speech recognized");
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() => StatusText = "NO SPEECH");
                if (IsConversationMode)
                    StartListening();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Speech recognition failed");
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() => StatusText = "STT ERROR");
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                IsRecognizing = false;
                MicButtonText = "HOLD";
                RefreshStatus();
            });
            _sendAfterHoldToTalk = false;
            try { File.Delete(wavPath); } catch { }
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
        SetWorkingNotice("AI 正在回复", "正在请求 AI 服务，最多等待 30 秒。");
        _pendingCommand = null;
        _audioService.StopTts();

        try
        {
            if (TryParseSongRequest(text, out var songQuery, out var requiresConfidentMatch) &&
                (!requiresConfidentMatch || await HasConfidentSongMatchAsync(songQuery)))
            {
                await RespondWithCommandAsync($"好，我来找《{songQuery}》。", $"play:{songQuery}", "happy");
                return;
            }

            var response = await _djService.GenerateChatResponseAsync(text);
            if (_djService.LastFailure is { } chatFailure)
            {
                AddFailureMessage("AI 回复失败", chatFailure);
                SetFailureNotice(chatFailure);
                return;
            }

            var (displayText, command) = ParseResponse(response);
            await RespondWithCommandAsync(displayText, command, _djService.CurrentEmotion);
        }
        catch (Exception ex)
        {
            var failure = ApiFailureInfo.FromException(ex);
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = $"AI 回复失败：{failure.Title}。{failure.RecoveryHint}"
            });
            SetFailureNotice(failure);
        }
        finally
        {
            IsProcessing = false;
            RefreshStatus();
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
        Live2DCommand?.Invoke(MapExpression(DjEmotion), MapMotion(DjEmotion));

        if (command != null && !_djService.TtsEnabled)
            await ExecuteCommandAsync(command);
        else if (command != null && _djService.TtsEnabled)
            _pendingCommand = command;

        var ttsText = StripEmoji(displayText);
        if (_djService.TtsEnabled && !string.IsNullOrWhiteSpace(ttsText))
        {
            StatusText = "VOICE...";
            SetWorkingNotice("正在生成语音", "AI 文字已返回，正在调用语音服务。");
            var speechData = await _djService.GenerateSpeechAsync(ttsText);
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
                        "语音生成失败",
                        "语音服务没有返回可播放的音频数据。",
                        "检查 API Key、账号权限和 TTS 额度后重试。"));
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
        SetWorkingNotice("正在生成语音", "正在调用语音服务。");
        var speechData = await _djService.GenerateSpeechAsync(ttsText);
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
                    "语音生成失败",
                    "语音服务没有返回可播放的音频数据。",
                    "检查 API Key、账号权限和 TTS 额度后重试。"));
            Log.Warning("TTS returned empty audio");
        }
    }

    private void RefreshStatus()
    {
        if (_hasFailureNotice && !IsProcessing && !IsSpeaking && !IsRecognizing && !IsListening)
        {
            StatusText = _failureStatusText;
            ShowStatusNotice = true;
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
            SetWorkingNotice("AI 正在回复", "正在请求 AI 服务，最多等待 30 秒。");
        }
        else if (IsSpeaking)
        {
            SetWorkingNotice("正在播报语音", "AI 回复已生成，正在播放 TTS 音频。");
        }
        else
        {
            ShowStatusNotice = false;
        }
    }

    private void SetWorkingNotice(string headline, string detail)
    {
        _hasFailureNotice = false;
        StatusHeadline = headline;
        StatusDetail = detail;
        StatusRecoveryHint = string.Empty;
        ShowStatusNotice = true;
    }

    private void SetFailureNotice(ApiFailureInfo failure)
    {
        _hasFailureNotice = true;
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
    }

    private void AddFailureMessage(string prefix, ApiFailureInfo failure)
    {
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = $"{prefix}：{failure.Title}。{failure.RecoveryHint}"
        });
    }

    private static (string displayText, string? command) ParseResponse(string response)
    {
        var displayText = Regex.Replace(response, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);

        var match = Regex.Match(displayText, @"【(play:.+?|next|pause|resume)】\s*$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return (displayText.Trim(), null);

        displayText = displayText[..match.Index].TrimEnd('\n', '\r', ' ');
        var command = match.Groups[1].Value;
        return (displayText, command);
    }

    private async Task<bool> HasConfidentSongMatchAsync(string query)
    {
        try
        {
            var results = await _musicSearchService.SearchAsync(query, 3);
            return results.Count > 0 && IsConfidentMusicMatch(query, results[0]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to preflight song request: {Query}", query);
            return false;
        }
    }

    private static bool TryParseSongRequest(string text, out string query, out bool requiresConfidentMatch)
    {
        query = string.Empty;
        requiresConfidentMatch = false;
        var normalized = NormalizeSongQuery(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var explicitMatch = Regex.Match(
            normalized,
            @"^(?:请|麻烦|帮我|给我)?\s*(?:播放|放一下|放下|放|听一下|听下|听|来一首|来首|点一首|点首|我想听|想听)\s*(?:一首|首)?\s*(?<query>.+)$",
            RegexOptions.IgnoreCase);
        if (explicitMatch.Success)
        {
            query = NormalizeSongQuery(explicitMatch.Groups["query"].Value);
            return !string.IsNullOrWhiteSpace(query) && !IsGenericMusicRequest(query);
        }

        if (!LooksLikeBareSongTitle(normalized))
            return false;

        query = normalized;
        requiresConfidentMatch = true;
        return true;
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
            if (command.StartsWith("play:"))
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
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to execute DJ command: {Command}", command);
        }
    }

    private async Task PlaySongAsync(string query)
    {
        if (_isPlayingSong) return;
        _isPlayingSong = true;
        try
        {
            Log.Information("DJ play request: {Query}", query);

            var results = await _musicSearchService.SearchAsync(query, 5);
            Log.Debug("DJ search returned {Count} results", results.Count);
            if (results.Count == 0)
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = "没找到这首歌，换个关键词试试？"
                });
                return;
            }

            var track = results[0];
            Log.Debug("DJ got track: {Track}, fetching URL...", track.Title);
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            Log.Debug("DJ got URL: {Url}", url != null ? "present" : "null");
            if (url == null)
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = "这首歌暂时无法播放，换一首吧？"
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
        catch (Exception ex)
        {
            Log.Error(ex, "PlaySongAsync failed for query: {Query}", query);
        }
        finally
        {
            _isPlayingSong = false;
        }
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
        _ttsSub.Dispose();
        _ttsErrorSub.Dispose();
        _stateSub.Dispose();
        _waveIn?.Dispose();
        if (!string.IsNullOrWhiteSpace(_tempWavPath))
        {
            try { File.Delete(_tempWavPath); } catch { }
        }
    }
}
