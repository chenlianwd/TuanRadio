using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
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
    private const string SecMsGecVersion = $"2-{ChromiumFullVersion}";
    /// <summary>单次 SynthesizeAsync（首试+重试）的总时限，略高于单次 60s 内部超时。</summary>
    private static readonly TimeSpan TotalSynthesisTimeout = TimeSpan.FromSeconds(75);
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string VoiceListUrl = $"https://speech.platform.bing.com/consumer/speech/synthesize/readaloud/voices/list?trustedclienttoken={TrustedClientToken}";
    private const string EdgeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    // 旧 MiniMax 音色 ID → Edge 免费端点音色的近似映射。免费池普通话仅 6 条
    // （女声晓晓/晓伊、男声云希/云夏/云健/云扬），按听感对齐标签：
    // 云扬沉稳专业配"精英"、云健激情有力配"霸道"；女声池没有第三条声线，
    // 「成熟女声」与「知性女声」共用晓伊，靠 VoiceBaselineProsody 基线韵律区分
    private static readonly Dictionary<string, string> LegacyVoiceMap = new()
    {
        ["female-shaonv"] = "zh-CN-XiaoxiaoNeural",
        ["female-yujie"] = "zh-CN-XiaoyiNeural",
        ["female-chengshu"] = "zh-CN-XiaoyiNeural",
        ["male-qn-qingse"] = "zh-CN-YunxiNeural",
        ["male-qn-jingying"] = "zh-CN-YunyangNeural",
        ["male-qn-badao"] = "zh-CN-YunjianNeural"
    };

    // 音色级基线韵律（与情绪韵律数值叠加）：免费池没有低沉女声，
    // 「成熟女声」在晓伊上整体压低放缓，与「知性女声」的原生晓伊拉开听感
    private static readonly Dictionary<string, (int PitchHz, int RatePct)> VoiceBaselineProsody = new()
    {
        ["female-chengshu"] = (-3, -5)
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
        // 总时限：首试+重试各 60s 内部超时，无总限时调用方最长可见约 120s，
        // 期间 _wsLock 被占，后续所有 TTS 排队
        linkedCts.CancelAfter(TotalSynthesisTimeout);

        try
        {
            var audio = await SynthesizeViaWebSocketAsync(text, voice, emotion, voiceId, linkedCts.Token);
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
                var audio = await SynthesizeViaWebSocketAsync(text, voice, emotion, voiceId, linkedCts.Token);
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
        string? sourceVoiceId,
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
            var ssml = BuildSsml(text, voice, emotion, sourceVoiceId);
            var ssmlMsg = BuildSsmlMessage(ssml);
            await SendWebSocketMessageAsync(ws, ssmlMsg, timeoutCts.Token);

            // Read audio data
            using var audioStream = new MemoryStream();
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open)
            {
                using var msgBuffer = new MemoryStream();
                using var textBuffer = new MemoryStream();
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
                        // 文本消息同样要累积到 EndOfMessage：WebSocket 允许把一条消息切成
                        // 多帧，"turn.end" 跨帧切断时逐分片 Contains 会漏检，该句合成
                        // 挂满 60s 超时再重试一次（最长约 2 分钟占住合成锁）
                        textBuffer.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var textMsg = Encoding.UTF8.GetString(textBuffer.ToArray());
                    if (textMsg.Contains("turn.end"))
                        goto turnEnd;
                    continue;
                }

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
            // 一连接一次合成：本轮结束即弃连接，下次合成重新握手。
            // 先做正常关闭握手（失败再强弃）：高频 abortive close 会被服务端
            // 记为异常断连，抬高被限流/握手失败的概率
            try
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "turn.end", closeCts.Token);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Edge TTS graceful close failed; disposing connection");
            }
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

    internal static string BuildSsml(string text, string voice, string? emotion = null, string? sourceVoiceId = null)
    {
        // Escape XML special characters
        var escaped = EscapeXml(text);
        // voice 名进入单引号属性：与正文一致转义，防含引号/尖括号的值破坏 SSML 结构
        var voiceAttr = EscapeXml(voice);

        // 情绪与音色基线按数值叠加后格式化，避免解析拼合已格式化的字符串
        var (pitchHz, ratePct, volumePct) = ResolveEmotionProsody(emotion);
        var (basePitchHz, baseRatePct) = ResolveVoiceBaseline(sourceVoiceId);

        // xml:lang 跟随音色 locale（如 en-US-AriaNeural → en-US），英文音色不再声明成 zh-CN
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{ResolveLocale(voice)}'>" +
               $"<voice name='{voiceAttr}'>" +
               $"<prosody pitch='{FormatSigned(basePitchHz + pitchHz)}Hz' rate='{FormatSigned(baseRatePct + ratePct)}%' volume='{FormatSigned(volumePct)}%'>" +
               $"{escaped}" +
               "</prosody></voice></speak>";
    }

    private static string FormatSigned(int value)
        => value >= 0 ? $"+{value}" : value.ToString();

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

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

    internal static (int PitchHz, int RatePct, int VolumePct) ResolveEmotionProsody(string? emotion)
        => emotion?.Trim().ToLowerInvariant() switch
        {
            "happy" => (2, 8, 0),
            "sad" => (-2, -8, -5),
            "calm" => (-1, -12, -5),
            "angry" => (3, 12, 3),
            "surprised" => (4, 10, 2),
            _ => (0, 0, 0)
        };

    internal static (int PitchHz, int RatePct) ResolveVoiceBaseline(string? voiceId)
        => voiceId != null && VoiceBaselineProsody.TryGetValue(voiceId, out var baseline)
            ? baseline
            : (0, 0);

    internal static string ResolveVoice(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return "zh-CN-XiaoxiaoNeural";

        return LegacyVoiceMap.GetValueOrDefault(voiceId, voiceId);
    }

    // ---- Sec-MS-GEC v2 令牌 ----
    // 微软已将 Edge Read Aloud 的防滥用令牌从 v1（纯 SHA256）升级为 v2 并作废 v1
    // （握手一律 403）。算法移植自 edge-tts（GPL-3.0）src/edge_tts/drm.py 的
    // generate_sec_ms_gec_v2：对 "ticks(300s 窗口)+TrustedClientToken" 串先算改造版
    // SHA-256（自定义初始状态/调度表，首轮 K[14] 被替换），再算自定义 CS64 摘要，
    // 输出 "CS64(16hex大写)-SHA256(64hex大写)"。
    // 原实现用任意精度整数做"移位不截断、异或后再相加"的混淆运算；由于加减进位只
    // 向上传播、异或按位独立，所有中间量在 32 位截断处与标准 uint 环绕运算逐位一致
    // （由锚定上游实现的向量测试保证）。
    private static readonly uint[] SecMsGecSha256Initial =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    };
    private static readonly uint[] SecMsGecSha256State =
    {
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A,
        0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    };
    private static readonly uint[] SecMsGecSha256Schedule =
    {
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA,
        0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6,
        0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33,
        0xD258B060, 0x72766564,
    };
    private static readonly uint[] SecMsGecSha256RoundConstants =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5,
        0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3,
        0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC,
        0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7,
        0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13,
        0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3,
        0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5,
        0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208,
        0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };
    // K[14] 位置被 SecMsGecSha256Adjustment 替换（其余与标准轮常数相同）
    private static readonly uint[] SecMsGecFirstBlockConstants =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5,
        0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3,
        0x72BE5D74, 0x80DEB1FE, 0xC9DC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC,
        0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7,
        0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13,
        0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3,
        0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5,
        0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208,
        0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };
    private const uint SecMsGecSha256Adjustment = 0xC9DC06A7;
    private const uint SecMsGecSigma0Adjustment = 0x059C0B80;
    private const uint SecMsGecSigma1Adjustment = 0x000B92C0;
    private static readonly uint[] SecMsGecCs64Coefficients =
    {
        // 每项 (乘 term 系数, 乘 offset 系数) 成对展开存放
        0xE82E91B1, 0xB32700D0, 0x44DE24C6, 0x88DFF51D,
        0x3BEB65BC, 0x29514038, 0xC89C5933, 0xB328CBDF,
        0x8D1EC211, 0xD405B4D5, 0x314EF5D5, 0x26E55F3C,
        0xE92491B1, 0x607801C6, 0xF0A5B9D5, 0x82B2C1FB,
        0x06064CF3, 0xFA51A3DA, 0xDA79FA60, 0x207CC464,
        0xBED20560, 0x3C3C74F3, 0x89F9C9F8, 0xD2D14B61,
        0x35FA38C6, 0xBB04A2C1, 0xCB49E3C6, 0xC5626167,
    };
    private const uint SecMsGecCs64Factor = 0x82893933;
    private const uint SecMsGecCs64Adjustment0 = 0xF2F90B1A;
    private const uint SecMsGecCs64Adjustment1 = 0x0B1A0000;

    internal static string GenerateSecMsGec(DateTimeOffset utcNow)
    {
        const long windowsEpochSeconds = 11_644_473_600;
        var seconds = utcNow.ToUnixTimeSeconds() + windowsEpochSeconds;
        seconds -= seconds % 300;
        var ticks = checked(seconds * 10_000_000);
        var input = ticks.ToString(CultureInfo.InvariantCulture) + TrustedClientToken;
        var data = Encoding.ASCII.GetBytes(input);

        var digest = SecMsGecSha256Digest(data);
        var cs64 = SecMsGecCs64Digest(data, digest);
        var cs64Hex = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cs64Hex, cs64.A);
        BinaryPrimitives.WriteUInt32LittleEndian(cs64Hex.AsSpan(4), cs64.B);
        return $"{Convert.ToHexString(cs64Hex)}-{Convert.ToHexString(digest)}";
    }

    private static uint SecMsGecSigma0(uint value)
        => BitOperations.RotateRight(value, 7) ^ BitOperations.RotateRight(value, 18) ^ (value >> 3);

    private static uint SecMsGecSigma1(uint value)
        => BitOperations.RotateRight(value, 17) ^ BitOperations.RotateRight(value, 19) ^ (value >> 10);

    private static void SecMsGecExpandSchedule(Span<uint> words, int firstWord)
    {
        for (var i = firstWord; i < 64; i++)
        {
            var x = words[i - 2];
            var y = words[i - 15];
            var sigma1 = BitOperations.RotateRight(x, 17) ^ BitOperations.RotateRight(x, 19) ^ (x >> 10);
            var sigma0 = BitOperations.RotateRight(y, 7) ^ BitOperations.RotateRight(y, 18) ^ (y >> 3);
            words[i] = sigma1 + words[i - 7] + sigma0 + words[i - 16];
        }
    }

    private static uint[] SecMsGecSha256Rounds(uint[] state, Span<uint> words, int firstRound)
    {
        var constants = firstRound == 14 ? SecMsGecFirstBlockConstants : SecMsGecSha256RoundConstants;
        var (a, b, c, d, e, f, g, h) = (state[0], state[1], state[2], state[3], state[4], state[5], state[6], state[7]);
        for (var i = firstRound; i < 64; i++)
        {
            var t1 = h
                     + (BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25))
                     + (g ^ (e & (f ^ g)))
                     + constants[i]
                     + words[i];
            var t2 = (BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22))
                     + ((a & b) | (c & (a | b)));
            var newA = t1 + t2;
            var newE = d + t1;
            (a, b, c, d, e, f, g, h) = (newA, a, b, c, newE, e, f, g);
        }
        return [a, b, c, d, e, f, g, h];
    }

    private static uint[] SecMsGecSha256Init(byte[] data)
    {
        var words = new uint[64];
        words[14] = (uint)((data[0] << 16) | (data[1] << 8) | data[2]);
        words[15] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3, 4));
        for (var i = 16; i < 30; i++)
        {
            var value = SecMsGecSha256Schedule[i - 16];
            if (i - 2 >= 14)
                value += i == 16
                    ? SecMsGecSigma1(words[i - 2]) ^ SecMsGecSigma1Adjustment
                    : SecMsGecSigma1(words[i - 2]);
            if (i - 7 >= 14)
                value += words[i - 7];
            if (i - 15 >= 14)
                value += i == 29
                    ? SecMsGecSigma0(words[i - 15]) ^ SecMsGecSigma0Adjustment
                    : SecMsGecSigma0(words[i - 15]);
            words[i] = value;
        }
        words[30] = SecMsGecSigma1(words[28]) + words[23] + SecMsGecSigma0(words[15])
                    + words[14] + SecMsGecSha256Adjustment - SecMsGecSha256RoundConstants[14];
        SecMsGecExpandSchedule(words, 31);

        var working = SecMsGecSha256Rounds(SecMsGecSha256State, words, 14);
        var state = new uint[8];
        for (var i = 0; i < 8; i++)
            state[i] = SecMsGecSha256Initial[i] + working[i];
        return state;
    }

    private static byte[] SecMsGecSha256Digest(byte[] data)
    {
        if (data.Length < 7)
            throw new ArgumentException("Token salt must contain at least seven bytes");

        var length = 57 + data.Length;
        var zeroCount = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + zeroCount + 8];
        data.AsSpan(7).CopyTo(padded);
        padded[data.Length - 7] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(^8), (ulong)length * 8);

        var state = SecMsGecSha256Init(data);
        var blockWords = new uint[64];
        for (var offset = 0; offset < padded.Length; offset += 64)
        {
            for (var i = 0; i < 16; i++)
                blockWords[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(offset + i * 4, 4));
            SecMsGecExpandSchedule(blockWords, 16);
            var working = SecMsGecSha256Rounds(state, blockWords, 0);
            for (var i = 0; i < 8; i++)
                state[i] += working[i];
        }

        var digest = new byte[32];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), state[i]);
        return digest;
    }

    private static (uint A, uint B) SecMsGecCs64Digest(byte[] data, byte[] digest)
    {
        var keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1u;
        var keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1u;
        var factorA = keyA * SecMsGecCs64Factor;
        var factorB = keyB * SecMsGecCs64Factor;

        // 与上游一致：数据流前置一个 0x00 字节、补零到 4 字节对齐后按小端 uint32 切词，
        // 奇数词数再补一个零词
        var stream = new byte[(data.Length + 4) / 4 * 4];
        data.CopyTo(stream, 1);
        var streamWords = stream.Length / 4;
        var wordCount = streamWords + (streamWords % 2);

        var terms = new uint[SecMsGecCs64Coefficients.Length / 2 + wordCount];
        var offsets = new uint[terms.Length];
        for (var i = 0; i < SecMsGecCs64Coefficients.Length / 2; i++)
        {
            terms[i] = SecMsGecCs64Coefficients[i * 2] * (i % 2 == 0 ? factorA : factorB);
            offsets[i] = SecMsGecCs64Coefficients[i * 2 + 1];
        }

        var index = SecMsGecCs64Coefficients.Length / 2;
        for (var w = 0; w < wordCount; w++)
        {
            // 超出补零缓冲的词（奇数补位）恒为 0
            var word = w < streamWords
                ? BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(w * 4, 4))
                : 0u;
            var term = word * (w % 2 == 0 ? keyA : keyB);
            var offset = BitOperations.RotateLeft(word, 16) * (w % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
            if (w == 0)
            {
                term += SecMsGecCs64Adjustment0 * factorA;
                offset += SecMsGecCs64Adjustment1;
            }
            terms[index] = term;
            offsets[index] = offset;
            index++;
        }

        uint swapAcc = 0, swapLast = 0, csState = 0, acc = 0;
        for (var i = 0; i < index; i += 2)
        {
            var x = terms[i];
            var y = terms[i + 1];
            var t = BitOperations.RotateLeft(offsets[i] + x, 16);
            var q = t * 0x76F50F34u;
            swapAcc += BitOperations.RotateLeft(q, 16) * 0xC3EFEA97u + q;
            t = BitOperations.RotateLeft(offsets[i + 1] + y, 16);
            q = t * 0x1881CE3Cu;
            swapLast = BitOperations.RotateLeft(q, 16) * 0x35BD1EC9u + q;
            swapAcc += swapLast;

            csState = csState * keyA + x;
            csState = BitOperations.RotateLeft(csState, 16) * 0xCF98B111u;
            csState = BitOperations.RotateLeft(csState, 16) * 0x87085B9Fu;
            csState = BitOperations.RotateLeft(csState, 16) * 0x12CEB96Du;
            csState = BitOperations.RotateLeft(csState, 16) * 0x257E1D83u;
            acc += csState;
            t = csState * keyB + y;
            t = BitOperations.RotateLeft(t, 16) * 0xA27416F5u;
            t = BitOperations.RotateLeft(t, 16) * 0xD38396FFu;
            csState = BitOperations.RotateLeft(BitOperations.RotateLeft(t, 16) * 0x7C932B89u, 16) * 0xBFA49F69u;
            acc += csState;
        }
        return (swapLast ^ csState, swapAcc ^ acc);
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
