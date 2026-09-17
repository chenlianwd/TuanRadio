using System;
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

/// <summary>
/// 天气取数（docs/plans/2026-09-17-weather-calendar-design.md §3）：城市优先（Open-Meteo
/// geocoding 转坐标），否则 ip-api.com IP 粗定位；Open-Meteo current 预报。
/// 纯展示增强：任一步失败返回 null 不抛，调用方隐藏图标即可。
/// </summary>
public interface IWeatherService
{
    /// <summary>获取当前天气（30 分钟内存缓存；city 为空时走 IP 定位）。</summary>
    Task<WeatherInfo?> GetWeatherAsync(string? city, CancellationToken cancellationToken);
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

    public async Task<WeatherInfo?> GetWeatherAsync(string? city, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(city) ? "$auto" : city.Trim();
        lock (_cacheGate)
        {
            if (_cached != null && _cacheKey == key && DateTime.Now - _cachedAt < CacheTtl)
                return _cached;
        }

        var result = await TryGetWeatherAsync(city, cancellationToken);
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

    private async Task<WeatherInfo?> TryGetWeatherAsync(string? city, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(OverallTimeout);

            double latitude, longitude;
            string locationName;
            if (!string.IsNullOrWhiteSpace(city))
            {
                var geo = await GeocodeAsync(city.Trim(), timeoutCts.Token);
                if (geo == null)
                    return null;
                (latitude, longitude, locationName) = geo.Value;
            }
            else
            {
                var located = await LocateByIpAsync(timeoutCts.Token);
                if (located == null)
                    return null;
                (latitude, longitude, locationName) = located.Value;
            }

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&longitude={longitude.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" +
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
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
            return null;

        var first = results[0];
        if (!first.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number ||
            !first.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number)
            return null;

        var name = first.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? city
            : city;
        return (lat.GetDouble(), lon.GetDouble(), name);
    }

    private async Task<(double Lat, double Lon, string Name)?> LocateByIpAsync(CancellationToken cancellationToken)
    {
        var url = "http://ip-api.com/json?fields=status,lat,lon,city";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = doc.RootElement;
        if (!root.TryGetProperty("status", out var status) ||
            status.GetString() != "success" ||
            !root.TryGetProperty("lat", out var lat) || lat.ValueKind != JsonValueKind.Number ||
            !root.TryGetProperty("lon", out var lon) || lon.ValueKind != JsonValueKind.Number)
            return null;

        var city = root.TryGetProperty("city", out var cityEl) && cityEl.ValueKind == JsonValueKind.String
            ? cityEl.GetString() ?? string.Empty
            : string.Empty;
        return (lat.GetDouble(), lon.GetDouble(), string.IsNullOrWhiteSpace(city) ? "当前位置" : city);
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
