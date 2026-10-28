using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIRadio.Desktop.Services;

public enum WeatherKind
{
    Sunny,
    Cloudy,
    Overcast,
    Rain,
    Snow,
    Thunder,
    Fog,
    Unknown
}

/// <summary>当前天气快照（描述文案与图标由 VM 层按 Kind 本地化）。</summary>
public sealed record WeatherInfo(WeatherKind Kind, double TemperatureC, string LocationName);

/// <summary>地点候选（城市搜索与 IP 定位共用；Name 为标准城市名）。</summary>
public sealed record GeoPlace(string Name, string Admin, string Country, double Latitude, double Longitude)
{
    /// <summary>候选列表展示行：省/州, 国家（空段省略）。</summary>
    public string AdminLine
    {
        get
        {
            var admin = string.IsNullOrWhiteSpace(Admin) ? string.Empty : Admin.Trim();
            var country = string.IsNullOrWhiteSpace(Country) ? string.Empty : Country.Trim();
            return admin.Length == 0 ? country
                : country.Length == 0 ? admin
                : $"{admin}, {country}";
        }
    }
}

/// <summary>
/// 天气取数（docs/plans/2026-09-17-weather-calendar-design.md §3）：优先用已选地点的
/// 经纬度直取，其次城市名经 Open-Meteo geocoding 转坐标，否则 ip-api.com IP 粗定位；
/// Open-Meteo current 预报。纯展示增强：任一步失败返回 null 不抛，调用方隐藏图标即可。
/// </summary>
public interface IWeatherService
{
    /// <summary>
    /// 获取当前天气（30 分钟内存缓存）。latitude/longitude 在时跳过 geocoding 直取，
    /// 此时 city 仅作 LocationName 展示；两者皆空且 city 非空走 geocoding，全空走 IP 定位。
    /// </summary>
    Task<WeatherInfo?> GetWeatherAsync(string? city, double? latitude, double? longitude, CancellationToken cancellationToken);

    /// <summary>按名称搜索地点候选（Open-Meteo geocoding，≤5 条，显示语言跟随界面）；null=网络/解析失败，空列表=无匹配。</summary>
    Task<IReadOnlyList<GeoPlace>?> SearchPlacesAsync(string query, CancellationToken cancellationToken);

    /// <summary>按网络 IP 粗定位（ip-api，城市粒度）；失败返回 null。</summary>
    Task<GeoPlace?> LocateByIpAsync(CancellationToken cancellationToken);
}

public sealed class WeatherService : IWeatherService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly object _cacheGate = new();
    private string? _cacheKey;
    private WeatherInfo? _cached;
    private DateTime _cachedAt;

    public WeatherService(HttpClient http)
    {
        _http = http;
    }

    public async Task<WeatherInfo?> GetWeatherAsync(string? city, double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var hasCoords = latitude is not null && longitude is not null;
        var key = hasCoords
            ? $"geo:{latitude!.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},{longitude!.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(city) ? "$auto" : city.Trim();
        lock (_cacheGate)
        {
            if (_cached != null && _cacheKey == key && DateTime.Now - _cachedAt < CacheTtl)
                return _cached;
        }

        var result = await TryGetWeatherAsync(city, latitude, longitude, cancellationToken);
        if (result != null)
        {
            lock (_cacheGate)
            {
                _cacheKey = key;
                _cached = result;
                _cachedAt = DateTime.Now;
            }
        }
        return result;
    }

    private async Task<WeatherInfo?> TryGetWeatherAsync(string? city, double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(OverallTimeout);

            double lat, lon;
            string locationName;
            if (latitude is not null && longitude is not null)
            {
                // 已选地点自带坐标（设置页候选点选/定位落定）：跳过 geocoding 直取
                lat = latitude.Value;
                lon = longitude.Value;
                locationName = string.IsNullOrWhiteSpace(city) ? "当前位置" : city.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(city))
            {
                var geo = await GeocodeAsync(city.Trim(), timeoutCts.Token);
                if (geo == null)
                    return null;
                (lat, lon, locationName) = geo.Value;
            }
            else
            {
                var located = await LocateByIpAsync(timeoutCts.Token);
                if (located == null)
                    return null;
                (lat, lon, locationName) = (located.Latitude, located.Longitude, located.Name);
            }

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&longitude={lon.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
                      "&current=weather_code,temperature_2m&timezone=auto";
            using var response = await _http.GetAsync(url, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutCts.Token));
            if (!doc.RootElement.TryGetProperty("current", out var current))
                return null;

            var code = current.TryGetProperty("weather_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : -1;
            var temperature = current.TryGetProperty("temperature_2m", out var tempEl) && tempEl.ValueKind == JsonValueKind.Number
                ? tempEl.GetDouble()
                : double.NaN;

            return new WeatherInfo(MapKind(code), temperature, locationName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 天气是展示增强：失败静默（Log.Debug），下次刷新自动重试
            Serilog.Log.Debug(ex, "Weather fetch failed");
            return null;
        }
    }

    private async Task<(double Lat, double Lon, string Name)?> GeocodeAsync(string city, CancellationToken cancellationToken)
    {
        var places = await SearchPlacesAsync(city, cancellationToken);
        if (places is not { Count: > 0 })
            return null;
        var first = places[0];
        return (first.Latitude, first.Longitude, first.Name);
    }

    /// <summary>按名称搜索地点候选；查询语言跟随界面语言；null=网络/解析失败，空列表=无匹配。</summary>
    public async Task<IReadOnlyList<GeoPlace>?> SearchPlacesAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var language = AppLanguage.Current == "en" ? "en" : "zh";
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=5&language={language}";
            using var response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return Array.Empty<GeoPlace>();

            var places = new List<GeoPlace>();
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number ||
                    !item.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number)
                    continue;
                var name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? query
                    : query;
                // admin1 是省/州，admin2 是市（与 name 大概率重复），取 admin1 + country 区分重名城市
                var admin = item.TryGetProperty("admin1", out var adminEl) && adminEl.ValueKind == JsonValueKind.String
                    ? adminEl.GetString() ?? string.Empty
                    : string.Empty;
                var country = item.TryGetProperty("country", out var countryEl) && countryEl.ValueKind == JsonValueKind.String
                    ? countryEl.GetString() ?? string.Empty
                    : string.Empty;
                places.Add(new GeoPlace(name, admin, country, lat.GetDouble(), lon.GetDouble()));
            }
            return places;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Place search failed for {Query}", query);
            return null;
        }
    }

    /// <summary>按网络 IP 粗定位（ip-api，城市粒度）；失败返回 null。</summary>
    public async Task<GeoPlace?> LocateByIpAsync(CancellationToken cancellationToken)
    {
        // 明文 HTTP 是上游限制的明知取舍：ip-api 免费版仅支持 HTTP（HTTPS 需付费），
        // 暴露面为经未加密信道返回的 lat/lon/城市名（仅天气展示定位，无凭据）；
        // 中间人最多篡改天气定位的展示，不影响其余任何链路。
        var url = "http://ip-api.com/json?fields=status,lat,lon,city,regionName,country";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        if (!root.TryGetProperty("status", out var status) ||
            status.GetString() != "success" ||
            !root.TryGetProperty("lat", out var lat) || lat.ValueKind != JsonValueKind.Number ||
            !root.TryGetProperty("lon", out var lon) || lon.ValueKind != JsonValueKind.Number)
            return null;

        string GetString(string prop) => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
        var city = GetString("city");
        return new GeoPlace(
            string.IsNullOrWhiteSpace(city) ? "当前位置" : city,
            GetString("regionName"),
            GetString("country"),
            lat.GetDouble(),
            lon.GetDouble());
    }

    /// <summary>WMO weather code → 图标类别。</summary>
    internal static WeatherKind MapKind(int code) => code switch
    {
        0 => WeatherKind.Sunny,
        1 or 2 => WeatherKind.Cloudy,
        3 => WeatherKind.Overcast,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 67 => WeatherKind.Rain,      // 毛毛雨/雨
        >= 71 and <= 77 => WeatherKind.Snow,
        >= 80 and <= 82 => WeatherKind.Rain,      // 阵雨
        85 or 86 => WeatherKind.Snow,             // 阵雪
        >= 95 and <= 99 => WeatherKind.Thunder,
        _ => WeatherKind.Unknown
    };
}
