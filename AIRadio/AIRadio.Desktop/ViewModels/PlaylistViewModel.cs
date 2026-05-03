using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase
{
    private readonly IAudioService _audioService;
    private readonly IMusicSearchService _musicSearchService;

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

        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync);

        PlayOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(async track =>
        {
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null)
            {
                Log.Warning("No play URL for track {Id}", track.Id);
                return;
            }
            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
            var index = Tracks.Count - 1;
            _audioService.PlayAtIndex(index);
        });

        AddOnlineCommand = ReactiveCommand.CreateFromTask<OnlineTrack>(async track =>
        {
            var url = await _musicSearchService.GetPlayUrlAsync(track.Id);
            if (url == null) return;
            var t = track.ToTrack(url);
            Tracks.Add(t);
            _audioService.AddTracks(new[] { t });
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
    }
}
