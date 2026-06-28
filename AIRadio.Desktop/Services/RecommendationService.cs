using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class RecommendationService : IRecommendationService
{
    private readonly ILLMService _llm;
    private readonly IMusicSearchService _musicSearch;
    private readonly List<UserMusicFeedback> _feedback = new();

    public RecommendationService(ILLMService llm, IMusicSearchService musicSearch)
    {
        _llm = llm;
        _musicSearch = musicSearch;
    }

    public RadioProgram? CurrentProgram { get; private set; }
    public IReadOnlyCollection<UserMusicFeedback> FeedbackHistory => _feedback.AsReadOnly();

    public async Task<RadioProgram> CreateProgramAsync(RecommendationRequest request)
    {
        var context = BuildContext(request);
        var queries = await GenerateQueriesAsync(request, context);
        var excluded = BuildExcludedTracks(request);

        var tracks = new List<RecommendedTrack>();
        var playableCount = 0;
        foreach (var query in queries)
        {
            if (playableCount >= 5) break;

            var results = await _musicSearch.SearchAsync(query, 8);
            foreach (var result in results)
            {
                if (playableCount >= 5) break;
                if (IsExcluded(result, excluded) || tracks.Any(x => IsSameOnlineTrack(x.Track, result)))
                    continue;

                var url = await _musicSearch.GetPlayUrlAsync(result.Id);
                if (string.IsNullOrWhiteSpace(url))
                {
                    if (tracks.Count(x => !x.IsPlayable) >= 5)
                        continue;

                    tracks.Add(new RecommendedTrack
                    {
                        Track = result.ToTrack(string.Empty),
                        Source = string.IsNullOrWhiteSpace(result.Source) ? ParseSource(result.Id) : result.Source,
                        IsPlayable = false,
                        Score = 0.1,
                        Tags = BuildTags(context, result),
                        Reason = "找到了候选歌曲，但当前音源暂时不可播放。"
                    });
                    continue;
                }

                var track = result.ToTrack(url);
                playableCount++;
                tracks.Add(new RecommendedTrack
                {
                    Track = track,
                    Source = string.IsNullOrWhiteSpace(result.Source) ? ParseSource(result.Id) : result.Source,
                    PlayUrl = url,
                    IsPlayable = true,
                    Score = 1.0 - playableCount * 0.05,
                    Tags = BuildTags(context, result),
                    Reason = BuildReason(context)
                });
            }
        }

        tracks = tracks
            .OrderByDescending(x => x.IsPlayable)
            .ThenByDescending(x => x.Score)
            .Take(5)
            .ToList();

        var program = new RadioProgram
        {
            Title = BuildProgramTitle(context),
            Context = context,
            Tracks = tracks,
            DjOpening = tracks.Any(x => x.IsPlayable)
                ? $"我先为你排了 {tracks.Count(x => x.IsPlayable)} 首可播放歌曲。"
                : "暂时没找到合适的可播放歌曲。"
        };
        CurrentProgram = program;
        return program;
    }

    public async Task<Track?> GetNextTrackAsync(RecommendationRequest request)
    {
        var excluded = BuildExcludedTracks(request);
        var next = CurrentProgram?.Tracks.FirstOrDefault(x =>
            x.IsPlayable &&
            !IsExcluded(x.Track, excluded) &&
            !_feedback.Any(f => f.Action == MusicFeedbackAction.Dislike && IsSameSource(f.TrackId, x.Track.SourceId ?? x.Track.Id)));

        if (next != null)
            return next.Track;

        var program = await CreateProgramAsync(request);
        return program.Tracks.FirstOrDefault(x => x.IsPlayable)?.Track;
    }

    public void RecordFeedback(UserMusicFeedback feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback.TrackId)) return;
        _feedback.Add(feedback);

        // Cap feedback history to avoid unbounded growth
        const int maxFeedback = 200;
        if (_feedback.Count > maxFeedback)
            _feedback.RemoveRange(0, _feedback.Count - maxFeedback);
    }

    private List<Track> BuildExcludedTracks(RecommendationRequest request)
    {
        return request.ExcludedTracks
            .Concat(request.Playlist)
            .Concat(_feedback
                .Where(x => x.Action == MusicFeedbackAction.Dislike)
                .Select(x => new Track { SourceId = x.TrackId, Id = x.TrackId }))
            .ToList();
    }

    private async Task<List<string>> GenerateQueriesAsync(RecommendationRequest request, ListeningContext context)
    {
        var fallback = BuildFallbackQueries(request, context);
        try
        {
            var prompt = $"""
                根据用户意图生成 3 个适合音乐搜索的短关键词，每行一个。
                用户意图：{request.UserIntent}
                当前歌曲：{request.CurrentTrack?.Title} - {request.CurrentTrack?.Artist}
                收藏参考：{string.Join(", ", request.Favorites.Take(5).Select(x => $"{x.Title} {x.Artist}"))}
                """;
            var response = await _llm.ChatAsync(prompt, new List<ChatMessage>());
            var queries = response
                .Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanQuery)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            return queries.Count > 0 ? queries : fallback;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Recommendation query generation failed");
            return fallback;
        }
    }

    private static ListeningContext BuildContext(RecommendationRequest request)
    {
        var text = request.UserIntent ?? string.Empty;
        return new ListeningContext
        {
            UserIntent = text,
            Mood = DetectMood(text),
            Scene = DetectScene(text),
            TimeOfDay = DateTime.Now.Hour switch
            {
                >= 5 and < 12 => "morning",
                >= 12 and < 18 => "afternoon",
                >= 18 and < 23 => "night",
                _ => "late-night"
            }
        };
    }

    private static List<string> BuildFallbackQueries(RecommendationRequest request, ListeningContext context)
    {
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.UserIntent))
            queries.Add(request.UserIntent);
        if (request.CurrentTrack != null)
            queries.Add($"{request.CurrentTrack.Title} {request.CurrentTrack.Artist}");
        queries.Add(context.Mood switch
        {
            "calm" => "安静 氛围",
            "energetic" => "摇滚 热血",
            "sad" => "emo 治愈",
            _ => "华语 流行"
        });
        return queries.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static string CleanQuery(string value)
    {
        var cleaned = Regex.Replace(value, @"^\s*[-*\d.、]+", "");
        cleaned = Regex.Replace(cleaned, @"[""“”‘’<>]+", "");
        return cleaned.Trim();
    }

    private static string BuildProgramTitle(ListeningContext context)
        => string.IsNullOrWhiteSpace(context.UserIntent) ? "今日电台" : $"为你调好的：{context.UserIntent}";

    private static string BuildReason(ListeningContext context)
        => string.IsNullOrWhiteSpace(context.UserIntent)
            ? $"这首歌适合 {context.TimeOfDay} 的 AIRadio 续播。"
            : $"它和“{context.UserIntent}”的氛围接近，可以接在当前电台里。";

    private static List<string> BuildTags(ListeningContext context, OnlineTrack track)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Mood)) tags.Add(context.Mood);
        if (!string.IsNullOrWhiteSpace(context.Scene)) tags.Add(context.Scene);
        if (!string.IsNullOrWhiteSpace(track.Source)) tags.Add(track.Source);
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static string DetectMood(string text)
    {
        if (Regex.IsMatch(text, "安静|放松|深夜|睡|温柔|慢")) return "calm";
        if (Regex.IsMatch(text, "燃|热血|运动|摇滚|兴奋")) return "energetic";
        if (Regex.IsMatch(text, "难过|emo|失恋")) return "sad";
        return "neutral";
    }

    private static string DetectScene(string text)
    {
        if (Regex.IsMatch(text, "代码|编程|工作|专注|学习")) return "focus";
        if (Regex.IsMatch(text, "开车|通勤|路上")) return "commute";
        if (Regex.IsMatch(text, "睡|夜晚|深夜")) return "night";
        return string.Empty;
    }

    private static string ParseSource(string id)
    {
        var idx = id.IndexOf(':');
        return idx > 0 ? id[..idx] : string.Empty;
    }

    private static bool IsExcluded(OnlineTrack candidate, IEnumerable<Track> excludedTracks)
        => excludedTracks.Any(track =>
            IsSameSource(track.SourceId ?? track.Id, candidate.Id) ||
            IsSameMusicIdentity(track.Title, track.Artist, candidate.Title, candidate.Artist));

    private static bool IsExcluded(Track candidate, IEnumerable<Track> excludedTracks)
        => excludedTracks.Any(track =>
            IsSameSource(track.SourceId ?? track.Id, candidate.SourceId ?? candidate.Id) ||
            IsSameMusicIdentity(track.Title, track.Artist, candidate.Title, candidate.Artist));

    private static bool IsSameOnlineTrack(Track track, OnlineTrack candidate)
        => IsSameSource(track.SourceId ?? track.Id, candidate.Id) ||
           IsSameMusicIdentity(track.Title, track.Artist, candidate.Title, candidate.Artist);

    private static bool IsSameSource(string? left, string? right)
        => MusicIdentity.IsSameSource(left, right);

    private static bool IsSameMusicIdentity(string titleA, string artistA, string titleB, string artistB)
        => MusicIdentity.IsSameMusicIdentity(titleA, artistA, titleB, artistB);

}
