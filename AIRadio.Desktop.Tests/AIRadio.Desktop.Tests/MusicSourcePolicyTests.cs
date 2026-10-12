using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>
/// 全局源策略回归：分级表映射（含展示别名）、跨源回退尝试顺序按策略重排
/// （覆盖优先=用户序）、搜索评分源权重随策略翻转、设置页读写与 Broker 接线。
/// </summary>
public class MusicSourcePolicyTests
{
    [Fact]
    public void SourcePolicyClasses_MapIdsAliasesAndUnknownDefaults()
    {
        Assert.Equal(0, SourcePolicyClasses.GetClass("opensubsonic", MusicSourcePolicy.Stability));
        Assert.Equal(0, SourcePolicyClasses.GetClass("opensubsonic", MusicSourcePolicy.Quality));
        Assert.Equal(3, SourcePolicyClasses.GetClass("kugou", MusicSourcePolicy.Stability));
        Assert.Equal(1, SourcePolicyClasses.GetClass("kugou", MusicSourcePolicy.Quality));
        Assert.Equal(1, SourcePolicyClasses.GetClass("audius", MusicSourcePolicy.Stability));
        // 展示别名（CandidateRanker 历史口径）与未知源走中性默认
        Assert.Equal(2, SourcePolicyClasses.GetClass("网易", MusicSourcePolicy.Stability));
        Assert.Equal(2, SourcePolicyClasses.GetClass("本地曲库", MusicSourcePolicy.Quality));
        Assert.Equal(SourcePolicyClasses.DefaultStabilityClass,
            SourcePolicyClasses.GetClass("whatever", MusicSourcePolicy.Stability));
        Assert.Equal(SourcePolicyClasses.DefaultQualityClass,
            SourcePolicyClasses.GetClass(null, MusicSourcePolicy.Quality));
        // Coverage 不分级（沿用既有硬编码表）
        Assert.Equal(0, SourcePolicyClasses.GetClass("kugou", MusicSourcePolicy.Coverage));
    }

    [Fact]
    public async Task StabilityPolicy_ReordersFallbackToTryStableSourceFirst()
    {
        var kugou = new FallbackStubProvider("kugou");
        var audius = new FallbackStubProvider("audius");
        var broker = new MusicSourceBroker(kugou, audius) // 用户序：酷狗在前
        {
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new InstantOkHandler()))
        };
        broker.SourcePolicy = MusicSourcePolicy.Stability;

        var target = new OnlineTrack { Id = "netease:1", Title = "歌", Artist = "手", DurationMs = 200_000 };
        var url = await broker.GetAlternativePlayUrlAsync(target, CancellationToken.None);

        Assert.NotNull(url);
        Assert.Equal(1, audius.ResolveCalls); // 稳定类 1 先被解析
        Assert.Equal(0, kugou.ResolveCalls);  // 风控史源（稳定类 3）命中即停后不再尝试
        Assert.StartsWith("audius:", target.Id); // 身份回写为实际生效源
    }

    [Fact]
    public async Task CoveragePolicy_KeepsUserOrderInFallback()
    {
        var kugou = new FallbackStubProvider("kugou");
        var audius = new FallbackStubProvider("audius");
        var broker = new MusicSourceBroker(kugou, audius)
        {
            RedirectGuard = new PlaybackRedirectGuard(new HttpClient(new InstantOkHandler()))
        };
        // 默认 Coverage：按用户序（kugou 在前）命中即停

        var target = new OnlineTrack { Id = "netease:1", Title = "歌", Artist = "手", DurationMs = 200_000 };
        var url = await broker.GetAlternativePlayUrlAsync(target, CancellationToken.None);

        Assert.NotNull(url);
        Assert.Equal(1, kugou.ResolveCalls);
        Assert.Equal(0, audius.ResolveCalls);
        Assert.StartsWith("kugou:", target.Id); // 用户序（kugou 在前）保持
    }

    [Fact]
    public void SearchRanker_SourceWeightFlipsWithPolicy()
    {
        var kugou = new OnlineTrack { Id = "kugou:1", Title = "歌", Artist = "手", Source = "kugou" };
        var audius = new OnlineTrack { Id = "audius:1", Title = "歌", Artist = "手", Source = "audius" };

        // 覆盖优先：既有静态表 kugou(0.95) > audius(未知 0.80)
        var coverage = CandidateRanker.RankSearchResults(new[] { audius, kugou }, "歌 手").ToList();
        Assert.Equal("kugou:1", coverage[0].Id);

        // 稳定优先：audius(类 1 → 0.95) > kugou(类 3 → 0.85)，排序翻转
        var stability = CandidateRanker.RankSearchResults(
            new[] { audius, kugou }, "歌 手", MusicSourcePolicy.Stability).ToList();
        Assert.Equal("audius:1", stability[0].Id);
    }

    [Fact]
    public async Task SettingsRoundTrip_AppliesPolicyToBrokerAndPersists()
    {
        var broker = new MusicSourceBroker(new FallbackStubProvider("audius"));
        var settingsFile = Path.Combine(Path.GetTempPath(), "AIRadio.Tests",
            $"policy-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        var vm = new SettingsViewModel(
            new Mock<ILLMService>().Object,
            new Mock<ISecureStorage>().Object,
            settingsFile,
            musicSearch: broker);
        try
        {
            Assert.Equal(MusicSourcePolicy.Coverage, broker.SourcePolicy); // 默认

            vm.SelectedMusicSourcePolicy = "stability";
            Assert.Equal(MusicSourcePolicy.Stability, broker.SourcePolicy); // 订阅即时生效

            await vm.SaveUiStateCommand.Execute().FirstAsync();
            var saved = await File.ReadAllTextAsync(settingsFile);
            Assert.Contains("\"music_source_policy\": \"stability\"", saved);
        }
        finally
        {
            vm.Dispose();
            try { File.Delete(settingsFile); } catch (IOException) { }
        }
    }

    /// <summary>搜索固定返回与目标同名同歌手的候选，解析返回 TEST-NET 直链。</summary>
    private sealed class FallbackStubProvider(string id) : IMusicProvider
    {
        public int ResolveCalls { get; private set; }
        public MusicProviderDescriptor Descriptor { get; } =
            new(id, id, ProviderNetworkScope.PublicInternet);

        public Task<List<OnlineTrack>> SearchAsync(string keyword, int limit, CancellationToken cancellationToken)
            => Task.FromResult(new List<OnlineTrack>
            {
                new() { Id = id + ":1", Title = "歌", Artist = "手", Source = id }
            });

        public Task<MediaResolutionResult> ResolveAsync(
            ProviderTrackRef trackRef,
            IReadOnlyDictionary<string, string>? providerMetadata,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(MediaResolutionResult.Playable(
                new ResolvedMedia(trackRef, new Uri("https://203.0.113.70/stream.mp3"))));
        }
    }

    private sealed class InstantOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
