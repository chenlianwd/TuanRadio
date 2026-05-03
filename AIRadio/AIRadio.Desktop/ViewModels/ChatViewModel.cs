using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Speech.Recognition;
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

    private WaveInEvent? _waveIn;
    private string? _tempWavPath;
    private SpeechRecognitionEngine? _recognizer;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [Reactive] public string InputText { get; set; } = string.Empty;
    [Reactive] public bool IsProcessing { get; set; }
    [Reactive] public bool IsListening { get; set; }
    [Reactive] public string DjEmotion { get; set; } = "neutral";

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVoiceInputCommand { get; }

    public ChatViewModel(IDJService djService, IAudioService audioService, IMusicSearchService musicSearchService)
    {
        _djService = djService;
        _audioService = audioService;
        _musicSearchService = musicSearchService;

        SendMessageCommand = ReactiveCommand.CreateFromTask(
            SendMessageAsync,
            this.WhenAnyValue(x => x.IsProcessing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Select(p => !p));

        ToggleVoiceInputCommand = ReactiveCommand.Create(ToggleVoiceInput);
    }

    private void ToggleVoiceInput()
    {
        if (IsListening)
            StopListening();
        else
            StartListening();
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
            _recognizer = new SpeechRecognitionEngine();
            _recognizer.LoadGrammar(new DictationGrammar());

            _recognizer.SetInputToWaveFile(wavPath);
            var result = _recognizer.Recognize();

            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    InputText = result.Text;
                });
                Log.Information("Speech recognized: {Text}", result.Text);
            }
            else
            {
                Log.Warning("No speech recognized");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Speech recognition failed");
        }
        finally
        {
            _recognizer?.Dispose();
            _recognizer = null;
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

            if (command != null)
                await ExecuteCommandAsync(command);

            // TTS: generate and play speech
            if (_djService.TtsEnabled)
            {
                var speechData = await _djService.GenerateSpeechAsync(displayText);
                if (speechData is { Length: > 0 })
                    _audioService.PlayTtsAudio(speechData);
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
        // Strip emotion tag like [happy] [neutral] etc
        var displayText = Regex.Replace(response, @"\s*\[(happy|sad|calm|neutral|angry|surprised)\]\s*$", "").TrimEnd();

        // Match 【play:xxx】 or 【next】 etc at the end
        var match = Regex.Match(displayText, @"【(play:.+?|next|pause|resume)】\s*$");
        if (!match.Success)
            return (displayText, null);

        displayText = displayText[..match.Index].TrimEnd('\n', '\r', ' ');
        var command = match.Groups[1].Value;
        return (displayText, command);
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
}
