using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IMinimaxService
{
    void SetApiKey(string apiKey);
    Task<string> ChatAsync(string userMessage, List<ChatMessage> history);
    Task<byte[]> TextToSpeechAsync(string text, string voiceId, string emotion = "neutral");
    Task<string> GenerateTrackIntroductionAsync(Track current, Track next);
}
