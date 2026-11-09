using System;
using System.Collections.Generic;
using System.Linq;
using AIRadio.Desktop.Models;

namespace AIRadio.Desktop.Services.Music;

/// <summary>
/// 内置精选离线兜底电台库（覆盖 8 大主流流派与公共广播高可用种子）。
/// 当 Radio-Browser 镜像集群全部不可达或处于离线断网环境时，保证网络电台列表零红字、永远有台可播。
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
                },
                new RadioStation
                {
                    StationUuid = "curated-lofi-3",
                    Name = "Chillsky Lo-Fi Radio",
                    StreamUrl = "https://stream.zeno.fm/f3wvbbqmdg8uv",
                    Tags = "lofi, relaxing, beats",
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
                },
                new RadioStation
                {
                    StationUuid = "curated-jazz-3",
                    Name = "Jazz24",
                    StreamUrl = "https://live.wostreaming.net/manifest/ppm-jazz24aac-ibc1",
                    Tags = "jazz, classic jazz, vocal",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "AAC",
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
                    Name = "Venice Classic Radio",
                    StreamUrl = "http://174.36.206.197:8000/stream",
                    Tags = "classical, opera, symphonic",
                    Country = "Italy",
                    CountryCode = "IT",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-classical-3",
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
                    Name = "SomaFM Space Station",
                    StreamUrl = "https://ice2.somafm.com/spacestation-128-mp3",
                    Tags = "ambient, space, electronic",
                    Country = "United States",
                    CountryCode = "US",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-synth-3",
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
                },
                new RadioStation
                {
                    StationUuid = "curated-news-3",
                    Name = "RFI Monde (Radio France)",
                    StreamUrl = "https://rfimonde64k.ice.infomaniak.ch/rfimonde-64.mp3",
                    Tags = "news, international, french",
                    Country = "France",
                    CountryCode = "FR",
                    Codec = "MP3",
                    Bitrate = 64
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
                },
                new RadioStation
                {
                    StationUuid = "curated-pop-3",
                    Name = "Dance Wave! Retro",
                    StreamUrl = "https://dancewave.online/dance.mp3",
                    Tags = "dance, retro, electronic",
                    Country = "Hungary",
                    CountryCode = "HU",
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
                    Name = "Radio Taiwan International (华语广播)",
                    StreamUrl = "https://stream.rti.org.tw/live.mp3",
                    Tags = "chinese, talk, culture",
                    Country = "Taiwan",
                    CountryCode = "TW",
                    Codec = "MP3",
                    Bitrate = 128
                },
                new RadioStation
                {
                    StationUuid = "curated-cn-3",
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
                },
                new RadioStation
                {
                    StationUuid = "curated-top-3",
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
                    StationUuid = "curated-top-4",
                    Name = "Lofi Girl Radio (Chill Beats)",
                    StreamUrl = "https://play.streamafrica.net/lofigirl",
                    Tags = "lofi, chillhop, study beats",
                    Country = "France",
                    CountryCode = "FR",
                    Codec = "MP3",
                    Bitrate = 128
                }
            ]
        };

    private static readonly IReadOnlyList<RadioStation> AllCuratedStations =
        StationsByCategory.Values.SelectMany(s => s)
            .GroupBy(s => s.StationUuid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
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
