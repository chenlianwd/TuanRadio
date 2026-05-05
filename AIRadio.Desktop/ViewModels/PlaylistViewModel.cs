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
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private bool _isPlayingOnline;
    private bool _isLoading;
    private readonly HashSet<string> _favoriteIds = new();
    private static readonly string PlaylistDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRadio");
    private static readonly string PlaylistFile = Path.Combine(PlaylistDir, "playlist.json");

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Track> Favorites { get; } = new();
    public ObservableCollection<OnlineTrack> SearchResults { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsSearching { get; set; }
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

    public PlaylistViewModel(IAudioService audioService, IMusicSearchService musicSearchService)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;

        RemoveTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.RemoveTrack(track);
            Tracks.Remove(track);
            _ = SaveAsync();
        });

        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            _audioService.ClearPlaylist();
            Tracks.Clear();
            _ = SaveAsync();
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
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null) return;

            // Check if already in playlist
            var existing = Tracks.FirstOrDefault(t => t.FilePath == url);
            if (existing != null) return;

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            TabIndex = 0;
            await SaveAsync();
        });

        ToggleFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            if (_favoriteIds.Contains(track.Id))
            {
                _favoriteIds.Remove(track.Id);
                track.IsFavorite = false;
                Favorites.Remove(track);
            }
            else
            {
                _favoriteIds.Add(track.Id);
                track.IsFavorite = true;
                if (!Favorites.Contains(track))
                    Favorites.Add(track);
            }
            _ = SaveAsync();
        });

        PlayFavoriteCommand = ReactiveCommand.Create<Track>(track =>
        {
            var index = Tracks.IndexOf(track);
            if (index >= 0)
                _audioService.PlayAtIndex(index);
        });

        this.WhenAnyValue(x => x.SelectedTrack)
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
        if (!_isLoading) _ = SaveAsync();
    }

    public async Task LoadAsync()
    {
        _isLoading = true;
        Tracks.CollectionChanged -= OnTracksChanged;
        try
        {
            if (!File.Exists(PlaylistFile)) return;

            var json = await File.ReadAllTextAsync(PlaylistFile);
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
            foreach (var item in data.Tracks)
            {
                if (item.IsOnline && !string.IsNullOrEmpty(item.SourceId))
                {
                    // Re-fetch URL for online tracks
                    var url = await _musicSearchService.GetPlayUrlAsync(item.SourceId);
                    if (!string.IsNullOrEmpty(url))
                    {
                        var track = new Track
                        {
                            Id = item.Id,
                            Title = item.Title,
                            Artist = item.Artist,
                            Album = item.Album,
                            Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                            FilePath = url,
                            SourceId = item.SourceId,
                            IsFavorite = item.IsFavorite
                        };
                        Tracks.Add(track);
                        if (_favoriteIds.Contains(track.Id))
                            Favorites.Add(track);
                    }
                }
                else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    // Local file - verify exists
                    var track = new Track
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        Duration = TimeSpan.FromMilliseconds(item.DurationMs),
                        FilePath = item.FilePath,
                        IsFavorite = item.IsFavorite
                    };
                    Tracks.Add(track);
                    if (_favoriteIds.Contains(track.Id))
                        Favorites.Add(track);
                }
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
            _ = SaveAsync(); // save once after load completes
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(PlaylistDir);
            var data = new PlaylistData
            {
                Tracks = Tracks.Select(t => new PlaylistTrack
                {
                    Id = t.Id,
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = t.Album,
                    DurationMs = (long)t.Duration.TotalMilliseconds,
                    FilePath = t.SourceId != null ? "" : t.FilePath,
                    SourceId = t.SourceId,
                    IsOnline = t.SourceId != null,
                    IsFavorite = _favoriteIds.Contains(t.Id)
                }).ToList(),
                FavoriteIds = _favoriteIds.ToList()
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(PlaylistFile, json);
            Log.Debug("Playlist saved: {Count} tracks, {FavCount} favorites", Tracks.Count, _favoriteIds.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save playlist");
        }
    }

    private async Task PlayOnlineAsync(OnlineTrack track)
    {
        if (_isPlayingOnline) return;
        _isPlayingOnline = true;
        try
        {
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null)
            {
                Log.Warning("No play URL for track {Id}", track.Id);
                return;
            }

            // Check if track already in playlist (by URL)
            var existingIndex = Tracks.FindIndex(t => t.FilePath == url);
            if (existingIndex >= 0)
            {
                _audioService.PlayAtIndex(existingIndex);
                TabIndex = 0;
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            var index = Tracks.Count - 1;
            _audioService.PlayAtIndex(index);
            TabIndex = 0;
            await SaveAsync();
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
        try
        {
            var results = await _musicSearchService.SearchAsync(SearchText);
            SearchResults.Clear();
            foreach (var track in results)
            {
                SearchResults.Add(track);
            }
            TabIndex = 2; // auto-switch to search results
            Log.Information("Search '{Query}' returned {Count} results", SearchText, results.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search failed");
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void AddExternalTrack(Track track)
    {
        if (Tracks.Any(t =>
                (!string.IsNullOrWhiteSpace(track.SourceId) && t.SourceId == track.SourceId) ||
                (!string.IsNullOrWhiteSpace(track.FilePath) && t.FilePath == track.FilePath)))
        {
            return;
        }

        Tracks.Add(track);
        if (_favoriteIds.Contains(track.Id) && !Favorites.Contains(track))
            Favorites.Add(track);
        TabIndex = 0;
        _ = SaveAsync();
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
        _ = SaveAsync();
    }
}

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
