using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class MinimaxService : IMinimaxService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private string _apiKey = string.Empty;

    public MinimaxService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<string> ChatAsync(string userMessage, List<ChatMessage> history)
    {
        return await ExecuteMinimaxRequestAsync("AI chat", async token =>
        {
            var messages = new List<object>();
            foreach (var msg in history)
            {
                messages.Add(new { role = msg.Role.ToString().ToLower(), content = msg.Content });
            }
            messages.Add(new { role = "user", content = userMessage });

            var requestBody = new
            {
                model = "MiniMax-M2.5",
                messages,
                max_tokens = 200,
                temperature = 1.0
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minimaxi.com/v1/text/chatcompletion_v2")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _httpClient.SendAsync(request, token);
            await EnsureSuccessAsync(response);

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                return message.GetProperty("content").GetString() ?? string.Empty;
            }

            return string.Empty;
        });
    }

    public async Task<byte[]> TextToSpeechAsync(string text, string voiceId, string emotion = "neutral")
    {
        return await ExecuteMinimaxRequestAsync("TTS", async token =>
        {
            var requestBody = new
            {
                model = "speech-2.8-hd",
                text,
                stream = false,
                language_boost = "Chinese",
                voice_setting = new
                {
                    voice_id = voiceId,
                    speed = 1.0,
                    vol = 1.0,
                    pitch = 0,
                    emotion
                },
                audio_setting = new
                {
                    sample_rate = 32000,
                    bitrate = 128000,
                    format = "mp3",
                    channel = 1
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minimaxi.com/v1/t2a_v2")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _httpClient.SendAsync(request, token);
            await EnsureSuccessAsync(response);

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("base_resp", out var baseResp))
            {
                var statusCode = baseResp.TryGetProperty("status_code", out var code) ? code.GetInt32() : 0;
                if (statusCode != 0)
                {
                    var statusMsg = baseResp.TryGetProperty("status_msg", out var msg) ? msg.GetString() : "Unknown TTS error";
                    Log.Warning("TTS API returned error {Code}: {Message}", statusCode, statusMsg);
                    throw new MinimaxApiException(ApiFailureInfo.FromMinimaxBaseResponse(statusCode, statusMsg ?? string.Empty));
                }
            }

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("audio", out var audioHex))
            {
                var hex = audioHex.GetString();
                if (!string.IsNullOrEmpty(hex))
                {
                    var bytes = Convert.FromHexString(hex);
                    Log.Information("TTS audio generated: {Bytes} bytes, voice={VoiceId}, emotion={Emotion}", bytes.Length, voiceId, emotion);
                    return bytes;
                }
            }

            Log.Warning("TTS response did not contain audio data");
            return Array.Empty<byte>();
        });
    }

    private async Task<T> ExecuteMinimaxRequestAsync<T>(string operation, Func<CancellationToken, Task<T>> action)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new MinimaxApiException(ApiFailureInfo.MissingApiKey());

        try
        {
            return await RetryPolicy.ExecuteAsync(async () =>
            {
                using var cts = new CancellationTokenSource(RequestTimeout);
                return await action(cts.Token);
            });
        }
        catch (MinimaxApiException)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new MinimaxApiException(new ApiFailureInfo(
                ApiFailureKind.Timeout,
                "AI 响应超时",
                $"{operation} 请求超过 {RequestTimeout.TotalSeconds:0} 秒仍未返回。",
                "可以稍后重试，或检查网络代理/防火墙。"), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MinimaxApiException(ApiFailureInfo.FromException(ex), ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        throw new MinimaxApiException(ApiFailureInfo.FromStatusCode(response.StatusCode, body));
    }

    public async Task<string> GenerateTrackIntroductionAsync(Track current, Track next)
    {
        var prompt = $"当前播放：{current.Title} - {current.Artist}\n" +
                     $"即将播放：{next.Title} - {next.Artist}\n" +
                     "请像真实电台 DJ 一样自然过渡，可以加入歌曲氛围、听众情绪和一句温柔的引导。";

        return await ChatAsync(prompt, new List<ChatMessage>());
    }
}
