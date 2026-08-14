using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private readonly string _playlistDir;
    private readonly string _playlistFile;
    private readonly Func<string, string, Task> _writeAllTextAsync;
    private bool _isPlayingOnline;
    private bool _isLoading;
    private readonly IDisposable _selectedTrackSub;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly HashSet<string> _favoriteIds = new();
    private static readonly string DefaultPlaylistDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string DefaultPlaylistFile = Path.Combine(DefaultPlaylistDir, "playlist.json");

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Track> Favorites { get; } = new();
    public ObservableCollection<OnlineTrack> SearchResults { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsSearching { get; set; }
    [Reactive] public bool HasSearchStatus { get; set; }
    [Reactive] public string SearchStatusMessage { get; set; } = string.Empty;
    [Reactive] public string SearchButtonText { get; set; } = "搜索";
    [Reactive] public int TabIndex { get; set; } // 0=列表, 1=收藏, 2=搜索

    public ReactiveCommand<Track, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFavoritesCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSearchCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayOnlineCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddOnlineCommand { get; }
    public ReactiveCommand<Track, Unit> ToggleFavoriteCommand { get; }
    public ReactiveCommand<Track, Unit> PlayFavoriteCommand { get; }

    public PlaylistViewModel(
        IAudioService audioService,
        IMusicSearchService musicSearchService,
        string? playlistFile = null,
        Func<string, string, Task>? writeAllTextAsync = null)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;
        _playlistFile = playlistFile ?? DefaultPlaylistFile;
        _playlistDir = Path.GetDirectoryName(_playlistFile) ?? DefaultPlaylistDir;
        _writeAllTextAsync = writeAllTextAsync ?? ((path, contents) => File.WriteAllTextAsync(path, contents));

        RemoveTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.RemoveTrack(track);
            Tracks.Remove(track);
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            _audioService.ClearPlaylist();
            Tracks.Clear();
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        ShowPlaylistCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 0;
        });

        ShowFavoritesCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 1;
        });

        ShowSearchCommand = ReactiveCommand.Create(() =>
        {
            TabIndex = 2;
        });

        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);

        PlayOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(PlayOnlineAsync);

        AddOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(async track =>
        {
            SetSearchStatus($"正在添加《{track.Title}》...");
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null)
            {
                SetSearchStatus("这首歌暂时无法获取播放地址，换一个结果试试。");
                return;
            }

            // Check if already in playlist
            var existing = Tracks.FirstOrDefault(t => t.FilePath == url);
            if (existing != null)
            {
                SetSearchStatus("这首歌已经在播放列表里了。");
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            TabIndex = 0;
            await SaveAsync();
            SetSearchStatus($"已添加《{track.Title}》。");
        });

        ToggleFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            var playlistTrack = FindMatchingTrack(track) ?? track;
            if (!Tracks.Contains(playlistTrack))
                Tracks.Add(playlistTrack);

            if (_favoriteIds.Contains(playlistTrack.Id))
            {
                _favoriteIds.Remove(playlistTrack.Id);
                playlistTrack.IsFavorite = false;
                Favorites.Remove(playlistTrack);
            }
            else
            {
                _favoriteIds.Add(playlistTrack.Id);
                playlistTrack.IsFavorite = true;
                if (!Favorites.Contains(playlistTrack))
                    Favorites.Add(playlistTrack);
            }
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        PlayFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            var index = Tracks.IndexOf(track);
            if (index >= 0)
                _audioService.PlayAtIndex(index);
        });

        _selectedTrackSub = this.WhenAnyValue(x => x.SelectedTrack)
            .WhereNotNull()
            .Subscribe(track =>
            {
                var index = Tracks.IndexOf(track);
                if (index >= 0)
                {
                    _audioService.PlayAtIndex(index);
                }
            });

        // Auto-save when tracks change (skip during initial load)
        Tracks.CollectionChanged += OnTracksChanged;
    }

    private void OnTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_isLoading) _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        Tracks.CollectionChanged -= OnTracksChanged;
        try
        {
            if (!File.Exists(_playlistFile)) return;

            var json = await File.ReadAllTextAsync(_playlistFile);
            var data = JsonSerializer.Deserialize<PlaylistData>(json);
            if (data == null || data.Tracks == null) return;

            _favoriteIds.Clear();
            if (data.FavoriteIds != null && data.FavoriteIds.Count > 0)
            {
                foreach (var id in data.FavoriteIds)
                    _favoriteIds.Add(id);
            }
            else
            {
                // Backward compat: load from legacy IsFavorite field
                foreach (var item in data.Tracks)
                    if (item.IsFavorite && !string.IsNullOrEmpty(item.Id))
                        _favoriteIds.Add(item.Id);
            }

            Tracks.Clear();
            Favorites.Clear();

            // Separate online and local tracks
            var onlineItems = new List<(PlaylistTrack Item, Track Track)>();
            foreach (var item in data.Tracks)
            {
                if (item.IsOnline && !string.IsNullOrEmpty(item.SourceId))
                {
                    var track = new Track
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                        FilePath = item.FilePath,
                        SourceId = item.SourceId,
                        IsFavorite = _favoriteIds.Contains(item.Id) || item.IsFavorite
                    };
                    onlineItems.Add((item, track));
                    Tracks.Add(track);
                    if (track.IsFavorite)
                        Favorites.Add(track);
                }
                else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    var track = new Track
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                        FilePath = item.FilePath,
                        IsFavorite = _favoriteIds.Contains(item.Id) || item.IsFavorite
                    };
                    Tracks.Add(track);
                    if (track.IsFavorite)
                        Favorites.Add(track);
                }
            }

            // Refresh online URLs in parallel
            if (onlineItems.Count > 0)
            {
                var tasks = onlineItems.Select(async x =>
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(x.Item.SourceId))
                            return;

                        var url = await _musicSearchService.GetPlayUrlAsync(x.Item.SourceId);
                        if (!string.IsNullOrEmpty(url))
                            x.Track.FilePath = url;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to refresh URL for {SourceId}", x.Item.SourceId);
                    }
                });
                await Task.WhenAll(tasks);
            }

            if (Tracks.Count > 0)
            {
                _audioService.LoadTracks(Tracks);
                Log.Information("Loaded {Count} tracks from playlist", Tracks.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load playlist");
        }
        finally
        {
            _isLoading = false;
            Tracks.CollectionChanged += OnTracksChanged;
            _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted); // save once after load completes
        }
    }

    internal async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_playlistDir);
            var data = new PlaylistData
            {
                Tracks = Tracks.Select(t => new PlaylistTrack
                {
                    Id = t.Id,
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = t.Album,
                    DurationMs = (long)t.Duration.TotalMilliseconds,
                    FilePath = t.FilePath,
                    SourceId = t.SourceId,
                    IsOnline = t.SourceId != null,
                    IsFavorite = _favoriteIds.Contains(t.Id)
                }).ToList(),
                FavoriteIds = _favoriteIds.ToList()
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await _writeAllTextAsync(_playlistFile, json);
            Log.Debug("Playlist saved: {Count} tracks, {FavCount} favorites", Tracks.Count, _favoriteIds.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save playlist");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task PlayOnlineAsync(OnlineTrack track)
    {
        if (_isPlayingOnline) return;
        _isPlayingOnline = true;
        try
        {
            SetSearchStatus($"正在播放《{track.Title}》...");
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null)
            {
                Log.Warning("No play URL for track {Id}", track.Id);
                SetSearchStatus("这首歌暂时无法获取播放地址，换一个结果试试。");
                return;
            }

            // Check if track already in playlist (by URL)
            var existingIndex = Tracks.FindIndex(t => t.FilePath == url);
            if (existingIndex >= 0)
            {
                _audioService.PlayAtIndex(existingIndex);
                TabIndex = 0;
                SetSearchStatus($"正在播放《{track.Title}》。");
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            var index = Tracks.Count - 1;
            _audioService.PlayAtIndex(index);
            TabIndex = 0;
            await SaveAsync();
            SetSearchStatus($"正在播放《{track.Title}》。");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Play online failed for {Track}", track.Title);
            SetSearchStatus("播放失败，可能是音源不可用或网络超时。");
        }
        finally
        {
            _isPlayingOnline = false;
        }
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;

        IsSearching = true;
        SearchButtonText = "搜索中...";
        SetSearchStatus($"正在搜索“{SearchText}”...");
        try
        {
            var results = await _musicSearchService.SearchAsync(SearchText);
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }
            TabIndex = 2; // auto-switch to search results
            SetSearchStatus(BuildSearchStatusMessage(results.Count));
            Log.Information("Search '{Query}' returned {Count} results", SearchText, results.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search failed");
            SetSearchStatus("搜索失败，可能是网络异常或音乐 API 服务不可用。");
        }
        finally
        {
            IsSearching = false;
            SearchButtonText = "搜索";
        }
    }

    /// <summary>构造搜索状态消息：透传各音源成功/超时/失败（子项目 5）。</summary>
    private string BuildSearchStatusMessage(int totalCount)
    {
        if (_musicSearchService is not Services.MultiSourceMusicService multi || multi.LastSearchReport.Count == 0)
            return totalCount == 0
                ? "没有找到可用结果。可以换个关键词，或检查网络/音乐 API 服务。"
                : $"找到 {totalCount} 个结果。";

        var perSource = string.Join("；", multi.LastSearchReport.Select(s =>
            s.Status == "ok" ? $"{s.Name}成功{s.Count}条"
            : s.Status == "timeout" ? $"{s.Name}超时"
            : $"{s.Name}失败:{s.Error}"));
        return totalCount == 0
            ? $"未找到结果。{perSource}"
            : $"找到 {totalCount} 个结果。{perSource}";
    }

    private void SetSearchStatus(string message)
    {
        SearchStatusMessage = message;
        HasSearchStatus = !string.IsNullOrWhiteSpace(message);
    }

    public void AddExternalTrack(Track track)
    {
        var existing = FindMatchingTrack(track);
        if (existing != null)
        {
            if (track.IsFavorite || _favoriteIds.Contains(existing.Id))
            {
                existing.IsFavorite = true;
                _favoriteIds.Add(existing.Id);
                if (!Favorites.Contains(existing))
                    Favorites.Add(existing);
                _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }
            return;
        }

        Tracks.Add(track);
        _audioService.AddTracks(new[] { track });
        if (_favoriteIds.Contains(track.Id) && !Favorites.Contains(track))
            Favorites.Add(track);
        TabIndex = 0;
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    private Track? FindMatchingTrack(Track track)
    {
        return Tracks.FirstOrDefault(t =>
            (!string.IsNullOrWhiteSpace(track.SourceId) && t.SourceId == track.SourceId) ||
            (!string.IsNullOrWhiteSpace(track.FilePath) && t.FilePath == track.FilePath) ||
            (!string.IsNullOrWhiteSpace(track.Id) && t.Id == track.Id));
    }

    public void AddFiles(string[] filePaths)
    {
        var added = new List<Track>();
        foreach (var path in filePaths)
        {
            var track = Track.FromFile(path);
            if (Tracks.Any(t => t.FilePath == track.FilePath))
                continue;

            Tracks.Add(track);
            added.Add(track);
        }

        if (added.Count > 0)
            _audioService.AddTracks(added);

        TabIndex = 0;
        _ = SaveAsync().ContinueWith(t => Log.Warning(t.Exception, "SaveAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        _selectedTrackSub.Dispose();
        Tracks.CollectionChanged -= OnTracksChanged;
    }
}

// ObservableCollection lacks FindIndex; used by MainWindowViewModel and ChatViewModel
public static class ObservableCollectionExtensions
{
    public static int FindIndex<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        for (int i = 0; i < collection.Count; i++)
        {
            if (predicate(collection[i])) return i;
        }
        return -1;
    }
}

// DTOs for JSON deserialization — mutable setters required by JsonSerializer
internal class PlaylistData
{
    public List<PlaylistTrack> Tracks { get; set; } = new();
    public List<string> FavoriteIds { get; set; } = new();
}

internal class PlaylistTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public long DurationMs { get; set; }
    public string FilePath { get; set; } = "";
    public string? SourceId { get; set; }
    public bool IsOnline { get; set; }
    public bool IsFavorite { get; set; }
}
