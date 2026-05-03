using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class MinimaxService : IMinimaxService
{
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
        return await RetryPolicy.ExecuteAsync(async () =>
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

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

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
        return await RetryPolicy.ExecuteAsync(async () =>
        {
            var requestBody = new
            {
                model = "speech-2.6-hd",
                text,
                stream = false,
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

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("audio", out var audioHex))
            {
                var hex = audioHex.GetString();
                if (!string.IsNullOrEmpty(hex))
                {
                    return Convert.FromHexString(hex);
                }
            }

            Log.Warning("TTS response did not contain audio data");
            return Array.Empty<byte>();
        });
    }

    public async Task<string> GenerateTrackIntroductionAsync(Track current, Track next)
    {
        var prompt = $"当前播放：{current.Title} - {current.Artist}\n" +
                     $"即将播放：{next.Title} - {next.Artist}\n" +
                     "请用简短活泼的语言（不超过50字）介绍即将播放的歌曲，像电台DJ一样自然过渡。";

        return await ChatAsync(prompt, new List<ChatMessage>());
    }
}
