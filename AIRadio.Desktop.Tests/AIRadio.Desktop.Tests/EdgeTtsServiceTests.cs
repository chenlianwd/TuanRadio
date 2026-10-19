using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class EdgeTtsServiceTests
{
    // 向量锚定 edge-tts（GPL-3.0）drm.py generate_sec_ms_gec_v2 的实际输出（2026-10-16 核对，
    // 该算法实现已用上游 Python 本体实机握手 101 通过）。v2 令牌 = "CS64(16hex)-SHA256变体(64hex)"。
    [Theory]
    [InlineData(1792200000, "3FEE85BA91F5659F-208F9842BF097D2CEB54190A308C5B353EA9A86DCF57BA52447E4B5676147133")]
    [InlineData(1792200299, "3FEE85BA91F5659F-208F9842BF097D2CEB54190A308C5B353EA9A86DCF57BA52447E4B5676147133")]
    [InlineData(1792200300, "39AD7E8E666B3188-618521B694AE3D094DB78E4E98F3A03527CD371DB21B039DF1DC5EE592782A9C")]
    [InlineData(1600000000, "1F5E89103EC98A08-E69D008C1241797258F0EE06F56044C11FAEC4B60F95C6AB0DB3C5E79BE2F010")]
    public void GenerateSecMsGec_MatchesV2ReferenceVectors(long unixSeconds, string expected)
    {
        var token = EdgeTtsService.GenerateSecMsGec(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, token);
    }

    [Fact]
    public void GenerateSecMsGec_IsStableWithinFiveMinuteWindow()
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(1792200000);

        var first = EdgeTtsService.GenerateSecMsGec(time);
        var sameWindow = EdgeTtsService.GenerateSecMsGec(time.AddSeconds(299));
        var nextWindow = EdgeTtsService.GenerateSecMsGec(time.AddSeconds(300));

        Assert.Equal(16 + 1 + 64, first.Length);
        Assert.Equal(first, sameWindow);
        Assert.NotEqual(first, nextWindow);
    }

    [Theory]
    [InlineData("female-shaonv", "zh-CN-XiaoxiaoNeural")]
    [InlineData("male-qn-jingying", "zh-CN-YunjianNeural")]
    [InlineData("zh-CN-XiaoyiNeural", "zh-CN-XiaoyiNeural")]
    [InlineData("", "zh-CN-XiaoxiaoNeural")]
    public void ResolveVoice_MapsLegacyIds(string voiceId, string expected)
    {
        Assert.Equal(expected, EdgeTtsService.ResolveVoice(voiceId));
    }

    [Fact]
    public void BuildSsml_UsesSupportedProsodyAndEscapesText()
    {
        var ssml = EdgeTtsService.BuildSsml("A&B<测试>", "zh-CN-XiaoxiaoNeural");

        Assert.Contains("<prosody", ssml);
        Assert.DoesNotContain("mstts:express-as", ssml);
        Assert.Contains("A&amp;B&lt;测试&gt;", ssml);
    }

    [Theory]
    [InlineData("happy", "pitch='+2Hz'", "rate='+8%'", "volume='+0%'")]
    [InlineData("sad", "pitch='-2Hz'", "rate='-8%'", "volume='-5%'")]
    [InlineData("unknown", "pitch='+0Hz'", "rate='+0%'", "volume='+0%'")]
    public void BuildSsml_MapsEmotionToSupportedProsody(
        string emotion,
        string expectedPitch,
        string expectedRate,
        string expectedVolume)
    {
        var ssml = EdgeTtsService.BuildSsml("测试", "zh-CN-XiaoxiaoNeural", emotion);

        Assert.Contains(expectedPitch, ssml);
        Assert.Contains(expectedRate, ssml);
        Assert.Contains(expectedVolume, ssml);
    }

    [Fact]
    public void BuildConfigMessage_UsesCurrentSynthesisEnvelope()
    {
        var message = EdgeTtsService.BuildConfigMessage();

        Assert.Contains("Path:speech.config", message);
        Assert.Contains("\"context\":{\"synthesis\":{\"audio\"", message);
        Assert.Contains("audio-24khz-48kbitrate-mono-mp3", message);
    }

    [Fact]
    public async Task Dispose_CancelsConcurrentVoiceLoadsWithoutOverReleasingGate()
    {
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new BlockingHandler(requestStarted));
        using var service = new EdgeTtsService(client);

        var first = service.GetVoicesAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.GetVoicesAsync();

        service.Dispose();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, Assert.Empty);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _requestStarted;

        public BlockingHandler(TaskCompletionSource<bool> requestStarted)
        {
            _requestStarted = requestStarted;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
