using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.ViewModels;
using Serilog;

namespace AIRadio.Desktop.Services;

/// <summary>
/// Edge TTS 语音合成服务，使用微软免费 TTS 服务。
/// 通过 WebSocket 连接 speech.platform.bing.com 获取语音。
/// </summary>
public class EdgeTtsService : ITtsService, IDisposable
{
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4&ConnectionId={0}";
    private const string VoiceListUrl = "https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list?trustedclienttoken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    private static readonly Dictionary<string, string> EmotionToStyle = new()
    {
        ["happy"] = "cheerful",
        ["sad"] = "sad",
        ["calm"] = "gentle",
        ["angry"] = "angry",
        ["neutral"] = "chat",
        ["surprised"] = "excited",
        ["affectionate"] = "affectionate",
        ["lyrical"] = "lyrical",
        ["embarrassed"] = "embarrassed",
        ["depressed"] = "depressed",
        ["envious"] = "envious",
        ["fearful"] = "fearful",
        ["gentle"] = "gentle",
        ["serious"] = "serious"
    };

    private readonly HttpClient _httpClient;
    private IReadOnlyList<VoiceOption>? _voiceCache;
    private readonly SemaphoreSlim _voiceCacheLock = new(1, 1);

    // Persistent WebSocket connection for TTS synthesis
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsLock = new(1, 1);
    private DateTime _wsCreatedAt = DateTime.MinValue;
    private static readonly TimeSpan WsMaxAge = TimeSpan.FromMinutes(5);

    public EdgeTtsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voiceId, string emotion = "neutral")
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        var voice = string.IsNullOrWhiteSpace(voiceId) ? "zh-CN-XiaoxiaoNeural" : voiceId;
        var style = EmotionToStyle.TryGetValue(emotion, out var s) ? s : "chat";

        try
        {
            return await SynthesizeViaWebSocketAsync(text, voice, style);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Edge TTS synthesis failed for voice {Voice}", voice);
            throw;
        }
    }

    public async Task<IReadOnlyList<VoiceOption>> GetVoicesAsync()
    {
        if (_voiceCache != null)
            return _voiceCache;

        await _voiceCacheLock.WaitAsync();
        try
        {
            if (_voiceCache != null)
                return _voiceCache;

            var response = await _httpClient.GetStringAsync(VoiceListUrl);
            var voices = JsonSerializer.Deserialize<List<EdgeVoice>>(response) ?? new();

            _voiceCache = voices
                .Where(v => v.Locale?.StartsWith("zh-") == true)
                .Select(v => new VoiceOption
                {
                    Id = v.ShortName ?? "",
                    DisplayName = v.FriendlyName ?? v.ShortName ?? ""
                })
                .OrderBy(v => v.DisplayName)
                .ToList();

            return _voiceCache;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch Edge TTS voice list");
            return new List<VoiceOption>
            {
                new() { Id = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓（女声）" },
                new() { Id = "zh-CN-YunxiNeural", DisplayName = "云希（男声）" },
                new() { Id = "zh-CN-YunjianNeural", DisplayName = "云健（男声）" }
            };
        }
        finally
        {
            _voiceCacheLock.Release();
        }
    }

    private async Task<ClientWebSocket> GetOrCreateWebSocketAsync(CancellationToken ct)
    {
        if (_ws != null && _ws.State == WebSocketState.Open &&
            DateTime.UtcNow - _wsCreatedAt < WsMaxAge)
            return _ws;

        _ws?.Dispose();
        var connectionId = Guid.NewGuid().ToString("N");
        var url = string.Format(WssUrl, connectionId);
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        await ws.ConnectAsync(new Uri(url), ct);
        _ws = ws;
        _wsCreatedAt = DateTime.UtcNow;
        return ws;
    }

    private async Task<byte[]> SynthesizeViaWebSocketAsync(string text, string voice, string style)
    {
        await _wsLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ws = await GetOrCreateWebSocketAsync(cts.Token);

            // Send speech config
            var configMsg = BuildConfigMessage(voice, style);
            await SendWebSocketMessageAsync(ws, configMsg, cts.Token);

            // Send SSML
            var ssml = BuildSsml(text, voice, style);
            var ssmlMsg = BuildSsmlMessage(ssml);
            await SendWebSocketMessageAsync(ws, ssmlMsg, cts.Token);

            // Read audio data
            using var audioStream = new MemoryStream();
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open)
            {
                using var msgBuffer = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        goto done;

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        msgBuffer.Write(buffer, 0, result.Count);
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var textMsg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (textMsg.Contains("turn.end"))
                            goto turnEnd;
                    }
                } while (!result.EndOfMessage);

                var msgBytes = msgBuffer.ToArray();
                if (msgBytes.Length > 2)
                {
                    var headerLen = (msgBytes[0] << 8) | msgBytes[1];
                    if (msgBytes.Length > headerLen + 2)
                        audioStream.Write(msgBytes, headerLen + 2, msgBytes.Length - headerLen - 2);
                }
                continue;

                turnEnd:
                // Consume remaining frames for this turn
                break;
            }
            done:
            return audioStream.ToArray();
        }
        catch (Exception)
        {
            // Connection broken — dispose so next call creates a new one
            _ws?.Dispose();
            _ws = null;
            throw;
        }
        finally
        {
            _wsLock.Release();
        }
    }

    private static string BuildConfigMessage(string voice, string style)
    {
        var timestamp = DateTime.UtcNow.ToString("R");
        var json = "{\"context\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}";
        return $"X-Timestamp:{timestamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n{json}";
    }

    private static string BuildSsml(string text, string voice, string style)
    {
        // Escape XML special characters
        var escaped = text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>" +
               $"<voice name='{voice}'>" +
               $"<mstts:express-as style='{style}'>" +
               $"{escaped}" +
               $"</mstts:express-as></voice></speak>";
    }

    private static string BuildSsmlMessage(string ssml)
    {
        var requestId = Guid.NewGuid().ToString("N");
        return $"X-RequestId:{requestId}\r\n" +
               $"Content-Type:application/ssml+xml\r\n" +
               $"X-Timestamp:{DateTime.UtcNow:R}\r\n" +
               $"Path:ssml\r\n\r\n" +
               ssml;
    }

    private static async Task SendWebSocketMessageAsync(ClientWebSocket ws, string message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        _ws?.Dispose();
        _wsLock.Dispose();
        _voiceCacheLock.Dispose();
    }

    private class EdgeVoice
    {
        public string? ShortName { get; set; }
        public string? FriendlyName { get; set; }
        public string? Locale { get; set; }
    }
}
