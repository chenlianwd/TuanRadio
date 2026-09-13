using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AIRadio.Desktop.Services;

/// <summary>
/// 标准 LRC 文本解析：[mm:ss.xx] 行时间戳，一行多时间戳展开，offset 元标签应用。
/// 只负责解析（结果可为空列表）；"空列表视为无词"是 LyricService 的服务层语义。
/// </summary>
internal static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    private static readonly Regex OffsetRegex = new(@"^\[offset:\s*([+-]?\d+)\s*]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<LyricLine> Parse(string? lrc)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
            return lines;

        long offsetMs = 0;
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            var offsetMatch = OffsetRegex.Match(line);
            if (offsetMatch.Success)
            {
                offsetMs = long.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var timestamps = TimestampRegex().Matches(line);
            if (timestamps.Count == 0)
                continue; // 元标签（[ti:] 等）与纯文本行不带时间戳，跳过

            // 行内去掉全部时间戳后的剩余部分；全部时间戳共用同一文本（一行多时间戳展开）
            var text = TimestampRegex().Replace(line, "").Trim();
            if (text.Length == 0)
                continue;

            foreach (Match stamp in timestamps)
            {
                var minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = stamp.Groups[3].Success ? stamp.Groups[3].Value : "0";
                // "5" => 500ms（十分位）、"50" => 500ms（百分位）、"500" => 500ms（千分位）：按位数补齐
                var ms = fraction.PadRight(3, '0');
                var time = new TimeSpan(0, 0, minutes, seconds, int.Parse(ms, CultureInfo.InvariantCulture)) + TimeSpan.FromMilliseconds(offsetMs);
                if (time < TimeSpan.Zero)
                    time = TimeSpan.Zero;
                lines.Add(new LyricLine(time, text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }
}
