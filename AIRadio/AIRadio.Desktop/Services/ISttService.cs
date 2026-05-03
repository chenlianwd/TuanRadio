using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public interface ISttService
{
    Task<string> TranscribeAsync(string wavFilePath);
}
