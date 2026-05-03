using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly IDJService _djService;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [Reactive] public string InputText { get; set; } = string.Empty;
    [Reactive] public bool IsProcessing { get; set; }
    [Reactive] public string DjEmotion { get; set; } = "neutral";

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }

    public ChatViewModel(IDJService djService)
    {
        _djService = djService;

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
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = response
            });
            DjEmotion = _djService.CurrentEmotion;
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
}
