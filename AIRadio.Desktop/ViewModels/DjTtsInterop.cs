using System;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;

namespace AIRadio.Desktop.ViewModels;

/// <summary>
/// MainWindowViewModel 与 ChatViewModel 共用的 DJ/TTS 互操作。
/// 之前两份逐行雷同的私有复制（StopTts/单曲推荐/语音生成）收敛于此。
/// </summary>
internal static class DjTtsInterop
{
    public static async Task StopTtsWithoutBlockingUiAsync(
        IAudioService audioService,
        CancellationToken cancellationToken)
    {
        var stopTask = Task.Factory.StartNew(
            audioService.StopTts,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            Serilog.Log.Warning("TTS stop did not complete within 2 seconds; continuing without blocking UI");
        }
    }

    public static Task<Track?> RequestDjRecommendationAsync(
        IDJService djService,
        Track? current,
        CancellationToken cancellationToken)
        => djService is DJService dj
            ? dj.RecommendNextTrackAsync(current, cancellationToken)
            : djService.RecommendNextTrackAsync(current).WaitAsync(cancellationToken);

    public static Task<byte[]?> GenerateSpeechAsync(
        IDJService djService,
        string text,
        CancellationToken cancellationToken)
        => djService is DJService dj
            ? dj.GenerateSpeechAsync(text, cancellationToken)
            : djService.GenerateSpeechAsync(text).WaitAsync(cancellationToken);
}
