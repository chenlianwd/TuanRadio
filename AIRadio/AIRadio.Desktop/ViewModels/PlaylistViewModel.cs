using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveCommand = ReactiveUI.ReactiveCommand;

namespace AIRadio.Desktop.ViewModels;

public class PlaylistViewModel : ViewModelBase
{
    private readonly IAudioService _audioService;

    public ObservableCollection<Track> Tracks { get; } = new();

    [Reactive] public Track? SelectedTrack { get; set; }

    public ReactiveCommand<Track, Unit> RemoveTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearPlaylistCommand { get; }

    public PlaylistViewModel(IAudioService audioService)
    {
        _audioService = audioService;

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
