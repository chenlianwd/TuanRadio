using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Moq;

namespace AIRadio.Desktop.Tests;

public class RecommendationServiceTests
{
    [Fact]
    public async Task CreateProgramAsync_ReturnsProgramWithPlayableAndUnavailableCandidates()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("夜晚华语 R&B\n晚间放松\nCity pop");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), 8))
            .ReturnsAsync((string query, int _) => new List<OnlineTrack>
            {
                new() { Id = $"netease:{query}:1", Title = $"{query} A", Artist = "Artist A", Source = "netease" },
                new() { Id = $"netease:{query}:2", Title = $"{query} B", Artist = "Artist B", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id.EndsWith(":1") ? $"http://example.com/{id}.mp3" : null);

        var program = await service.CreateProgramAsync(new RecommendationRequest
        {
            UserIntent = "想听适合晚上写代码的中文歌",
            Favorites = new[] { new Track { Title = "收藏歌", Artist = "收藏歌手", IsFavorite = true } }
        });

        Assert.Equal("想听适合晚上写代码的中文歌", program.Context.UserIntent);
        Assert.InRange(program.Tracks.Count, 1, 5);
        Assert.Contains(program.Tracks, item => item.IsPlayable && !string.IsNullOrWhiteSpace(item.PlayUrl));
        Assert.Contains(program.Tracks, item => !item.IsPlayable);
        Assert.All(program.Tracks, item => Assert.False(string.IsNullOrWhiteSpace(item.Reason)));
    }

    [Fact]
    public async Task RecordFeedback_ExcludesDislikedTrackFromNextProgram()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("ambient");
        search.Setup(x => x.SearchAsync("ambient", 8))
            .ReturnsAsync(new List<OnlineTrack>
            {
                new() { Id = "netease:skip", Title = "Skip Me", Artist = "Artist", Source = "netease" },
                new() { Id = "netease:keep", Title = "Keep Me", Artist = "Artist", Source = "netease" }
            });
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => $"http://example.com/{id}.mp3");

        service.RecordFeedback(new UserMusicFeedback
        {
            TrackId = "netease:skip",
            Action = MusicFeedbackAction.Dislike
        });

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "ambient" });

        Assert.DoesNotContain(program.Tracks, item => item.Track.SourceId == "netease:skip");
        Assert.Contains(program.Tracks, item => item.Track.SourceId == "netease:keep");
    }

    [Fact]
    public async Task CreateProgramAsync_ContinuesSearchingWhenEarlyCandidatesAreUnavailable()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("ambient");
        search.Setup(x => x.SearchAsync("ambient", 8))
            .ReturnsAsync(Enumerable.Range(1, 6)
                .Select(i => new OnlineTrack
                {
                    Id = $"netease:{i}",
                    Title = $"Candidate {i}",
                    Artist = "Artist",
                    Source = "netease"
                })
                .ToList());
        search.Setup(x => x.GetPlayUrlAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => id == "netease:6" ? "http://example.com/playable.mp3" : null);

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "ambient" });

        Assert.Contains(program.Tracks, item => item.Track.SourceId == "netease:6" && item.IsPlayable);
    }

    [Fact]
    public async Task CreateProgramAsync_EmptySearchResults_ReturnsEmptyProgram()
    {
        var minimax = new Mock<ILLMService>();
        var search = new Mock<IMusicSearchService>();
        var service = new RecommendationService(minimax.Object, search.Object);

        minimax.Setup(x => x.ChatAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .ReturnsAsync("安静钢琴曲\n轻音乐");
        search.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var program = await service.CreateProgramAsync(new RecommendationRequest { UserIntent = "安静" });

        Assert.Empty(program.Tracks);
    }
}
