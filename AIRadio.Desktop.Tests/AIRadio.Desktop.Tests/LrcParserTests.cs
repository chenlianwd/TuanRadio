using System;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parse_StandardTimestamps_ProducesSortedLines()
    {
        var lines = LrcParser.Parse("[00:12.50]第一句\n[00:05.00]第二句先唱\n[01:02.25]副歌\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].Time);
        Assert.Equal("第二句先唱", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[1].Time);
        Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(2.25), lines[2].Time);
    }

    [Fact]
    public void Parse_MillisecondPrecision_HandlesOneTwoAndThreeDigits()
    {
        var lines = LrcParser.Parse("[00:01.5]a\n[00:02.50]b\n[00:03.500]c\n[00:04]d\n");

        Assert.Equal(4, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(2.5), lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(3.5), lines[2].Time);
        Assert.Equal(TimeSpan.FromSeconds(4), lines[3].Time);
    }

    [Fact]
    public void Parse_MultipleTimestampsOnOneLine_ExpandsToSeparateLines()
    {
        var lines = LrcParser.Parse("[00:10.00][01:30.00]重复句\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal("重复句", lines[0].Text);
        Assert.Equal("重复句", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(10), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(90), lines[1].Time);
    }

    [Fact]
    public void Parse_OffsetMetaTag_ShiftsAllTimestamps()
    {
        var lines = LrcParser.Parse("[offset:500]\n[00:10.00]句一\n[00:20.00]句二\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(10.5), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(20.5), lines[1].Time);
    }

    [Fact]
    public void Parse_OffsetWithPlusSign_ShiftsTimestamps()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]句一\n");

        Assert.Single(lines);
        Assert.Equal(TimeSpan.FromSeconds(10.5), lines[0].Time);
    }

    [Fact]
    public void Parse_SkipsMetadataTagsEmptyAndMalformedLines()
    {
        var lines = LrcParser.Parse(
            "[ti:歌名]\n[ar:歌手]\n[by:制作]\n\n[00:05.00]有效行\n无时间戳行\n[xx:yy]不是时间\n[   ]空\n");

        Assert.Single(lines);
        Assert.Equal("有效行", lines[0].Text);
    }

    [Fact]
    public void Parse_NullOrEmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse(""));
        Assert.Empty(LrcParser.Parse("   \n  \r\n"));
    }

    [Fact]
    public void Parse_NegativeOffset_ClampsToZero()
    {
        var lines = LrcParser.Parse("[offset:-3000]\n[00:01.00]句\n");

        Assert.Single(lines);
        Assert.Equal(TimeSpan.Zero, lines[0].Time);
    }

    [Fact]
    public void Parse_TimestampOnlyLineWithoutText_Skipped()
    {
        var lines = LrcParser.Parse("[00:05.00]\n[00:10.00]   \n[00:15.00]词\n");

        Assert.Single(lines);
        Assert.Equal("词", lines[0].Text);
    }

    [Fact]
    public void Parse_HandlesCrlfAndSurroundingWhitespace()
    {
        var lines = LrcParser.Parse("[00:05.00]  前后空格  \r\n");

        Assert.Single(lines);
        Assert.Equal("前后空格", lines[0].Text);
    }
}
