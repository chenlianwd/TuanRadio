using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AIRadio.Desktop.Views;

/// <summary>
/// Shared file picker logic for importing audio files.
/// </summary>
public static class FilePickerHelper
{
    private static readonly FilePickerFileType AudioFilter = new("音频文件")
    {
        Patterns = ["*.mp3", "*.flac", "*.wav", "*.ogg", "*.m4a", "*.wma", "*.aac"]
    };

    public static async Task<string[]> PickAudioFilesAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = true,
            FileTypeFilter = [AudioFilter]
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToArray();
    }
}
