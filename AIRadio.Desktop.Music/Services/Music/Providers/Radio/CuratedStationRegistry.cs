using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 公开网络电台高可用兜底库。
/// 当 Radio-Browser 元数据 API 集群不可达或请求超时时，提供主流流派的权威公开广播流种子，保障基础收听可用。
/// 注意：流地址仍为在线 HTTP/HTTPS 音频流，需网络连接可达方能播发。
/// </summary>
public static class CuratedStationRegistry
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RadioStation>> StationsByCategory =
        new Dictionary<string, IReadOnlyList<RadioStation>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lofi"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-lofi-1",
                    Name = "Lofi Girl Radio (Chill Beats)",
                    StreamUrl = "https://play.streamafrica.net/lofigirl",
                    Tags = "lofi, chillhop, study beats",
                    Country = "France",
                    CountryCode = "FR",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-lofi-2",
                    Name = "SomaFM Beat Blender",
                    StreamUrl = "https://ice2.somafm.com/beatblender-128-mp3",
                    Tags = "downtempo, chill, electronic",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["jazz"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-jazz-1",
                    Name = "Radio Swiss Jazz",
                    StreamUrl = "https://stream.srg-ssr.ch/m/rsj/mp3_128",
                    Tags = "jazz, blues, swing",
                    Country = "Switzerland",
                    CountryCode = "CH",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-jazz-2",
                    Name = "SomaFM Secret Agent",
                    StreamUrl = "https://ice2.somafm.com/secretagent-128-mp3",
                    Tags = "spy, lounge, jazz",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["classical"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-classical-1",
                    Name = "Radio Swiss Classic",
                    StreamUrl = "https://stream.srg-ssr.ch/m/rsc_de/mp3_128",
                    Tags = "classical, baroque, piano",
                    Country = "Switzerland",
                    CountryCode = "CH",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-classical-2",
                    Name = "Calm Radio Classical",
                    StreamUrl = "http://streams.calmradio.com:2128/stream",
                    Tags = "classical, relaxing, focus",
                    Country = "Canada",
                    CountryCode = "CA",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["synthwave"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-synth-1",
                    Name = "Nightride FM",
                    StreamUrl = "https://stream.nightride.fm/nightride.mp3",
                    Tags = "synthwave, retrowave, cyberpunk",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-synth-2",
                    Name = "SomaFM Groove Salad",
                    StreamUrl = "https://ice2.somafm.com/groovesalad-128-mp3",
                    Tags = "ambient, downtempo, chillout",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["news"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-news-1",
                    Name = "BBC World Service",
                    StreamUrl = "https://stream.live.vc.bbcmedia.co.uk/bbc_world_service",
                    Tags = "news, talk, global",
                    Country = "United Kingdom",
                    CountryCode = "GB",
                    Codec = "MP3",
                    Bitrate = 96
                },
                new RadioStation
                {
                    StationUuid = "curated-news-2",
                    Name = "NPR 24 Hour Program Stream",
                    StreamUrl = "https://npr-ice.streamguys1.com/live.mp3",
                    Tags = "public radio, news, culture",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 96
                }
            ],
            ["pop"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-pop-1",
                    Name = "Radio Swiss Pop",
                    StreamUrl = "https://stream.srg-ssr.ch/m/rsp/mp3_128",
                    Tags = "pop, hits, current",
                    Country = "Switzerland",
                    CountryCode = "CH",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-pop-2",
                    Name = "SomaFM PopTron",
                    StreamUrl = "https://ice2.somafm.com/poptron-128-mp3",
                    Tags = "indie pop, alternative, modern",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["chinese"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-cn-1",
                    Name = "CRI Easy FM (轻松调频)",
                    StreamUrl = "http://live.cri.cn/fm915.mp3",
                    Tags = "chinese, easy listening, bilingual",
                    Country = "China",
                    CountryCode = "CN",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-cn-2",
                    Name = "Chinese Classical Music Radio",
                    StreamUrl = "http://stream.zeno.fm/3h7k5b721nhvv",
                    Tags = "chinese, traditional, instrumental",
                    Country = "China",
                    CountryCode = "CN",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ],
            ["top"] =
            [
                new RadioStation
                {
                    StationUuid = "curated-top-1",
                    Name = "SomaFM Groove Salad",
                    StreamUrl = "https://ice2.somafm.com/groovesalad-128-mp3",
                    Tags = "ambient, downtempo, chillout",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-top-2",
                    Name = "Radio Swiss Pop",
                    StreamUrl = "https://stream.srg-ssr.ch/m/rsp/mp3_128",
                    Tags = "pop, hits, current",
                    Country = "Switzerland",
                    CountryCode = "CH",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ]
        };

    private static readonly IReadOnlyList<RadioStation> AllCuratedStations =
        StationsByCategory.Values
            .SelectMany(s => s)
            .DistinctBy(s => s.StationUuid, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<RadioStation> GetCuratedStations(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return StationsByCategory["top"];

        return StationsByCategory.TryGetValue(category.Trim(), out var list)
            ? list
            : StationsByCategory["top"];
    }

    public static IReadOnlyList<RadioStation> SearchCuratedStations(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return AllCuratedStations;

        var clean = query.Trim();
        return AllCuratedStations
            .Where(s => s.Name.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                        s.Tags.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
                        s.Country.Contains(clean, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static RadioStation? FindByUuid(string stationUuid)
    {
        if (string.IsNullOrWhiteSpace(stationUuid))
            return null;

        return AllCuratedStations.FirstOrDefault(s =>
            string.Equals(s.StationUuid, stationUuid, StringComparison.OrdinalIgnoreCase));
    }
}
