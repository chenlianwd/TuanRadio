using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Avalonia.Threading;
using Serilog;
using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly IMinimaxService _minimaxService;
    private readonly IDJService _djService;
    private readonly ISecureStorage _secureStorage;
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    [Reactive] public string ApiKey { get; set; } = string.Empty;
    [Reactive] public string DjName { get; set; } = "小音";
    [Reactive] public string DjDescription { get; set; } = "活泼开朗的电台主播";
    [Reactive] public string StatusMessage { get; set; } = string.Empty;
    [Reactive] public bool IsTesting { get; set; }

    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public SettingsViewModel(IMinimaxService minimaxService, IDJService djService, ISecureStorage secureStorage)
    {
        _minimaxService = minimaxService;
        _djService = djService;
        _secureStorage = secureStorage;

        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    public async Task LoadAsync()
    {
        try
        {
            var key = await _secureStorage.GetApiKeyAsync("minimax");
            if (!string.IsNullOrEmpty(key))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApiKey = key;
                });
                _minimaxService.SetApiKey(key);
            }

            string? djName = null, djDesc = null;
            if (File.Exists(SettingsFile))
            {
                var json = await File.ReadAllTextAsync(SettingsFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("dj_name", out var name))
                    djName = name.GetString();
                if (root.TryGetProperty("dj_description", out var desc))
                    djDesc = desc.GetString();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (djName != null) DjName = djName;
                if (djDesc != null) DjDescription = djDesc;
            });

            _djService.Initialize(new DJProfile { Name = DjName, Description = DjDescription });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
        }
    }

    private async Task TestConnectionAsync()
    {
        Log.Information("TestConnection clicked, ApiKey length={Len}", ApiKey?.Length ?? 0);
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "请先输入 API Key");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsTesting = true;
            StatusMessage = "正在测试连接...";
        });
        try
        {
            _minimaxService.SetApiKey(ApiKey);
            Log.Information("Calling Minimax API...");
            var result = await _minimaxService.ChatAsync("你好，请用一句话回复", new System.Collections.Generic.List<ChatMessage>());
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"连接成功：{result[..Math.Min(50, result.Length)]}...");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Minimax API error");
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"连接失败：{ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsTesting = false);
        }
    }

    private async Task SaveAsync()
    {
        Log.Information("Save clicked, DjName={Name}", DjName);
        var profile = new DJProfile
        {
            Name = DjName,
            Description = DjDescription
        };
        _djService.Initialize(profile);

        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _secureStorage.SaveApiKeyAsync("minimax", ApiKey);
                _minimaxService.SetApiKey(ApiKey);
            }

            Directory.CreateDirectory(SettingsDir);
            var settingsData = new
            {
                dj_name = DjName,
                dj_description = DjDescription
            };
            var json = JsonSerializer.Serialize(settingsData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFile, json);

            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "设置已保存");
            Log.Information("Settings saved to {Path}", SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"保存失败：{ex.Message}");
        }
    }
}
