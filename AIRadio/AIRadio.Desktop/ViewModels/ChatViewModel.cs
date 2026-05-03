using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly IDJService _djService;
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [Reactive] public string InputText { get; set; } = string.Empty;
    [Reactive] public bool IsProcessing { get; set; }
    [Reactive] public string DjEmotion { get; set; } = "neutral";

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }

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
        // Match 【play:xxx】 or 【next】 etc at the end of the response
        var match = Regex.Match(response, @"【(play:.+?|next|pause|resume)】\s*$");
        if (!match.Success)
            return (response, null);

        var displayText = response[..match.Index].TrimEnd('\n', '\r', ' ');
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

        var t = track.ToTrack(url);
        _audioService.AddTracks(new[] { t });
        var index = _audioService.Playlist.Count - 1;
        _audioService.PlayAtIndex(index);
    }
}
