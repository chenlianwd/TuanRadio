using System.Collections.Generic;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services;

public interface IMusicSearchService
{
    string Name { get; }
    Task<List<OnlineTrack>> SearchAsync(string keyword, int limit = 20);
    Task<string?> GetPlayUrlAsync(string trackId);
}

public class OnlineTrack
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string Source { get; set; } = string.Empty;

    public Track ToTrack(string playUrl) => new()
    {
        Id = Id,
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = System.TimeSpan.FromMilliseconds(DurationMs),
        FilePath = playUrl,
        SourceId = Id  // store original ID for URL re-resolution
    };
}
