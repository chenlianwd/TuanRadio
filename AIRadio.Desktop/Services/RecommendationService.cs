using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using Serilog;

namespace AIRadio.Desktop.Services;

public class RecommendationService : IRecommendationService
{
    private readonly ILLMService _llm;
    private readonly IMusicSearchService _musicSearch;
    private readonly List<UserMusicFeedback> _feedback = new();
    private readonly List<Track> _recentlyPlayed = new();
    private readonly HashSet<string> _returnedTrackIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _programGate = new(1, 1);
    private readonly object _stateGate = new();
    private string? _moodBias;

    public RecommendationService(ILLMService llm, IMusicSearchService musicSearch)
    {
        _llm = llm;
        _musicSearch = musicSearch;
    }

    public RadioProgram? CurrentProgram { get; private set; }
    public IReadOnlyCollection<UserMusicFeedback> FeedbackHistory
    {
        get
        {
            lock (_stateGate)
                return _feedback.ToArray();
        }
    }

    public IReadOnlyCollection<Track> RecentlyPlayed
    {
        get
        {
            lock (_stateGate)
                return _recentlyPlayed.ToArray();
        }
    }

    public Task<RadioProgram> CreateProgramAsync(RecommendationRequest request)
        => CreateProgramAsync(request, CancellationToken.None);

    public async Task<RadioProgram> CreateProgramAsync(
        RecommendationRequest request,
        CancellationToken cancellationToken)
    {
        await _programGate.WaitAsync(cancellationToken);
        try
        {
            return await CreateProgramCoreAsync(request, cancellationToken);
        }
        finally
        {
            _programGate.Release();
        }
    }

    private async Task<RadioProgram> CreateProgramCoreAsync(
        RecommendationRequest request,
        CancellationToken cancellationToken)
    {
        string? moodBias;
        lock (_stateGate)
            moodBias = _moodBias;

        var context = BuildContext(moodBias, request);
        var queries = await GenerateQueriesAsync(request, context, moodBias, cancellationToken);
        var excluded = BuildExcludedTracks(request);

        var tracks = new List<RecommendedTrack>();
        var playableCount = 0;
        foreach (var query in queries)
        {
            if (playableCount >= 5) break;

            cancellationToken.ThrowIfCancellationRequested();
            var results = await SearchMusicAsync(query, 8, cancellationToken);
            foreach (var result in results)
            {
                if (playableCount >= 5) break;
                if (IsExcluded(result, excluded) || tracks.Any(x => IsSameOnlineTrack(x.Track, result)))
                    continue;

                var url = await ResolvePlayUrlAsync(result, cancellationToken);
                if (string.IsNullOrWhiteSpace(url))
                {
                    if (tracks.Count(x => !x.IsPlayable) >= 5)
                        continue;

                    tracks.Add(new RecommendedTrack
                    {
                        Track = result.ToTrack(string.Empty),
                        Source = GetSourceDisplayName(result),
                        IsPlayable = false,
                        Score = 0.1,
                        Tags = BuildTags(context, result),
                        Reason = BuildUnavailableReason()
                    });
                    continue;
                }

                var track = result.ToTrack(url);
                playableCount++;
                tracks.Add(new RecommendedTrack
                {
                    Track = track,
                    Source = GetSourceDisplayName(result),
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
            DjOpening = BuildProgramOpening(tracks)
        };
        ApplyLocalization(program);
        // 新节目单生成即清空已播记忆：记忆只防"同一节目单内反复返回同一首"，
        // 跨节目单的重复由调用方的 ExcludedTracks 负责
        _returnedTrackIds.Clear();
        CurrentProgram = program;
        return program;
    }

    public Task<Track?> GetNextTrackAsync(RecommendationRequest request)
        => GetNextTrackAsync(request, CancellationToken.None);

    public async Task<Track?> GetNextTrackAsync(
        RecommendationRequest request,
        CancellationToken cancellationToken)
    {
        await _programGate.WaitAsync(cancellationToken);
        try
        {
            var excluded = BuildExcludedTracks(request);
            // 已播记忆：防止调用方未把已播曲目放进 ExcludedTracks 时，同一首被反复返回
            var next = CurrentProgram?.Tracks.FirstOrDefault(x =>
                x.IsPlayable &&
                !IsExcluded(x.Track, excluded) &&
                !IsAlreadyReturned(x.Track) &&
                !IsDisliked(x.Track));

            if (next == null)
            {
                var program = await CreateProgramCoreAsync(request, cancellationToken);
                next = program.Tracks.FirstOrDefault(x => x.IsPlayable && !IsAlreadyReturned(x.Track));
            }

            if (next != null)
                _returnedTrackIds.Add(next.Track.SourceId ?? next.Track.Id);

            return next?.Track;
        }
        finally
        {
            _programGate.Release();
        }
    }

    private bool IsAlreadyReturned(Track track)
        => _returnedTrackIds.Contains(track.SourceId ?? track.Id);

    public void RecordFeedback(UserMusicFeedback feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback.TrackId)) return;
        lock (_stateGate)
        {
            _feedback.Add(feedback);

            // Cap feedback history to avoid unbounded growth
            const int maxFeedback = 200;
            if (_feedback.Count > maxFeedback)
                _feedback.RemoveRange(0, _feedback.Count - maxFeedback);
        }
    }

    public void RecordPlayedTrack(Track track)
    {
        lock (_stateGate)
        {
            _recentlyPlayed.RemoveAll(item => TrackComparer.IsSameTrackIdentity(item, track));
            _recentlyPlayed.Add(track);
            const int historyLimit = 20;
            if (_recentlyPlayed.Count > historyLimit)
                _recentlyPlayed.RemoveRange(0, _recentlyPlayed.Count - historyLimit);
        }
    }

    /// <summary>会话级氛围偏好：覆盖意图正则检测出的 mood，并注入搜索词生成提示。传 null/空白清除。</summary>
    public void SetMoodBias(string? mood)
    {
        lock (_stateGate)
            _moodBias = NormalizeMood(mood);
    }

    /// <summary>电台轮换选曲启发式：优先避开与当前曲同歌手，避免连播同一歌手。</summary>
    public static Track? PickDiversifiedTrack(IReadOnlyList<Track> pool, Track? current)
    {
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        var differentArtist = pool.Where(t => current == null || t.Artist != current.Artist).ToList();
        var candidates = differentArtist.Count > 0 ? differentArtist : pool.ToList();
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>启动推荐选曲启发式：收藏优先，排除正在播放的曲目，再做同歌手分散。</summary>
    public static Track? PickStartupRecommendation(
        IReadOnlyList<Track> favorites,
        IReadOnlyList<Track> allTracks,
        Track? current)
    {
        var source = favorites.Count > 0 ? favorites : allTracks;
        if (source.Count == 0) return null;

        var candidates = source.Where(t => t != current).ToList();
        if (candidates.Count == 0)
            candidates = source.ToList();
        return PickDiversifiedTrack(candidates, current);
    }

    private static string? NormalizeMood(string? mood)
    {
        if (string.IsNullOrWhiteSpace(mood)) return null;
        var value = mood.Trim().ToLowerInvariant();
        if (value.Contains("calm") || value.Contains("安静") || value.Contains("放松") || value.Contains("温柔")) return "calm";
        if (value.Contains("energy") || value.Contains("energetic") || value.Contains("燃") || value.Contains("热血") || value.Contains("兴奋")) return "energetic";
        if (value.Contains("sad") || value.Contains("emo") || value.Contains("难过")) return "sad";
        // 其它短词当作自由氛围提示进搜索词生成，不覆盖结构化 mood
        return value.Length <= 12 ? value : null;
    }

    private List<Track> BuildExcludedTracks(RecommendationRequest request)
    {
        Track[] played;
        UserMusicFeedback[] feedback;
        lock (_stateGate)
        {
            played = _recentlyPlayed.ToArray();
            feedback = _feedback.ToArray();
        }

        return request.ExcludedTracks
            .Concat(request.Playlist)
            .Concat(request.RecentlyPlayed)
            .Concat(played)
            .Concat(feedback
                .Where(x => x.Action == MusicFeedbackAction.Dislike)
                .Select(x => new Track { SourceId = x.TrackId, Id = x.TrackId }))
            .ToList();
    }

    private bool IsDisliked(Track track)
    {
        lock (_stateGate)
        {
            return _feedback.Any(f =>
                f.Action == MusicFeedbackAction.Dislike &&
                IsSameSource(f.TrackId, track.SourceId ?? track.Id));
        }
    }

    private async Task<List<string>> GenerateQueriesAsync(
        RecommendationRequest request,
        ListeningContext context,
        string? moodBias,
        CancellationToken cancellationToken)
    {
        var recentHistory = BuildRecentHistory(request);
        var fallback = BuildFallbackQueries(request, context, recentHistory);
        // 未配置时 ChatAsync 返回“请先在设置中配置 AI 服务。”提示文案，
        // 直接走本地兜底关键词，避免把提示文案当搜索词发给音源
        if (_llm is LLMService llmService && !llmService.IsConfigured())
            return fallback;
        try
        {
            var userIntent = GetLocalizedUserIntent(request.UserIntentKey, request.UserIntent);
            var moodHint = string.IsNullOrWhiteSpace(moodBias)
                ? string.Empty
                : AppLanguage.T($"\n氛围偏好：{moodBias}", $"\nMood preference: {moodBias}");
            var recentTracks = recentHistory
                .Reverse()
                .DistinctBy(track => $"{track.Title.Trim()}|{track.Artist.Trim()}", StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(track => $"{track.Title} - {track.Artist}");
            var prompt = AppLanguage.Current == "en"
                ? $"""
                    Generate 3 short music-search queries, one per line.
                    Infer the shared genre, era, language and mood of recently played tracks instead of repeating one title.
                    Output only the queries — no greetings, preamble or explanations.
                    User intent: {userIntent}
                    Current track: {request.CurrentTrack?.Title} - {request.CurrentTrack?.Artist}
                    Recently played: {string.Join(", ", recentTracks)}
                    Favorites: {string.Join(", ", request.Favorites.Take(5).Select(x => $"{x.Title} {x.Artist}"))}{moodHint}
                    """
                : $"""
                    根据用户意图生成 3 个适合音乐搜索的短关键词，每行一个。
                    关键词应归纳最近已播放歌曲的共同风格、年代、语言和氛围，不要只复述某一首歌名。
                    只输出关键词本身：不要问候、开场白或任何解释。
                    用户意图：{userIntent}
                    当前歌曲：{request.CurrentTrack?.Title} - {request.CurrentTrack?.Artist}
                    最近已播放：{string.Join(", ", recentTracks)}
                    收藏参考：{string.Join(", ", request.Favorites.Take(5).Select(x => $"{x.Title} {x.Artist}"))}{moodHint}
                    """;
            var response = await ChatAsync(prompt, cancellationToken);
            var queries = SanitizeSearchQueries(
                response.Split(new[] { '\r', '\n', ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries));
            return queries.Count > 0 ? queries : fallback;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Recommendation query generation failed");
            return fallback;
        }
    }

    private static ListeningContext BuildContext(string? moodBias, RecommendationRequest request)
    {
        var text = request.UserIntent ?? string.Empty;
        // 结构化氛围（calm/energetic/sad）优先用会话级 bias；自由词 bias 只进搜索提示，不占结构化 mood
        var mood = moodBias is "calm" or "energetic" or "sad" ? moodBias : DetectMood(text);
        return new ListeningContext
        {
            UserIntent = text,
            UserIntentKey = request.UserIntentKey,
            Mood = mood,
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

    private IReadOnlyCollection<Track> BuildRecentHistory(RecommendationRequest request)
    {
        Track[] recorded;
        lock (_stateGate)
            recorded = _recentlyPlayed.ToArray();

        return recorded
            .Concat(request.RecentlyPlayed)
            .Reverse()
            .DistinctBy(track => $"{track.Title.Trim()}|{track.Artist.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Reverse()
            .ToArray();
    }

    private static List<string> BuildFallbackQueries(
        RecommendationRequest request,
        ListeningContext context,
        IReadOnlyCollection<Track> recentHistory)
    {
        var queries = new List<string>();
        var userIntent = GetLocalizedUserIntent(request.UserIntentKey, request.UserIntent);
        if (!string.IsNullOrWhiteSpace(userIntent))
            queries.Add(userIntent);
        if (request.CurrentTrack != null)
            queries.Add(AppLanguage.T(
                $"{request.CurrentTrack.Artist} 相似歌曲",
                $"songs similar to {request.CurrentTrack.Artist}"));
        var recentArtists = recentHistory
            .Reverse()
            .Select(track => track.Artist)
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (recentArtists.Count > 1)
            queries.Add(AppLanguage.T(
                $"{string.Join(" ", recentArtists)} 相似风格",
                $"music similar to {string.Join(" ", recentArtists)}"));
        queries.Add(context.Mood switch
        {
            "calm" => AppLanguage.T("安静 氛围", "calm ambient"),
            "energetic" => AppLanguage.T("摇滚 热血", "energetic rock"),
            "sad" => AppLanguage.T("emo 治愈", "reflective emo"),
            _ => AppLanguage.T("华语 流行", "popular music")
        });
        return queries.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
    }

    private static string CleanQuery(string value)
    {
        // 数字必须带 . 或 、 才算列表序号：裸剥会把 "90s city pop"/"2020 华语金曲" 这类年代关键词削头
        var cleaned = Regex.Replace(value, @"^\s*(?:\d+[.、]|[-*、])+", "");
        cleaned = Regex.Replace(cleaned, @"[""“”‘’<>]+", "");
        return cleaned.Trim();
    }

    /// <summary>
    /// LLM 偶发不守"只输出关键词"的约定（尤其回复里带问候/解说）：
    /// 整段台词被按标点切开后，开场白碎片会混进搜索词，搜出与意图无关的歌并直接播放。
    /// 按"像搜索词"过滤：短、无句末标点、无问候/解说开头、无句尾语气词。
    /// </summary>
    internal static List<string> SanitizeSearchQueries(IEnumerable<string> candidates)
    {
        return candidates
            .Select(CleanQuery)
            .Where(x => x.Length > 0)
            // 含 CJK 的行按 25 字上限，纯 ASCII 关键词（如 "japanese city pop 80s funk"）放宽到 60
            .Where(x => x.Length <= (Regex.IsMatch(x, @"\p{IsCJKUnifiedIdeographs}") ? 25 : 60))
            .Where(x => !Regex.IsMatch(x, @"[。!！?？;；:：~～]"))
            .Where(x => !Regex.IsMatch(x, @"^(哈喽|哈囉|你好|嗨|我是|欢迎|根据|为你|為你|接下来|接下來|我们|我們|这首|這首|希望|祝)"))
            .Where(x => !Regex.IsMatch(x, @"[吧呢哦喔啦呀嘛咯哟]$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string BuildProgramTitle(ListeningContext context)
    {
        var userIntent = GetLocalizedUserIntent(context.UserIntentKey, context.UserIntent);
        return string.IsNullOrWhiteSpace(userIntent)
            ? AppLanguage.T("今日电台", "Today's Radio")
            : AppLanguage.T($"为你调好的：{userIntent}", $"Tuned for you: {userIntent}");
    }

    private static string BuildReason(ListeningContext context)
    {
        var userIntent = GetLocalizedUserIntent(context.UserIntentKey, context.UserIntent);
        return string.IsNullOrWhiteSpace(userIntent)
            ? AppLanguage.T(
                $"这首歌适合 {LocalizeTimeOfDay(context.TimeOfDay)} 的 TuanRadio 续播。",
                $"This track fits a {LocalizeTimeOfDay(context.TimeOfDay)} TuanRadio session.")
            : AppLanguage.T(
                $"它和“{userIntent}”的氛围接近，可以接在当前电台里。",
                $"Its mood matches “{userIntent}” and fits naturally into this station.");
    }

    private static string GetLocalizedUserIntent(string? key, string? fallback)
    {
        if (key == RecommendationIntentKeys.ContinueStation ||
            fallback is "继续当前电台" or "Continue current station")
        {
            return AppLanguage.T("继续当前电台", "Continue current station");
        }

        return fallback ?? string.Empty;
    }

    private static string BuildUnavailableReason()
        => AppLanguage.T(
            "找到了候选歌曲，但当前音源暂时不可播放。",
            "This candidate was found, but its source is temporarily unavailable.");

    private static string BuildProgramOpening(IReadOnlyCollection<RecommendedTrack> tracks)
    {
        var count = tracks.Count(track => track.IsPlayable);
        return count > 0
            ? AppLanguage.T($"我先为你排了 {count} 首可播放歌曲。", $"I've lined up {count} playable track(s) for you.")
            : AppLanguage.T("暂时没找到合适的可播放歌曲。", "I couldn't find a suitable playable track right now.");
    }

    private static string LocalizeTimeOfDay(string value) => value switch
    {
        "morning" => AppLanguage.T("清晨", "morning"),
        "afternoon" => AppLanguage.T("午后", "afternoon"),
        "night" => AppLanguage.T("夜晚", "evening"),
        "late-night" => AppLanguage.T("深夜", "late-night"),
        _ => value
    };

    public static void ApplyLocalization(RadioProgram program)
    {
        program.Title = BuildProgramTitle(program.Context);
        program.DjOpening = BuildProgramOpening(program.Tracks);
        foreach (var item in program.Tracks)
        {
            item.Source = AppLanguage.MusicSourceName(item.Source);
            item.Tags = item.Tags.Select(AppLanguage.MusicSourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
            item.Reason = item.IsPlayable ? BuildReason(program.Context) : BuildUnavailableReason();
        }
    }

    private static List<string> BuildTags(ListeningContext context, OnlineTrack track)
    {
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Mood)) tags.Add(context.Mood);
        if (!string.IsNullOrWhiteSpace(context.Scene)) tags.Add(context.Scene);
        if (!string.IsNullOrWhiteSpace(track.Source)) tags.Add(AppLanguage.MusicSourceName(track.Source));
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

    private static string GetSourceDisplayName(OnlineTrack track)
        => AppLanguage.MusicSourceName(
            string.IsNullOrWhiteSpace(track.Source) ? ParseSource(track.Id) : track.Source);

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

    private Task<List<OnlineTrack>> SearchMusicAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
        => _musicSearch is MultiSourceMusicService multi
            ? multi.SearchAsync(query, limit, MusicSearchIntent.Automatic, cancellationToken)
            : _musicSearch.SearchAsync(query, limit)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    private Task<string?> ResolvePlayUrlAsync(
        OnlineTrack track,
        CancellationToken cancellationToken)
        => _musicSearch is MultiSourceMusicService multi
            ? multi.GetPlayUrlAsync(track, cancellationToken)
            : _musicSearch.GetPlayUrlAsync(track.Id)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

    private Task<string> ChatAsync(string prompt, CancellationToken cancellationToken)
        => _llm is LLMService llm
            // 关键词生成必须走无人设调用：DJ 人设会让模型回整段台词，台词碎片被当搜索词
            // 搜出与意图完全无关的歌（如把开场白拿去搜出儿歌并直接播放）
            ? llm.ChatRawAsync(prompt, cancellationToken)
            : _llm.ChatAsync(prompt, new List<ChatMessage>())
                .WaitAsync(cancellationToken);

}
