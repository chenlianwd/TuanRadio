using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services.Music;

/// <summary>用户选定文件与文件夹的本地索引。索引只保存路径和媒体标签，不复制音频。</summary>
public sealed class LocalLibraryProvider : IMusicProvider
{
    private const int MaxIndexedFiles = 100_000;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".wma", ".aac", ".aiff" };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly string _indexFile;
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private LocalLibraryIndex _index = new();
    private IReadOnlyDictionary<string, LocalLibraryEntry> _entries =
        new Dictionary<string, LocalLibraryEntry>(StringComparer.Ordinal);

    public MusicProviderDescriptor Descriptor { get; } = new(
        "local", "Local Library", ProviderNetworkScope.LocalFileOnly);

    public int TrackCount => Volatile.Read(ref _entries).Count;

    public bool ContainsFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Volatile.Read(ref _entries).ContainsKey(BuildId(fullPath));
    }

    public LocalLibraryProvider(string? indexFile = null)
    {
        _indexFile = indexFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRadio", "local-library.json");
    }

    /// <summary>启动时恢复索引；坏文件按空库处理，避免阻断播放器。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_indexFile)) return;
            await using var stream = File.OpenRead(_indexFile);
            var index = await JsonSerializer.DeserializeAsync<LocalLibraryIndex>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _index = index ?? new LocalLibraryIndex();
            _index.Folders ??= new List<string>();
            _index.ImportedFiles ??= new List<string>();
            _index.Entries ??= new List<LocalLibraryEntry>();
            Publish(_index.Entries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Local library index could not be loaded");
            _index = new LocalLibraryIndex();
            Publish(_index.Entries);
        }
        finally { _changeGate.Release(); }
    }

    public async Task<int> AddFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = CopyIndex();
            if (!next.Folders.Contains(fullPath, PathComparer))
                next.Folders.Add(fullPath);
            await RescanCoreAsync(next, cancellationToken).ConfigureAwait(false);
            return TrackCount;
        }
        finally { _changeGate.Release(); }
    }

    public async Task<int> AddFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = CopyIndex();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                if (IsAudioFile(fullPath) && File.Exists(fullPath) &&
                    !next.ImportedFiles.Contains(fullPath, PathComparer))
                    next.ImportedFiles.Add(fullPath);
            }
            await RescanCoreAsync(next, cancellationToken).ConfigureAwait(false);
            return TrackCount;
        }
        finally { _changeGate.Release(); }
    }

    public async Task<int> RescanAsync(CancellationToken cancellationToken = default)
    {
        await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RescanCoreAsync(CopyIndex(), cancellationToken).ConfigureAwait(false);
            return TrackCount;
        }
        finally { _changeGate.Release(); }
    }

    private LocalLibraryIndex CopyIndex() => new()
    {
        Folders = new List<string>(_index.Folders),
        ImportedFiles = new List<string>(_index.ImportedFiles),
        Entries = new List<LocalLibraryEntry>(_index.Entries)
    };

    private async Task RescanCoreAsync(LocalLibraryIndex next, CancellationToken cancellationToken)
    {
        var paths = await Task.Run(() => EnumerateAudioPaths(next, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var entries = await Task.Run(() =>
        {
            var result = new List<LocalLibraryEntry>(paths.Count);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ReadEntry(path));
            }
            return result;
        }, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        next.Entries = entries;
        await SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _index = next;
        Publish(entries);
    }

    private List<string> EnumerateAudioPaths(LocalLibraryIndex index, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(index.ImportedFiles.Where(File.Exists).Where(IsAudioFile), PathComparer);
        foreach (var root in index.Folders)
        {
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && paths.Count < MaxIndexedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsAudioFile(file)) paths.Add(file);
                        if (paths.Count >= MaxIndexedFiles) break;
                    }
                    foreach (var child in Directory.EnumerateDirectories(current))
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug(ex, "Local library directory skipped: {Directory}", current);
                }
            }
        }
        return paths.Take(MaxIndexedFiles).ToList();
    }

    private static bool IsAudioFile(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    private static LocalLibraryEntry ReadEntry(string path)
    {
        var track = Track.FromFile(path);
        return new LocalLibraryEntry(
            BuildId(path),
            path, track.Title, track.Artist, track.Album, track.Duration.TotalMilliseconds);
    }

    private static string BuildId(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)));

    private async Task SaveAsync(LocalLibraryIndex index, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_indexFile)!;
        Directory.CreateDirectory(directory);
        var temporary = _indexFile + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, index, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            File.Move(temporary, _indexFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Publish(IEnumerable<LocalLibraryEntry> entries)
        => Volatile.Write(ref _entries,
            entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Id) &&
                                   !string.IsNullOrWhiteSpace(entry.Path))
                .GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));

    public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0) return Task.FromResult(new List<OnlineTrack>());
        var terms = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = Volatile.Read(ref _entries).Values
            .Where(entry => terms.All(term =>
                entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Artist.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Album.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(entry => new OnlineTrack
            {
                Id = $"local:{entry.Id}", Title = entry.Title, Artist = entry.Artist,
                Album = entry.Album, DurationMs = (long)entry.DurationMs, Source = "Local Library"
            }).ToList();
        return Task.FromResult(results);
    }

    public Task<MediaResolutionResult> ResolveAsync(
        ProviderTrackRef track,
        IReadOnlyDictionary<string, string>? providerMetadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(track.ProviderId, Descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
            !Volatile.Read(ref _entries).TryGetValue(track.TrackId, out var entry) ||
            !File.Exists(entry.Path))
            return Task.FromResult(MediaResolutionResult.Failed(track.ProviderId, PlaybackFailureKind.NotFound));
        return Task.FromResult(MediaResolutionResult.Playable(new ResolvedMedia(track, new Uri(entry.Path))));
    }

    public sealed class LocalLibraryIndex
    {
        public List<string> Folders { get; set; } = new();
        public List<string> ImportedFiles { get; set; } = new();
        public List<LocalLibraryEntry> Entries { get; set; } = new();
    }

    public sealed record LocalLibraryEntry(
        string Id, string Path, string Title, string Artist, string Album, double DurationMs);
}
