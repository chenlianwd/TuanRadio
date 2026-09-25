using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;

namespace AIRadio.Desktop.Tests;

public sealed class LocalLibraryProviderTests
{
    [Fact]
    public async Task FolderIndex_SearchResolveReloadAndRescan()
    {
        var root = Path.Combine(Path.GetTempPath(), "airadio-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "music");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Night Drive.mp3");
            await File.WriteAllBytesAsync(file, [0, 1, 2, 3]);
            var indexFile = Path.Combine(root, "index.json");
            var provider = new LocalLibraryProvider(indexFile);
            Assert.Equal(1, await provider.AddFolderAsync(folder));
            Assert.True(provider.ContainsFile(file));

            var found = Assert.Single(await provider.SearchAsync("Night Drive", 10, CancellationToken.None));
            Assert.StartsWith("local:", found.Id);
            var broker = new MusicSourceBroker(provider);
            var results = await broker.SearchAsync("Night Drive", 10, MusicSearchIntent.Automatic, CancellationToken.None);
            Assert.Equal(found.Id, Assert.Single(results).Id);
            Assert.Equal(file, await broker.GetPlayUrlAsync(found, CancellationToken.None));

            var reloaded = new LocalLibraryProvider(indexFile);
            await reloaded.LoadAsync();
            Assert.Single(await reloaded.SearchAsync("Drive", 10, CancellationToken.None));
            File.Delete(file);
            Assert.Equal(PlaybackFailureKind.NotFound, (await reloaded.ResolveAsync(
                new ProviderTrackRef("local", found.Id["local:".Length..]), null, CancellationToken.None)).Failure);
            Assert.Equal(0, await reloaded.RescanAsync());
            Assert.False(reloaded.ContainsFile(file));
            Assert.Empty(await reloaded.SearchAsync("Drive", 10, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ImportedFile_IsSearchableWithoutFolderScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "airadio-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Quiet Morning.flac");
            await File.WriteAllBytesAsync(file, [0, 1, 2, 3]);
            var provider = new LocalLibraryProvider(Path.Combine(root, "index.json"));
            Assert.Equal(1, await provider.AddFilesAsync([file]));
            Assert.Single(await provider.SearchAsync("Morning", 10, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedIndexSave_DoesNotRetainUncommittedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "airadio-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "music");
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "Track.mp3"), [0, 1, 2, 3]);
            var indexDirectory = Path.Combine(root, "index-parent");
            await File.WriteAllTextAsync(indexDirectory, "blocking file");
            var provider = new LocalLibraryProvider(Path.Combine(indexDirectory, "index.json"));

            await Assert.ThrowsAsync<IOException>(() => provider.AddFolderAsync(folder));
            File.Delete(indexDirectory);
            Directory.CreateDirectory(indexDirectory);

            Assert.Equal(0, await provider.RescanAsync());
            Assert.Equal(1, await provider.AddFolderAsync(folder));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
