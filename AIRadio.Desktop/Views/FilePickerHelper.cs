using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AIRadio.Desktop.Views;

/// <summary>
/// Shared file picker logic for importing audio files.
/// </summary>
public static class FilePickerHelper
{
    public static async Task<string[]> PickAudioFilesAsync(TopLevel topLevel)
    {
        var audioFilter = new FilePickerFileType(AppLanguage.T("音频文件", "Audio files"))
        {
            Patterns = ["*.mp3", "*.flac", "*.wav", "*.ogg", "*.m4a", "*.wma", "*.aac"]
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppLanguage.T("选择音频文件", "Select audio files"),
            AllowMultiple = true,
            FileTypeFilter = [audioFilter]
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();
    }
}
