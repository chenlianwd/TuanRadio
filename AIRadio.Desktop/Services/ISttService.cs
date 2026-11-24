using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public enum SttStage
{
    Idle,
    DownloadingModel,
    LoadingModel,
    Transcribing
}

public sealed record SttProgress(SttStage Stage, double? DownloadProgress = null);

public interface ISttService
{
    IObservable<SttProgress> ProgressChanged => Observable.Empty<SttProgress>();

    ApiFailureInfo? LastFailure => null;

    Task<string> TranscribeAsync(string wavFilePath);

    Task<string> TranscribeAsync(string wavFilePath, CancellationToken cancellationToken)
        => TranscribeAsync(wavFilePath);

    Task<string> TranscribeAsync(string wavFilePath, string? languageHint, CancellationToken cancellationToken)
        => TranscribeAsync(wavFilePath, cancellationToken);
}
