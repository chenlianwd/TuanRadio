using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
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
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string SecMsGecVersion = $"1-{ChromiumFullVersion}";
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string VoiceListUrl = $"https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list?trustedclienttoken={TrustedClientToken}";
    private const string EdgeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    private static readonly Dictionary<string, string> LegacyVoiceMap = new()
    {
        ["female-shaonv"] = "zh-CN-XiaoxiaoNeural",
        ["female-yujie"] = "zh-CN-XiaoyiNeural",
        ["female-chengshu"] = "zh-CN-XiaoxiaoNeural",
        ["male-qn-qingse"] = "zh-CN-YunxiNeural",
        ["male-qn-jingying"] = "zh-CN-YunjianNeural",
        ["male-qn-badao"] = "zh-CN-YunyangNeural"
    };

    private readonly HttpClient _httpClient;
    private IReadOnlyList<VoiceOption>? _voiceCache;
    private readonly SemaphoreSlim _voiceCacheLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _disposed;

    // Persistent WebSocket connection for TTS synthesis
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsLock = new(1, 1);

    public EdgeTtsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<byte[]> SynthesizeAsync(string text, string voiceId, string emotion = "neutral")
        => SynthesizeAsync(text, voiceId, emotion, CancellationToken.None);

    public async Task<byte[]> SynthesizeAsync(
        string text,
        string voiceId,
        string emotion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref _disposed) != 0)
            return Array.Empty<byte>();

        var voice = ResolveVoice(voiceId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token,
            cancellationToken);

        try
        {
            var audio = await SynthesizeViaWebSocketAsync(text, voice, emotion, linkedCts.Token);
            // 空音频说明本次合成没有产出任何数据，按失败处理走重试，避免"合成成功但无声"
            if (audio.Length > 0)
                return audio;
            throw new InvalidOperationException("Edge TTS returned empty audio");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            throw;
        }
        catch (Exception firstAttempt)
        {
            // 服务端按"一连接一次合成"设计，复用连接可能在合成中途收到 Close；
            // 用全新连接重试一次
            Log.Warning(firstAttempt, "Edge TTS first attempt failed for voice {Voice}, retrying with fresh connection", voice);
            try
            {
                var audio = await SynthesizeViaWebSocketAsync(text, voice, emotion, linkedCts.Token);
                if (audio.Length > 0)
                    return audio;
                throw new InvalidOperationException("Edge TTS returned empty audio after retry");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Edge TTS synthesis failed for voice {Voice}", voice);
                throw;
            }
        }
    }

    public async Task<IReadOnlyList<VoiceOption>> GetVoicesAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Array.Empty<VoiceOption>();

        if (_voiceCache != null)
            return _voiceCache;

        var lockHeld = false;
        try
        {
            await _voiceCacheLock.WaitAsync(_lifetimeCts.Token);
            lockHeld = true;
            if (_voiceCache != null)
                return _voiceCache;

            var response = await _httpClient.GetStringAsync(VoiceListUrl, _lifetimeCts.Token);
            var voices = JsonSerializer.Deserialize<List<EdgeVoice>>(response) ?? new();

            _voiceCache = voices
                .Where(v => v.Locale?.StartsWith("zh-") == true ||
                            v.Locale == "en-US" || v.Locale == "en-GB")
                .Select(v => new VoiceOption
                {
                    Id = v.ShortName ?? "",
                    DisplayName = v.FriendlyName ?? v.ShortName ?? ""
                })
                .OrderBy(v => v.DisplayName)
                .ToList();

            return _voiceCache;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            return Array.Empty<VoiceOption>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch Edge TTS voice list");
            return new List<VoiceOption>
            {
                new() { Id = "zh-CN-XiaoxiaoNeural", DisplayName = AppLanguage.T("晓晓（女声）", "Xiaoxiao (female)") },
                new() { Id = "zh-CN-YunxiNeural", DisplayName = AppLanguage.T("云希（男声）", "Yunxi (male)") },
                new() { Id = "zh-CN-YunjianNeural", DisplayName = AppLanguage.T("云健（男声）", "Yunjian (male)") }
            };
        }
        finally
        {
            if (lockHeld)
                _voiceCacheLock.Release();
        }
    }

    private async Task<ClientWebSocket> ConnectWebSocketAsync(CancellationToken ct)
    {
        // 服务端按“一连接一次合成”设计：复用旧连接几乎必然在合成中途收到 Close，
        // 每次合成都用全新连接，反而省掉一轮“失败→重连重试”的往返
        _ws?.Dispose();
        _ws = null;

        var connectionId = Guid.NewGuid().ToString("N");
        var secMsGec = GenerateSecMsGec(DateTimeOffset.UtcNow);
        var url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}" +
                  $"&Sec-MS-GEC={secMsGec}" +
                  $"&Sec-MS-GEC-Version={SecMsGecVersion}" +
                  $"&ConnectionId={connectionId}";
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent", EdgeUserAgent);
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={Guid.NewGuid().ToString("N").ToUpperInvariant()};");
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
        }
        catch
        {
            // 连接失败必须释放局部实例：此时还未赋给 _ws，外层 catch 只会清理旧值
            ws.Dispose();
            throw;
        }
        _ws = ws;
        return ws;
    }

    private async Task<byte[]> SynthesizeViaWebSocketAsync(
        string text,
        string voice,
        string emotion,
        CancellationToken cancellationToken)
    {
        await _wsLock.WaitAsync(cancellationToken);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            var ws = await ConnectWebSocketAsync(timeoutCts.Token);

            // Send speech config
            var configMsg = BuildConfigMessage();
            await SendWebSocketMessageAsync(ws, configMsg, timeoutCts.Token);

            // Send SSML
            var ssml = BuildSsml(text, voice, emotion);
            var ssmlMsg = BuildSsmlMessage(ssml);
            await SendWebSocketMessageAsync(ws, ssmlMsg, timeoutCts.Token);

            // Read audio data
            using var audioStream = new MemoryStream();
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open)
            {
                using var msgBuffer = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // 未收到 turn.end 就收到 Close = 本次合成被服务端中断，
                        // 返回部分音频会被上层当作成功（表现为无声），必须按失败抛出
                        throw new InvalidOperationException("Edge TTS connection closed before synthesis completed");
                    }

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

            var audio = audioStream.ToArray();
            // 一连接一次合成：本轮结束即弃连接，下次合成重新握手
            _ws?.Dispose();
            _ws = null;
            return audio;
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

    internal static string BuildConfigMessage()
    {
        var timestamp = BuildTimestamp();
        var json = "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";
        return $"X-Timestamp:{timestamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n{json}";
    }

    internal static string BuildSsml(string text, string voice, string? emotion = null)
    {
        // Escape XML special characters
        var escaped = text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

        var (pitch, rate, volume) = ResolveProsody(emotion);

        // xml:lang 跟随音色 locale（如 en-US-AriaNeural → en-US），英文音色不再声明成 zh-CN
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{ResolveLocale(voice)}'>" +
               $"<voice name='{voice}'>" +
               $"<prosody pitch='{pitch}' rate='{rate}' volume='{volume}'>" +
               $"{escaped}" +
               "</prosody></voice></speak>";
    }

    internal static string ResolveLocale(string voice)
    {
        // 音色名形如 "zh-CN-XiaoxiaoNeural" / "en-US-AriaNeural"，前两段即 locale
        var parts = voice.Split('-');
        if (parts.Length >= 2 &&
            parts[0].Length == 2 &&
            parts[1].Length is 2 or 3 or 4 &&
            parts[0].All(char.IsLetter) &&
            parts[1].All(char.IsLetter))
            return $"{parts[0]}-{parts[1]}";
        return "zh-CN";
    }

    internal static (string Pitch, string Rate, string Volume) ResolveProsody(string? emotion)
        => emotion?.Trim().ToLowerInvariant() switch
        {
            "happy" => ("+2Hz", "+8%", "+0%"),
            "sad" => ("-2Hz", "-8%", "-5%"),
            "calm" => ("-1Hz", "-12%", "-5%"),
            "angry" => ("+3Hz", "+12%", "+3%"),
            "surprised" => ("+4Hz", "+10%", "+2%"),
            _ => ("+0Hz", "+0%", "+0%")
        };

    internal static string ResolveVoice(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return "zh-CN-XiaoxiaoNeural";

        return LegacyVoiceMap.GetValueOrDefault(voiceId, voiceId);
    }

    internal static string GenerateSecMsGec(DateTimeOffset utcNow)
    {
        const long windowsEpochSeconds = 11_644_473_600;
        var seconds = utcNow.ToUnixTimeSeconds() + windowsEpochSeconds;
        seconds -= seconds % 300;
        var ticks = checked(seconds * 10_000_000);
        var input = ticks.ToString(CultureInfo.InvariantCulture) + TrustedClientToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(input)));
    }

    private static string BuildSsmlMessage(string ssml)
    {
        var requestId = Guid.NewGuid().ToString("N");
        return $"X-RequestId:{requestId}\r\n" +
               $"Content-Type:application/ssml+xml\r\n" +
               $"X-Timestamp:{BuildTimestamp()}Z\r\n" +
               $"Path:ssml\r\n\r\n" +
               ssml;
    }

    private static string BuildTimestamp()
        => DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            CultureInfo.InvariantCulture);

    private static async Task SendWebSocketMessageAsync(ClientWebSocket ws, string message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCts.Cancel();
        // Cancel current Receive/Send first. Do not dispose the semaphores while an
        // in-flight synthesis may still execute its finally/Release.
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    private class EdgeVoice
    {
        public string? ShortName { get; set; }
        public string? FriendlyName { get; set; }
        public string? Locale { get; set; }
    }
}
