using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public interface ISttService
{
    Task<string> TranscribeAsync(string wavFilePath);

    Task<string> TranscribeAsync(string wavFilePath, CancellationToken cancellationToken)
        => TranscribeAsync(wavFilePath);

    Task<string> TranscribeAsync(string wavFilePath, string? languageHint, CancellationToken cancellationToken)
        => TranscribeAsync(wavFilePath, cancellationToken);
}
