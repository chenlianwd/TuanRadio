using System.Collections.Generic;

namespace AIRadio.Desktop.Services;

/// <summary>
/// LibVLC 音频轨道 fourcc 到展示用短名的映射。fourcc 按 VLC_FOURCC('a','b','c','d')
/// 小端打包为 uint，还原为字符串后查表（尾部填充空格已去除）；未识别返回 null 由调用方回退。
/// </summary>
public static class MediaCodecNames
{
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["mpga"] = "MP3",
        ["mp3"] = "MP3",
        ["mp4a"] = "AAC",
        ["aac"] = "AAC",
        ["a52"] = "AC3",
        ["eac3"] = "EAC3",
        ["flac"] = "FLAC",
        ["opus"] = "OPUS",
        ["vorb"] = "VORBIS",
        ["alac"] = "ALAC",
        ["wmav"] = "WMA",
        ["wma1"] = "WMA",
        ["wma2"] = "WMA",
        ["dts"] = "DTS",
        ["dtsh"] = "DTS",
        ["mlpa"] = "MLP",
        // PCM 家族：WAV/AIFF 常见采样封装
        ["araw"] = "PCM",
        ["twos"] = "PCM",
        ["s8"] = "PCM",
        ["u8"] = "PCM",
        ["s16l"] = "PCM",
        ["s16b"] = "PCM",
        ["s24l"] = "PCM",
        ["s24b"] = "PCM",
        ["s32l"] = "PCM",
        ["s32b"] = "PCM",
        ["fl32"] = "PCM",
        ["fl64"] = "PCM",
        ["ulaw"] = "PCM",
        ["alaw"] = "PCM",
    };

    public static string? ToShortName(uint fourcc)
    {
        var key = new string(new[]
        {
            (char)(fourcc & 0xFF),
            (char)((fourcc >> 8) & 0xFF),
            (char)((fourcc >> 16) & 0xFF),
            (char)((fourcc >> 24) & 0xFF)
        }).TrimEnd(' ');
        return Map.TryGetValue(key, out var name) ? name : null;
    }
}
