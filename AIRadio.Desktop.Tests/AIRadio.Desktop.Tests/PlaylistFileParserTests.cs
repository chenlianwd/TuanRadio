using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AIRadio.Desktop.Services.Music;
using Xunit;

namespace AIRadio.Desktop.Tests;

public sealed class PlaylistFileParserTests
{
    [Fact]
    public void ParseContent_ExtendedM3u_ExtractsTitleWithAttributesAndDeduplicates()
    {
        var m3u = """
            #EXTM3U
            #EXTINF:-1 tvg-id="1" group-title="Jazz", Smooth Jazz 24/7
            https://stream.example.com/jazz.mp3
            #EXTINF:-1, Duplicate Jazz
            https://stream.example.com/jazz.mp3/
            file:///C:/local/song.mp3
            relative/path.mp3
            #EXTINF:-1, Tokyo City Pop
            http://stream.example.com/citypop.aac
            """;

        var parser = new PlaylistFileParser();
        var result = parser.ParseContent(m3u);

        Assert.Equal(2, result.Stations.Count);
        Assert.Equal("Smooth Jazz 24/7", result.Stations[0].Name);
        Assert.Equal("https://stream.example.com/jazz.mp3", result.Stations[0].StreamUrl);
        Assert.Equal("MP3", result.Stations[0].Codec);
        Assert.True(result.Stations[0].IsCustom);

        Assert.Equal("Tokyo City Pop", result.Stations[1].Name);
        Assert.Equal("AAC", result.Stations[1].Codec);
        Assert.Equal(3, result.SkippedCount);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void ParseContent_Pls_HandlesCaseInsensitiveAndOutOfOrderKeys()
    {
        var pls = """
            [playlist]
            TITLE2=Second Station
            file1=https://stream.example.com/one.m3u8
            FILE2=http://stream.example.com/two.ogg
            Title1=First HLS Station
            NumberOfEntries=2
            Version=2
            """;

        var parser = new PlaylistFileParser();
        var result = parser.ParseContent(pls);

        Assert.Equal(2, result.Stations.Count);
        Assert.Equal("First HLS Station", result.Stations[0].Name);
        Assert.Equal("HLS", result.Stations[0].Codec);
        Assert.Equal("Second Station", result.Stations[1].Name);
        Assert.Equal("OGG", result.Stations[1].Codec);
    }

    [Fact]
    public void ParseContent_TruncatesAtMaxImportStations()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        for (var i = 1; i <= PlaylistFileParser.MaxImportStations + 20; i++)
        {
            sb.AppendLine($"#EXTINF:-1, Station {i}");
            sb.AppendLine($"https://stream.example.com/ch{i}.mp3");
        }

        var parser = new PlaylistFileParser();
        var result = parser.ParseContent(sb.ToString());

        Assert.Equal(PlaylistFileParser.MaxImportStations, result.Stations.Count);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task ParseFileAsync_HandlesGbkFallbackEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var tempFile = Path.Combine(Path.GetTempPath(), $"airadio_test_{Guid.NewGuid():N}.m3u");
        try
        {
            var gbk = Encoding.GetEncoding("GBK");
            var content = "#EXTM3U\r\n#EXTINF:-1,校园经典广播\r\nhttps://stream.example.com/campus.mp3\r\n";
            await File.WriteAllBytesAsync(tempFile, gbk.GetBytes(content));

            var parser = new PlaylistFileParser();
            var result = await parser.ParseFileAsync(tempFile);

            Assert.Single(result.Stations);
            Assert.Equal("https://stream.example.com/campus.mp3", result.Stations[0].StreamUrl);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseContent_PlsWithLeadingCommentsAndWhitespace_ParsesCorrectly()
    {
        var pls = """
            ; Exported from Foobar2000

              [playlist]
            File0 = http://192.168.1.5:8000/stream.mp3
            Title0 = NAS Radio
            """;

        var parser = new PlaylistFileParser();
        var result = parser.ParseContent(pls);

        Assert.Single(result.Stations);
        Assert.Equal("NAS Radio", result.Stations[0].Name);
        Assert.Equal("http://192.168.1.5:8000/stream.mp3", result.Stations[0].StreamUrl);
    }
}
