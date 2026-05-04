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

public class ChatViewModel : ViewModelBase
{
    private readonly IDJService _djService;
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly ISttService _sttService;
    private readonly IDisposable _stateSub;

    private WaveInEvent? _waveIn;
    private string? _tempWavPath;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [Reactive] public string InputText { get; set; } = string.Empty;
    [Reactive] public bool IsProcessing { get; set; }
    [Reactive] public bool IsListening { get; set; }
    [Reactive] public bool IsConversationMode { get; set; }
    [Reactive] public string DjEmotion { get; set; } = "neutral";

    public event Action<string, string>? Live2DCommand; // expression, motion

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVoiceInputCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConversationModeCommand { get; }

    public ChatViewModel(IDJService djService, IAudioService audioService, IMusicSearchService musicSearchService, ISttService sttService)
    {
        _djService = djService;
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _sttService = sttService;

        SendMessageCommand = ReactiveCommand.CreateFromTask(
            SendMessageAsync,
            this.WhenAnyValue(x => x.IsProcessing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(p => !p));

        ToggleVoiceInputCommand = ReactiveCommand.Create(ToggleVoiceInput);
        ToggleConversationModeCommand = ReactiveCommand.Create(ToggleConversationMode);

        // Listen for TTS completion in conversation mode
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
            Log.Information("Mic recording started");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start mic recording");
            IsListening = false;
        }
    }

    private void StopListening()
    {
        try
        {
            _waveIn?.StopRecording();
            IsListening = false;
            Log.Information("Mic recording stopped");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping mic");
            IsListening = false;
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

                // Auto-send in conversation mode
                if (IsConversationMode)
                {
                    await SendMessageAsync();
                }
            }
            else
            {
                Log.Warning("No speech recognized");
                if (IsConversationMode)
                    StartListening();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Speech recognition failed");
        }
        finally
        {
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

        try
        {
            var response = await _djService.GenerateChatResponseAsync(text);
            var (displayText, command) = ParseResponse(response);
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = displayText
            });
            DjEmotion = _djService.CurrentEmotion;
            Live2DCommand?.Invoke(MapExpression(DjEmotion), MapMotion(DjEmotion));

            if (command != null)
                await ExecuteCommandAsync(command);

            // TTS: generate and play speech (strip emoji so they aren't read aloud)
            if (_djService.TtsEnabled)
            {
                var ttsText = StripEmoji(displayText);
                if (!string.IsNullOrWhiteSpace(ttsText))
                {
                    var speechData = await _djService.GenerateSpeechAsync(ttsText);
                    if (speechData is { Length: > 0 })
                        _audioService.PlayTtsAudio(speechData);
                }
            }
        }
        catch
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = "网络有点问题，稍后再聊吧~"
            });
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private static (string displayText, string? command) ParseResponse(string response)
    {
        // Strip emotion tag like [happy] [neutral] etc - anywhere in response
        var displayText = Regex.Replace(response, @"\[(happy|sad|calm|neutral|angry|surprised)\]", "", RegexOptions.IgnoreCase);

        // Match 【play:xxx】 or 【next】 etc at the end
        var match = Regex.Match(displayText, @"【(play:.+?|next|pause|resume)】\s*$");
        if (!match.Success)
            return (displayText, null);

        displayText = displayText[..match.Index].TrimEnd('\n', '\r', ' ');
        var command = match.Groups[1].Value;
        return (displayText, command);
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
        Log.Information("DJ play request: {Query}", query);

        var results = await _musicSearchService.SearchAsync(query, 5);
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
        var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
        if (url == null)
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = "这首歌暂时无法播放，换一首吧？"
            });
            return;
        }

        // Check if already in playlist
        for (int i = 0; i < _audioService.Playlist.Count; i++)
        {
            if (_audioService.Playlist[i].FilePath == url)
            {
                _audioService.PlayAtIndex(i);
                return;
            }
        }

        var t = track.ToTrack(url);
        _audioService.AddTracks(new[] { t });
        var index = _audioService.Playlist.Count - 1;
        _audioService.PlayAtIndex(index);
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
}
