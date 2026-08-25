using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 曲目身份判定测试：TrackComparer（正在播放/去重两档）与 MusicIdentity（精确/宽松）。
/// 重复添加与跨源回退是历史高发缺陷，这些纯函数是相关链路的判定核心。
/// </summary>
public class TrackIdentityTests
{
    // ---- TrackComparer.IsSameTrack（宽松：单侧有标识即比）----

    [Fact]
    public void IsSameTrack_NullCases()
    {
        var track = new Track { SourceId = "netease:1" };
        Assert.True(TrackComparer.IsSameTrack(null, null));
        Assert.False(TrackComparer.IsSameTrack(track, null));
        Assert.False(TrackComparer.IsSameTrack(null, track));
    }

    [Fact]
    public void IsSameTrack_MatchesBySourceIdOrFilePathOrId()
    {
        var a = new Track { SourceId = "netease:1", FilePath = "http://a/1.mp3", Id = "id-a" };
        var bySource = new Track { SourceId = "netease:1" };
        var byPath = new Track { FilePath = "http://a/1.mp3" };
        var byId = new Track { Id = "id-a" };
        var other = new Track { SourceId = "kuwo:9", FilePath = "http://b/9.mp3", Id = "id-b" };

        Assert.True(TrackComparer.IsSameTrack(a, bySource));
        Assert.True(TrackComparer.IsSameTrack(a, byPath));
        Assert.True(TrackComparer.IsSameTrack(a, byId));
        Assert.False(TrackComparer.IsSameTrack(a, other));
    }

    [Fact]
    public void IsSameTrackIdentity_RequiresBothSidesToHaveIdentifier()
    {
        var a = new Track { SourceId = "netease:1", FilePath = "http://a/1.mp3" };
        var sourceOnly = new Track { SourceId = "netease:1" };
        var pathOnly = new Track { FilePath = "http://a/1.mp3" };
        var empty = new Track();

        Assert.True(TrackComparer.IsSameTrackIdentity(a, sourceOnly));
        Assert.True(TrackComparer.IsSameTrackIdentity(a, pathOnly));
        // 单侧缺失对应字段时不得凭 Id 空值或单侧 SourceId 误判
        Assert.False(TrackComparer.IsSameTrackIdentity(a, empty));
        Assert.False(TrackComparer.IsSameTrackIdentity(sourceOnly, pathOnly));
    }

    // ---- MusicIdentity.IsSameMusicIdentity（精确：标点/书名号归一 + 歌手全等）----

    [Theory]
    [InlineData("晴天", "周杰伦", "晴天", "周杰伦", true)]
    [InlineData("晴天！", "周杰伦", "晴天", "周杰伦", true)]           // 标点归一
    [InlineData("《晴天》", "周杰伦", "晴天", "周杰伦", true)]         // 书名号归一
    [InlineData("SUNNY DAY", "Artist", "sunny day", "artist", true)]  // 大小写归一
    [InlineData("晴天", "周杰伦", "晴天", "方文山", false)]            // 歌手不同
    [InlineData("晴天(Live)", "周杰伦", "晴天", "周杰伦", false)]      // 括号保留：精确档不剥
    public void IsSameMusicIdentity_PreciseSemantics(
        string titleA, string artistA, string titleB, string artistB, bool expected)
    {
        Assert.Equal(expected, MusicIdentity.IsSameMusicIdentity(titleA, artistA, titleB, artistB));
    }

    [Fact]
    public void IsSameMusicIdentity_EmptyArtistMatchesAnyArtist()
    {
        Assert.True(MusicIdentity.IsSameMusicIdentity("晴天", "", "晴天", "周杰伦"));
    }

    // ---- MusicIdentity.IsSameSongLoose（宽松：括号/Live 剥离 + 歌手双向包含）----

    [Theory]
    [InlineData("晴天 (Live)", "周杰伦", "晴天(Live)", "周杰伦", true)]   // 标点/空格差异归一（live 字样保留）
    [InlineData("晴天", "周杰伦 & 方文山", "晴天", "周杰伦", true)]         // 歌手双向包含
    [InlineData("晴天(Live)", "周杰伦", "晴天", "周杰伦", false)]          // 宽松档不剥 live 字样
    [InlineData("晴天", "周杰伦", "阴天", "周杰伦", false)]                // 标题不同
    public void IsSameSongLoose_LooseSemantics(
        string titleA, string artistA, string titleB, string artistB, bool expected)
    {
        Assert.Equal(expected, MusicIdentity.IsSameSongLoose(titleA, artistA, titleB, artistB));
    }

    [Fact]
    public void NormalizeLoose_KeepsOnlyLettersAndDigits()
    {
        Assert.Equal("晴天live", MusicIdentity.NormalizeLoose("晴天 (Live)！！"));
        Assert.Equal("song123", MusicIdentity.NormalizeLoose("Song-123."));
        Assert.Equal("", MusicIdentity.NormalizeLoose("！！！"));
    }

    [Fact]
    public void IsSameSource_RequiresBothSidesAndIgnoresCase()
    {
        Assert.True(MusicIdentity.IsSameSource("netease:1", "NETEASE:1"));
        Assert.False(MusicIdentity.IsSameSource("netease:1", null));
        Assert.False(MusicIdentity.IsSameSource("", "netease:1"));
    }

    // ---- ProviderTrackRef（稳定音源身份：持久化与 Provider 契约共用）----

    [Fact]
    public void ProviderTrackRef_FromSourceId_ParsesAndRoundTrips()
    {
        var parsed = ProviderTrackRef.FromSourceId("netease:123");
        Assert.Equal(new ProviderTrackRef("netease", "123"), parsed);
        Assert.Equal("netease:123", parsed!.ToSourceId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noprefix")]
    [InlineData(":leading")]
    public void ProviderTrackRef_FromSourceId_ReturnsNullForUnprefixed(string? sourceId)
    {
        Assert.Null(ProviderTrackRef.FromSourceId(sourceId));
    }

    [Fact]
    public void ProviderTrackRef_ToSourceId_ToleratesMissingProvider()
    {
        // 无前缀的旧 ID 保存为 ProviderId 为空，还原时不捏造前缀
        Assert.Equal("raw-id", new ProviderTrackRef("", "raw-id").ToSourceId());
    }
}
