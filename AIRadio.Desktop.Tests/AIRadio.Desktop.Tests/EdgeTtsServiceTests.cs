using System;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class EdgeTtsServiceTests
{
    [Fact]
    public void GenerateSecMsGec_IsStableWithinFiveMinuteWindow()
    {
        var time = new DateTimeOffset(2026, 7, 10, 11, 32, 10, TimeSpan.Zero);

        var first = EdgeTtsService.GenerateSecMsGec(time);
        var sameWindow = EdgeTtsService.GenerateSecMsGec(time.AddMinutes(2));
        var nextWindow = EdgeTtsService.GenerateSecMsGec(time.AddMinutes(5));

        Assert.Equal(64, first.Length);
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
}
