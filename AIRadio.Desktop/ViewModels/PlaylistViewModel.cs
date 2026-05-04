using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;
    private bool _isPlayingOnline;

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<OnlineTrack> SearchResults { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }
    [Reactive] public string SearchText { get; set; } = string.Empty;
    [Reactive] public bool IsSearching { get; set; }
    [Reactive] public bool IsSearchMode { get; set; }

    public ReactiveCommand<Track, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSearchCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSearchCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> PlayOnlineCommand { get; }
    public ReactiveCommand<OnlineTrack, Unit> AddOnlineCommand { get; }

    public PlaylistViewModel(IAudioService audioService, IMusicSearchService musicSearchService)
    {
        _audioService = audioService;
        _musicSearchService = musicSearchService;

        RemoveTrackCommand = ReactiveCommand.Create<Track>(track =>
        {
            _audioService.RemoveTrack(track);
            Tracks.Remove(track);
        });

        ClearPlaylistCommand = ReactiveCommand.Create(() =>
        {
            _audioService.ClearPlaylist();
            Tracks.Clear();
        });

        ToggleSearchCommand = ReactiveCommand.Create(() =>
        {
            IsSearchMode = !IsSearchMode;
        });

        ShowPlaylistCommand = ReactiveCommand.Create(() =>
        {
            IsSearchMode = false;
        });

        ShowSearchCommand = ReactiveCommand.Create(() =>
        {
            IsSearchMode = true;
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
            IsSearchMode = false;
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
                IsSearchMode = false;
                return;
            }

            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            var index = Tracks.Count - 1;
            _audioService.PlayAtIndex(index);
            IsSearchMode = false;
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
            IsSearchMode = true; // auto-switch to search results
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

    public void AddFiles(string[] filePaths)
    {
        foreach (var path in filePaths)
        {
            var track = Track.FromFile(path);
            Tracks.Add(track);
        }
        _audioService.AddTracks(Tracks);
        IsSearchMode = false;
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
