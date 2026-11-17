using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AIRadio.Desktop.Services.WebRemote;

/// <summary>
/// 纯 C# ISO/IEC 18004 QR 码生成器（Byte 模式、ECC Level M、Version 1–9，最大 180 UTF-8 字节），
/// 零第三方依赖，直接输出布尔矩阵或高对比度静默白边 WriteableBitmap 供桌面 UI 扫码展示。
/// </summary>
public static class QrCodeGenerator
{
    private readonly record struct VersionSpec(
        int Version,
        int DataCodewords,
        int EccPerBlock,
        int Group1Blocks,
        int Group1DataCodewords,
        int Group2Blocks,
        int Group2DataCodewords,
        int[] AlignmentCenters);

    // ISO/IEC 18004 Table 9 (ECC Level M, Versions 1..9) + Table E.1 Alignment Pattern centers
    private static readonly VersionSpec[] Specs =
    [
        new(1, 16, 10, 1, 16, 0, 0, []),
        new(2, 28, 16, 1, 28, 0, 0, [6, 18]),
        new(3, 44, 26, 1, 44, 0, 0, [6, 22]),
        new(4, 64, 18, 2, 32, 0, 0, [6, 26]),
        new(5, 86, 24, 2, 43, 0, 0, [6, 30]),
        new(6, 108, 16, 4, 27, 0, 0, [6, 34]),
        new(7, 124, 18, 4, 31, 0, 0, [6, 22, 38]),
        new(8, 154, 22, 2, 38, 2, 39, [6, 24, 42]),
        new(9, 182, 22, 3, 36, 2, 37, [6, 26, 46]),
    ];

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCodeGenerator()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++)
            GfExp[i] = GfExp[i - 255];
    }

    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var spec = SelectVersion(bytes.Length);
        var dataCodewords = BuildDataCodewords(bytes, spec);
        var finalBytes = InterleaveBlocks(dataCodewords, spec);

        var size = 17 + spec.Version * 4;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, spec, size);
        PlaceCodewords(modules, isFunction, finalBytes, size);

        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        var trial = new bool[size, size];

        for (var mask = 0; mask < 8; mask++)
        {
            Array.Copy(modules, trial, modules.Length);
            ApplyMask(trial, isFunction, mask, size);
            DrawFormatInfo(trial, mask, size);
            var penalty = ComputePenalty(trial, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
        }

        ApplyMask(modules, isFunction, bestMask, size);
        DrawFormatInfo(modules, bestMask, size);
        return modules;
    }

    public static byte[] RenderBgraPixels(string text, out int pixelSize, int modulePixels = 5, int quietModules = 4)
    {
        var matrix = Encode(text);
        var count = matrix.GetLength(0);
        var totalModules = count + quietModules * 2;
        pixelSize = totalModules * modulePixels;

        var buffer = new byte[pixelSize * pixelSize * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 255;     // B
            buffer[i + 1] = 255; // G
            buffer[i + 2] = 255; // R
            buffer[i + 3] = 255; // A
        }

        for (var row = 0; row < count; row++)
        {
            for (var col = 0; col < count; col++)
            {
                if (!matrix[row, col]) continue;
                var yStart = (row + quietModules) * modulePixels;
                var xStart = (col + quietModules) * modulePixels;
                for (var dy = 0; dy < modulePixels; dy++)
                {
                    var rowOffset = (yStart + dy) * pixelSize;
                    for (var dx = 0; dx < modulePixels; dx++)
                    {
                        var idx = (rowOffset + xStart + dx) * 4;
                        buffer[idx] = 17;     // B (#111111 crisp dark ink)
                        buffer[idx + 1] = 17; // G
                        buffer[idx + 2] = 17; // R
                        buffer[idx + 3] = 255;
                    }
                }
            }
        }

        return buffer;
    }

    public static WriteableBitmap RenderBitmap(string text, int modulePixels = 5, int quietModules = 4)
    {
        var buffer = RenderBgraPixels(text, out var pixelSize, modulePixels, quietModules);
        var bitmap = new WriteableBitmap(
            new PixelSize(pixelSize, pixelSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var fb = bitmap.Lock();
        var srcStride = pixelSize * 4;
        for (var y = 0; y < pixelSize; y++)
            Marshal.Copy(buffer, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
        return bitmap;
    }

    private static VersionSpec SelectVersion(int byteLength)
    {
        // 4-bit mode + 8-bit length + 4-bit minimum terminator overhead uses at most 2 codewords header
        foreach (var spec in Specs)
        {
            var availableBits = spec.DataCodewords * 8;
            var neededBits = 4 + 8 + byteLength * 8;
            if (neededBits <= availableBits)
                return spec;
        }
        throw new ArgumentException($"QR payload too long ({byteLength} bytes; max 180 bytes).", nameof(byteLength));
    }

    private static byte[] BuildDataCodewords(byte[] payload, VersionSpec spec)
    {
        var totalBits = spec.DataCodewords * 8;
        var bits = new List<bool>(totalBits);

        void AppendBits(int value, int bitCount)
        {
            for (var i = bitCount - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);
        }

        // Byte mode = 0100, 8-bit length (valid for Versions 1..9)
        AppendBits(0b0100, 4);
        AppendBits(payload.Length, 8);
        foreach (var b in payload)
            AppendBits(b, 8);

        var terminator = Math.Min(4, totalBits - bits.Count);
        for (var i = 0; i < terminator; i++)
            bits.Add(false);

        while (bits.Count % 8 != 0)
            bits.Add(false);

        var codewords = new byte[spec.DataCodewords];
        var byteCount = bits.Count / 8;
        for (var i = 0; i < byteCount; i++)
        {
            byte val = 0;
            for (var b = 0; b < 8; b++)
                if (bits[i * 8 + b]) val |= (byte)(1 << (7 - b));
            codewords[i] = val;
        }

        var padA = true;
        for (var i = byteCount; i < codewords.Length; i++)
        {
            codewords[i] = padA ? (byte)0xEC : (byte)0x11;
            padA = !padA;
        }

        return codewords;
    }

    private static byte[] InterleaveBlocks(byte[] data, VersionSpec spec)
    {
        var totalBlocks = spec.Group1Blocks + spec.Group2Blocks;
        var dataBlocks = new byte[totalBlocks][];
        var eccBlocks = new byte[totalBlocks][];
        var divisor = BuildRsGenerator(spec.EccPerBlock);

        var offset = 0;
        var blockIndex = 0;
        for (var i = 0; i < spec.Group1Blocks; i++, blockIndex++)
        {
            dataBlocks[blockIndex] = new byte[spec.Group1DataCodewords];
            Array.Copy(data, offset, dataBlocks[blockIndex], 0, spec.Group1DataCodewords);
            eccBlocks[blockIndex] = ComputeRsRemainder(dataBlocks[blockIndex], divisor);
            offset += spec.Group1DataCodewords;
        }
        for (var i = 0; i < spec.Group2Blocks; i++, blockIndex++)
        {
            dataBlocks[blockIndex] = new byte[spec.Group2DataCodewords];
            Array.Copy(data, offset, dataBlocks[blockIndex], 0, spec.Group2DataCodewords);
            eccBlocks[blockIndex] = ComputeRsRemainder(dataBlocks[blockIndex], divisor);
            offset += spec.Group2DataCodewords;
        }

        var maxDataLen = Math.Max(spec.Group1DataCodewords, spec.Group2DataCodewords);
        var result = new List<byte>(data.Length + totalBlocks * spec.EccPerBlock);
        for (var col = 0; col < maxDataLen; col++)
            for (var b = 0; b < totalBlocks; b++)
                if (col < dataBlocks[b].Length)
                    result.Add(dataBlocks[b][col]);

        for (var col = 0; col < spec.EccPerBlock; col++)
            for (var b = 0; b < totalBlocks; b++)
                result.Add(eccBlocks[b][col]);

        return result.ToArray();
    }

    private static byte[] BuildRsGenerator(int degree)
    {
        var poly = new byte[degree];
        poly[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                poly[j] = GfMul(poly[j], (byte)root);
                if (j + 1 < degree)
                    poly[j] ^= poly[j + 1];
            }
            root = GfMul((byte)root, 0x02);
        }
        return poly;
    }

    private static byte[] ComputeRsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < divisor.Length; i++)
                result[i] ^= GfMul(divisor[i], factor);
        }
        return result;
    }

    private static byte GfMul(byte x, byte y)
    {
        if (x == 0 || y == 0) return 0;
        return GfExp[GfLog[x] + GfLog[y]];
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, VersionSpec spec, int size)
    {
        // Timing patterns
        for (var i = 0; i < size; i++)
        {
            var dark = (i % 2) == 0;
            SetFunctionModule(modules, isFunction, 6, i, dark);
            SetFunctionModule(modules, isFunction, i, 6, dark);
        }

        // Three finder patterns + 1-module white separator
        DrawFinder(modules, isFunction, 0, 0, size);
        DrawFinder(modules, isFunction, 0, size - 7, size);
        DrawFinder(modules, isFunction, size - 7, 0, size);

        // Alignment patterns
        var centers = spec.AlignmentCenters;
        for (var i = 0; i < centers.Length; i++)
        {
            for (var j = 0; j < centers.Length; j++)
            {
                if ((i == 0 && j == 0) ||
                    (i == 0 && j == centers.Length - 1) ||
                    (i == centers.Length - 1 && j == 0))
                    continue;
                DrawAlignment(modules, isFunction, centers[i], centers[j]);
            }
        }

        // Dark module & reserved format areas
        SetFunctionModule(modules, isFunction, 4 * spec.Version + 9, 8, true);
        ReserveFormatAreas(isFunction, size);

        if (spec.Version >= 7)
            DrawVersionInfo(modules, isFunction, spec.Version, size);
    }

    private static void DrawFinder(bool[,] modules, bool[,] isFunction, int top, int left, int size)
    {
        for (var r = -1; r <= 7; r++)
        {
            for (var c = -1; c <= 7; c++)
            {
                var rr = top + r;
                var cc = left + c;
                if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                var dist = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
                var dark = dist != 2 && dist != 4;
                SetFunctionModule(modules, isFunction, rr, cc, dark);
            }
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int centerRow, int centerCol)
    {
        for (var dr = -2; dr <= 2; dr++)
        {
            for (var dc = -2; dc <= 2; dc++)
            {
                var dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                SetFunctionModule(modules, isFunction, centerRow + dr, centerCol + dc, dist != 1);
            }
        }
    }

    private static void ReserveFormatAreas(bool[,] isFunction, int size)
    {
        for (var i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                isFunction[8, i] = true;
                isFunction[i, 8] = true;
            }
        }
        for (var i = 0; i < 8; i++)
            isFunction[8, size - 1 - i] = true;
        for (var i = 0; i < 7; i++)
            isFunction[size - 1 - i, 8] = true;
    }

    private static void DrawVersionInfo(bool[,] modules, bool[,] isFunction, int version, int size)
    {
        var rem = version;
        for (var i = 0; i < 12; i++)
            rem = (rem << 1) ^ (((rem >> 11) & 1) * 0x1F25);
        var bits = (version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0;
            var a = size - 11 + (i % 3);
            var b = i / 3;
            SetFunctionModule(modules, isFunction, a, b, dark);
            SetFunctionModule(modules, isFunction, b, a, dark);
        }
    }

    private static void DrawFormatInfo(bool[,] modules, int mask, int size)
    {
        // ECC Level M = 0b00
        var data = (0b00 << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        var bits = ((data << 10) | rem) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            modules[i, 8] = ((bits >> i) & 1) != 0;
        modules[7, 8] = ((bits >> 6) & 1) != 0;
        modules[8, 8] = ((bits >> 7) & 1) != 0;
        modules[8, 7] = ((bits >> 8) & 1) != 0;
        for (var i = 9; i < 15; i++)
            modules[8, 14 - i] = ((bits >> i) & 1) != 0;

        for (var i = 0; i < 8; i++)
            modules[8, size - 1 - i] = ((bits >> i) & 1) != 0;
        for (var i = 8; i < 15; i++)
            modules[size - 15 + i, 8] = ((bits >> i) & 1) != 0;
    }

    private static void PlaceCodewords(bool[,] modules, bool[,] isFunction, byte[] data, int size)
    {
        var bitIdx = 0;
        var totalBits = data.Length * 8;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (!isFunction[y, x] && bitIdx < totalBits)
                    {
                        var dark = ((data[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        modules[y, x] = dark;
                        bitIdx++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask, int size)
    {
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (isFunction[r, c]) continue;
                var invert = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => (r * c) % 2 + (r * c) % 3 == 0,
                    6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
                    7 => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
                    _ => false
                };
                if (invert)
                    modules[r, c] = !modules[r, c];
            }
        }
    }

    private static int ComputePenalty(bool[,] modules, int size)
    {
        var penalty = 0;

        // Rule 1: 5+ same-color consecutive modules horizontally/vertically
        for (var r = 0; r < size; r++)
        {
            var runColor = modules[r, 0];
            var runLen = 1;
            for (var c = 1; c < size; c++)
            {
                if (modules[r, c] == runColor) runLen++;
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runColor = modules[r, c];
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }
        for (var c = 0; c < size; c++)
        {
            var runColor = modules[0, c];
            var runLen = 1;
            for (var r = 1; r < size; r++)
            {
                if (modules[r, c] == runColor) runLen++;
                else
                {
                    if (runLen >= 5) penalty += 3 + (runLen - 5);
                    runColor = modules[r, c];
                    runLen = 1;
                }
            }
            if (runLen >= 5) penalty += 3 + (runLen - 5);
        }

        // Rule 2: 2x2 blocks of same color
        for (var r = 0; r < size - 1; r++)
            for (var c = 0; c < size - 1; c++)
                if (modules[r, c] == modules[r, c + 1] &&
                    modules[r, c] == modules[r + 1, c] &&
                    modules[r, c] == modules[r + 1, c + 1])
                    penalty += 3;

        // Rule 3: finder-like pattern 1:1:3:1:1 with 4 white before/after
        static int CheckPattern(Func<int, bool> get, int len)
        {
            var count = 0;
            for (var i = 0; i <= len - 11; i++)
            {
                var p1 = !get(i) && !get(i + 1) && !get(i + 2) && !get(i + 3) &&
                         get(i + 4) && !get(i + 5) && get(i + 6) && get(i + 7) &&
                         get(i + 8) && !get(i + 9) && get(i + 10);
                var p2 = get(i) && !get(i + 1) && get(i + 2) && get(i + 3) &&
                         get(i + 4) && !get(i + 5) && get(i + 6) &&
                         !get(i + 7) && !get(i + 8) && !get(i + 9) && !get(i + 10);
                if (p1 || p2) count += 40;
            }
            return count;
        }
        for (var r = 0; r < size; r++)
            penalty += CheckPattern(c => modules[r, c], size);
        for (var c = 0; c < size; c++)
            penalty += CheckPattern(r => modules[r, c], size);

        // Rule 4: balance of dark modules
        var darkCount = 0;
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
                if (modules[r, c]) darkCount++;
        var percent = darkCount * 100.0 / (size * size);
        var prev5 = (int)(Math.Floor(percent / 5.0) * 5);
        var next5 = (int)(Math.Ceiling(percent / 5.0) * 5);
        penalty += Math.Min(Math.Abs(prev5 - 50) / 5, Math.Abs(next5 - 50) / 5) * 10;

        return penalty;
    }

    private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int row, int col, bool dark)
    {
        modules[row, col] = dark;
        isFunction[row, col] = true;
    }
}
