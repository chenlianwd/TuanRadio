using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class CandidateRankerTests
{
    private static OnlineTrack MakeTrack(string title, string artist, long durationMs = 240000, string source = "netease")
        => new()
        {
            Id = $"{source}:123",
            Title = title,
            Artist = artist,
            DurationMs = durationMs,
            Source = source
        };

    [Fact]
    public void ScoreFallbackCandidate_PenalizesInstrumental_WhenTargetIsNotInstrumental()
    {
        var target = MakeTrack("青花瓷", "周杰伦", 239000);
        var originalCand = MakeTrack("青花瓷", "周杰伦", 238000);
        var instCand = MakeTrack("青花瓷 (伴奏)", "周杰伦", 239000);

        var originalScore = CandidateRanker.ScoreFallbackCandidate(originalCand, target);
        var instScore = CandidateRanker.ScoreFallbackCandidate(instCand, target);

        Assert.True(originalScore > 0.85, $"Original score should be high, was {originalScore}");
        Assert.True(originalScore - instScore >= 0.5, $"Penalty should heavily dock instrumental ({originalScore} vs {instScore})");
    }

    [Fact]
    public void ScoreFallbackCandidate_DoesNotPenalizeInstrumental_WhenTargetIsInstrumental()
    {
        var target = MakeTrack("青花瓷 (伴奏)", "周杰伦", 239000);
        var instCand = MakeTrack("青花瓷 伴奏", "周杰伦", 239000);

        var score = CandidateRanker.ScoreFallbackCandidate(instCand, target);
        Assert.True(score > 0.80, $"Requested instrumental should receive high score, was {score}");
    }

    [Fact]
    public void ScoreFallbackCandidate_PenalizesCoverAndRemix_WhenTargetIsOriginal()
    {
        var target = MakeTrack("晴天", "周杰伦", 269000);
        var originalCand = MakeTrack("晴天", "周杰伦", 268000);
        var coverCand = MakeTrack("晴天 (Cover)", "网络歌手", 269000);
        var remixCand = MakeTrack("晴天 (DJ慢摇版)", "周杰伦", 269000);

        var origScore = CandidateRanker.ScoreFallbackCandidate(originalCand, target);
        var coverScore = CandidateRanker.ScoreFallbackCandidate(coverCand, target);
        var remixScore = CandidateRanker.ScoreFallbackCandidate(remixCand, target);

        Assert.True(origScore > coverScore + 0.4);
        Assert.True(origScore > remixScore + 0.4);
    }

    [Fact]
    public void ScoreFallbackCandidate_PenalizesDurationDeviation()
    {
        var target = MakeTrack("夜曲", "周杰伦", 226000);
        var closeCand = MakeTrack("夜曲", "周杰伦", 225000); // 1s 偏差
        var previewClip = MakeTrack("夜曲", "周杰伦", 30000); // 30s 试听片段，相差近200秒

        var closeScore = CandidateRanker.ScoreFallbackCandidate(closeCand, target);
        var previewScore = CandidateRanker.ScoreFallbackCandidate(previewClip, target);

        Assert.True(closeScore > previewScore + 0.15);
    }

    [Fact]
    public void ScoreFallbackCandidate_HandlesMultiArtistAndFeat()
    {
        var target = MakeTrack("千里之外", "周杰伦 / 费玉清", 255000);
        var featCand = MakeTrack("千里之外", "周杰伦 feat. 费玉清", 255000);
        var commaCand = MakeTrack("千里之外", "周杰伦, 费玉清", 255000);

        var featScore = CandidateRanker.ScoreFallbackCandidate(featCand, target);
        var commaScore = CandidateRanker.ScoreFallbackCandidate(commaCand, target);

        Assert.True(featScore > 0.90, $"feat score was {featScore}");
        Assert.True(commaScore > 0.90, $"comma score was {commaScore}");
    }

    [Theory]
    [InlineData("晴天 (伴奏)", "周杰伦", true)]
    [InlineData("晴天 [Instrumental]", "周杰伦", true)]
    [InlineData("晴天 (Karaoke)", "周杰伦", true)]
    [InlineData("晴天 (纯音乐)", "周杰伦", true)]
    [InlineData("晴天 (Cover)", "张三", true)]
    [InlineData("晴天 (翻唱)", "李四", true)]
    [InlineData("晴天 (DJ版)", "周杰伦", true)]
    [InlineData("晴天 (加速版)", "周杰伦", true)]
    [InlineData("晴天", "周杰伦", false)]
    [InlineData("夜曲", "周杰伦", false)]
    public void IsUnwantedVersion_IdentifiesUnwantedTagsCorrectly(string title, string artist, bool expectedUnwanted)
    {
        var track = MakeTrack(title, artist);
        var result = CandidateRanker.IsUnwantedVersion(track, "晴天");
        Assert.Equal(expectedUnwanted, result);
    }

    [Fact]
    public void RankSearchResults_PutsOriginalBeforeInstrumentalAndCover()
    {
        var original = MakeTrack("晴天", "周杰伦", 269000);
        var instrumental = MakeTrack("晴天 (伴奏)", "周杰伦", 269000);
        var cover = MakeTrack("晴天 (Cover)", "翻唱歌手", 269000);

        var list = new List<OnlineTrack> { instrumental, cover, original };
        var ranked = CandidateRanker.RankSearchResults(list, "周杰伦 晴天");

        Assert.Equal(original.Title, ranked[0].Title);
        Assert.Equal(original.Artist, ranked[0].Artist);
    }

    [Fact]
    public void DeduplicateTracks_RemovesLooseDuplicates()
    {
        var list = new List<OnlineTrack>
        {
            MakeTrack("七里香", "周杰伦", 299000, "netease"),
            MakeTrack("七里香 (Live)", "周杰伦", 305000, "kugou"),
            MakeTrack("夜曲", "周杰伦", 226000, "netease"),
        };

        var deduped = CandidateRanker.DeduplicateTracks(list);
        // 七里香与七里香(Live)标题宽松等同且歌手等同，去重只保留第1个
        Assert.Equal(2, deduped.Count);
        Assert.Equal("七里香", deduped[0].Title);
        Assert.Equal("夜曲", deduped[1].Title);
    }

    [Fact]
    public void IsUnwantedVersion_DoesNotFalsePositiveOnDjArtistOrLegitTitles()
    {
        // 艺术家名字包含 DJ（如 DJ Okawari、DJ Snake）不能被误判为非预期混音版
        var djTrack = MakeTrack("Flower Dance", "DJ Okawari");
        Assert.False(CandidateRanker.IsUnwantedVersion(djTrack, "Flower Dance"));

        var snakeTrack = MakeTrack("Turn Down for What", "DJ Snake, Lil Jon");
        Assert.False(CandidateRanker.IsUnwantedVersion(snakeTrack, "Turn Down for What"));

        // 包含常规英文词汇 Live 或 Cover 的正统歌曲不能被误判
        var liveTrack = MakeTrack("Live Forever", "Oasis");
        Assert.False(CandidateRanker.IsUnwantedVersion(liveTrack, "Oasis"));

        var coverTrack = MakeTrack("Cover Me", "Bruce Springsteen");
        Assert.False(CandidateRanker.IsUnwantedVersion(coverTrack, "Bruce Springsteen"));
    }

    [Fact]
    public void ScoreSearchResult_DoesNotPenalizeLegitLiveOrCoverTitles()
    {
        var liveTrack = MakeTrack("Live Forever", "Oasis");
        var normalTrack = MakeTrack("Wonderwall", "Oasis");

        var liveScore = CandidateRanker.ScoreSearchResult(liveTrack, "Oasis Live Forever");
        var normalScore = CandidateRanker.ScoreSearchResult(normalTrack, "Oasis Wonderwall");

        // 两个正统搜索评分均应保持在较高水平，不能受到 0.30/0.60 负向惩罚
        Assert.True(liveScore > 0.80, $"Live Forever score should be high, was {liveScore}");
        Assert.True(normalScore > 0.80, $"Wonderwall score should be high, was {normalScore}");
    }
}
