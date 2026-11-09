using System;
using System.Collections.Generic;
using System.Globalization;
using AIRadio.Desktop.Services;

namespace AIRadio.Desktop.Models;

/// <summary>
/// 全球开源网络电台实体（Radio-Browser API 开放数据源）。
/// </summary>
public sealed class RadioStation : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsFavorite)));
            }
        }
    }

    public const string SourcePrefix = "radio-browser:";

    public string StationUuid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string StreamUrl { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string CountryCode { get; init; } = string.Empty;
    public string Codec { get; init; } = "MP3";
    public int Bitrate { get; init; }
    public int Votes { get; init; }
    public int ClickCount { get; init; }

    public string DisplayCountryAndTags =>
        string.IsNullOrWhiteSpace(Country)
            ? Tags
            : string.IsNullOrWhiteSpace(Tags) ? Country : $"{Country} · {Tags}";

    public string DisplayQuality =>
        Bitrate > 0 && !string.IsNullOrWhiteSpace(Codec)
            ? $"{Codec.ToUpperInvariant()} {Bitrate}k"
            : !string.IsNullOrWhiteSpace(Codec)
                ? Codec.ToUpperInvariant()
                : Bitrate > 0 ? $"{Bitrate}k" : "LIVE";

    /// <summary>
    /// 将电台转换为播放列表与收藏夹通用的 Track 模型。
    /// 直播电台特征：Duration = TimeSpan.Zero，SourceId 携带 radio-browser: 前缀，
    /// ProviderMetadata 声明 is_live=true 与固定流地址。
    /// </summary>
    public Track ToTrack()
    {
        var sourceId = SourcePrefix + StationUuid;
        return new Track
        {
            Id = sourceId,
            Title = Name,
            Artist = DisplayCountryAndTags,
            Album = AppLanguage.T("网络电台", "Internet Radio"),
            Duration = TimeSpan.Zero,
            FilePath = StreamUrl,
            SourceId = sourceId,
            DeclaredCodec = string.IsNullOrWhiteSpace(Codec) ? null : Codec.ToUpperInvariant(),
            DeclaredBitrateKbps = Bitrate > 0 ? Bitrate : null,
            IsFavorite = IsFavorite,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
            ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["station_uuid"] = StationUuid,
                ["stream_url"] = StreamUrl,
                ["country"] = Country,
                ["tags"] = Tags,
                ["codec"] = Codec,
                ["bitrate"] = Bitrate > 0 ? Bitrate.ToString(CultureInfo.InvariantCulture) : string.Empty,
                ["is_live"] = "true"
            }
        };
    }
}
