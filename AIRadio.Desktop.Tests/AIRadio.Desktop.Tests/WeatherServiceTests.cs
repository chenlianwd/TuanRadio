using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class WeatherServiceTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string>> _routes;
        public List<string> Requests { get; } = new();

        public RoutingHandler(Dictionary<string, Func<string>> routes)
            => _routes = routes;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            lock (Requests)
            {
                Requests.Add(url);
            }

            var route = _routes.FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.Ordinal));
            if (route.Key == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}")
                });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route.Value())
            });
        }
    }

    [Fact]
    public async Task GetWeatherAsync_CityGeocodesThenFetchesForecast_AndCachesForRepeatCalls()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["geocoding-api.open-meteo.com/v1/search"] =
                () => "{\"results\":[{\"latitude\":31.23,\"longitude\":121.47,\"name\":\"上海\"}]}",
            ["api.open-meteo.com/v1/forecast"] =
                () => "{\"current\":{\"weather_code\":61,\"temperature_2m\":18.5}}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var weather = await service.GetWeatherAsync("上海", CancellationToken.None);
        var weatherAgain = await service.GetWeatherAsync("上海", CancellationToken.None);

        Assert.NotNull(weather);
        Assert.Equal(WeatherKind.Rain, weather.Kind);
        Assert.Equal(18.5, weather.TemperatureC);
        Assert.Equal("上海", weather.LocationName);
        // 第二次命中缓存：geocoding + forecast 各只请求一次
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(weather, weatherAgain);
    }

    [Fact]
    public async Task GetWeatherAsync_EmptyCityLocatesByIp()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["ip-api.com/json"] =
                () => "{\"status\":\"success\",\"lat\":39.9,\"lon\":116.4,\"city\":\"北京\"}",
            ["api.open-meteo.com/v1/forecast"] =
                () => "{\"current\":{\"weather_code\":0,\"temperature_2m\":25.1}}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var weather = await service.GetWeatherAsync(null, CancellationToken.None);

        Assert.NotNull(weather);
        Assert.Equal(WeatherKind.Sunny, weather.Kind);
        Assert.Equal("北京", weather.LocationName);
    }

    [Fact]
    public async Task GetWeatherAsync_IpLocationFails_ReturnsNullWithoutThrowing()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["ip-api.com/json"] = () => "{\"status\":\"fail\"}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var weather = await service.GetWeatherAsync("  ", CancellationToken.None);

        Assert.Null(weather);
    }

    [Fact]
    public async Task GetWeatherAsync_UnknownCity_ReturnsNull()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["geocoding-api.open-meteo.com/v1/search"] = () => "{\"results\":[]}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        Assert.Null(await service.GetWeatherAsync("不存在的城市xyz", CancellationToken.None));
    }

    [Theory]
    [InlineData(0, WeatherKind.Sunny)]
    [InlineData(2, WeatherKind.Cloudy)]
    [InlineData(3, WeatherKind.Overcast)]
    [InlineData(45, WeatherKind.Fog)]
    [InlineData(61, WeatherKind.Rain)]
    [InlineData(80, WeatherKind.Rain)]
    [InlineData(73, WeatherKind.Snow)]
    [InlineData(86, WeatherKind.Snow)]
    [InlineData(95, WeatherKind.Thunder)]
    [InlineData(42, WeatherKind.Unknown)]
    public void MapKind_WmoCodes(int code, WeatherKind expected)
        => Assert.Equal(expected, WeatherService.MapKind(code));
}

public class WeatherViewModelTests
{
    private sealed class FakeWeatherService : IWeatherService
    {
        public WeatherInfo? Result { get; set; }

        public Task<WeatherInfo?> GetWeatherAsync(string? city, CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    [Fact]
    public async Task RefreshWeather_WithResult_ShowsGlyphAndTooltip()
    {
        var weather = new FakeWeatherService
        {
            Result = new WeatherInfo(WeatherKind.Snow, -3.4, "哈尔滨")
        };
        using var vm = new WeatherViewModel(weather);

        await vm.RefreshWeatherAsync("哈尔滨");

        Assert.True(vm.IsWeatherVisible);
        Assert.Equal("❄", vm.WeatherGlyph);
        Assert.Contains("哈尔滨", vm.WeatherTooltip);
        Assert.Contains("雪", vm.WeatherTooltip);
        Assert.Contains("-3°C", vm.WeatherTooltip);
    }

    [Fact]
    public async Task RefreshWeather_WithoutResult_HidesIcon()
    {
        var weather = new FakeWeatherService { Result = null };
        using var vm = new WeatherViewModel(weather);

        await vm.RefreshWeatherAsync(null);

        Assert.False(vm.IsWeatherVisible);
        Assert.Equal(string.Empty, vm.WeatherTooltip);
    }

    [Fact]
    public void UpdateCalendar_HighlightsFestivalAndShowsBadge()
    {
        var weather = new FakeWeatherService();
        using var vm = new WeatherViewModel(weather);

        vm.UpdateCalendar(new DateTime(2026, 10, 1));

        Assert.Equal("1", vm.CalendarDayBadge);
        Assert.True(vm.IsCalendarHighlighted);
        Assert.Contains("国庆节", vm.CalendarTooltip);
        Assert.Contains("农历", vm.CalendarTooltip);
    }

    [Fact]
    public void UpdateCalendar_DifferentDateRecomputesBadge()
    {
        var weather = new FakeWeatherService();
        using var vm = new WeatherViewModel(weather);

        vm.UpdateCalendar(new DateTime(2026, 10, 1));
        Assert.Equal("1", vm.CalendarDayBadge);
        Assert.Contains("距周六 2 天", vm.CalendarTooltip);

        // 跨日重算（同日重复调用为 no-op，不在此断言内部计数）
        vm.UpdateCalendar(new DateTime(2026, 10, 2));
        Assert.Equal("2", vm.CalendarDayBadge);
    }
}
