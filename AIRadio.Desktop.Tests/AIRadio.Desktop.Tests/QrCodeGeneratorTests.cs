using System;
using AIRadio.Desktop.Services.WebRemote;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class QrCodeGeneratorTests
{
    [Theory]
    [InlineData("http://192.168.1.25:37260/?token=AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("http://10.0.0.5:37260/?token=short")]
    [InlineData("https://tuanradio-host.local:37260/?token=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Encode_ProducesValidFinderTimingAndDarkModuleGeometry(string payload)
    {
        var matrix = QrCodeGenerator.Encode(payload);
        var size = matrix.GetLength(0);
        Assert.Equal(matrix.GetLength(1), size);
        Assert.True(size >= 21 && (size - 17) % 4 == 0);
        var version = (size - 17) / 4;

        // 三个角落 7x7 Finder 外框与中心 3x3 必为暗块
        AssertFinder(matrix, 0, 0);
        AssertFinder(matrix, 0, size - 7);
        AssertFinder(matrix, size - 7, 0);

        // 第 6 行/列 Timing Pattern 必黑白交替（偶下标暗、奇下标亮）
        for (var i = 8; i <= size - 9; i++)
        {
            Assert.Equal(i % 2 == 0, matrix[6, i]);
            Assert.Equal(i % 2 == 0, matrix[i, 6]);
        }

        // ISO/IEC 18004 固定暗块 (4*V + 9, 8)
        Assert.True(matrix[4 * version + 9, 8]);

        // ISO/IEC 18004 格式信息双副本须一致且通过 BCH(15,5) 校验（ECC M = 0b00）
        var copy1 = 0;
        for (var i = 0; i <= 5; i++)
            if (matrix[i, 8]) copy1 |= 1 << i;
        if (matrix[7, 8]) copy1 |= 1 << 6;
        if (matrix[8, 8]) copy1 |= 1 << 7;
        if (matrix[8, 7]) copy1 |= 1 << 8;
        for (var i = 9; i < 15; i++)
            if (matrix[8, 14 - i]) copy1 |= 1 << i;

        var copy2 = 0;
        for (var i = 0; i < 8; i++)
            if (matrix[8, size - 1 - i]) copy2 |= 1 << i;
        for (var i = 8; i < 15; i++)
            if (matrix[size - 15 + i, 8]) copy2 |= 1 << i;

        Assert.Equal(copy1, copy2);

        var unmasked = copy1 ^ 0x5412;
        var eccLevel = (unmasked >> 13) & 0b11;
        Assert.Equal(0b00, eccLevel); // Level M
        var data = (unmasked >> 10) & 0b11111;
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        Assert.Equal(((data << 10) | rem), unmasked);
    }

    [Fact]
    public void Encode_TooLongPayload_ThrowsArgumentException()
    {
        var tooLong = new string('a', 181);
        Assert.Throws<ArgumentException>(() => QrCodeGenerator.Encode(tooLong));
    }

    [AvaloniaFact]
    public void RenderBitmap_ProducesCrispQuietZoneAndDarkModules()
    {
        const string url = "http://192.168.1.10:37260/?token=TestToken123456";
        var matrix = QrCodeGenerator.Encode(url);
        var modules = matrix.GetLength(0);
        const int scale = 4;
        const int quiet = 4;

        var expectedPx = (modules + quiet * 2) * scale;
        var buf = QrCodeGenerator.RenderBgraPixels(url, out var pixelSize, modulePixels: scale, quietModules: quiet);
        Assert.Equal(expectedPx, pixelSize);

        // 左上静默白边像素 (0, 0) 应为纯白 (#FFFFFFFF)
        Assert.Equal(255, buf[0]);
        Assert.Equal(255, buf[1]);
        Assert.Equal(255, buf[2]);
        Assert.Equal(255, buf[3]);

        // Finder 左上角第一个模块 (quiet*scale, quiet*scale) 应为深色 (#111111)
        var offset = (quiet * scale * pixelSize * 4) + (quiet * scale * 4);
        Assert.Equal(17, buf[offset]);
        Assert.Equal(17, buf[offset + 1]);
        Assert.Equal(17, buf[offset + 2]);

        using var bmp = QrCodeGenerator.RenderBitmap(url, modulePixels: scale, quietModules: quiet);
        Assert.Equal(expectedPx, bmp.PixelSize.Width);
        Assert.Equal(expectedPx, bmp.PixelSize.Height);
    }

    private static void AssertFinder(bool[,] m, int top, int left)
    {
        for (var r = 0; r < 7; r++)
        {
            for (var c = 0; c < 7; c++)
            {
                var onBorder = r == 0 || r == 6 || c == 0 || c == 6;
                var inCore = r >= 2 && r <= 4 && c >= 2 && c <= 4;
                Assert.Equal(onBorder || inCore, m[top + r, left + c]);
            }
        }
    }
}
