using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Concurrency;
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

        var weather = await service.GetWeatherAsync("上海", null, null, CancellationToken.None);
        var weatherAgain = await service.GetWeatherAsync("上海", null, null, CancellationToken.None);

        Assert.NotNull(weather);
        Assert.Equal(WeatherKind.Rain, weather.Kind);
        Assert.Equal(18.5, weather.TemperatureC);
        Assert.Equal("上海", weather.LocationName);
        // 第二次命中缓存：geocoding + forecast 各只请求一次
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(weather, weatherAgain);
    }

    [Fact]
    public async Task GetWeatherAsync_CoordinatesSkipGeocoding_AndUseCityAsDisplayName()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["api.open-meteo.com/v1/forecast"] =
                () => "{\"current\":{\"weather_code\":0,\"temperature_2m\":22.0}}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var weather = await service.GetWeatherAsync("成都", 30.6667, 104.0667, CancellationToken.None);

        Assert.NotNull(weather);
        Assert.Equal("成都", weather!.LocationName);
        Assert.Equal(WeatherKind.Sunny, weather.Kind);
        // 只有 forecast 一次请求：坐标直取不碰 geocoding
        Assert.Single(handler.Requests);
        Assert.Contains("latitude=30.6667", handler.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPlacesAsync_ParsesCandidates_WithAdminAndCountry()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["geocoding-api.open-meteo.com/v1/search"] =
                () => "{\"results\":[" +
                      "{\"name\":\"成都\",\"admin1\":\"四川\",\"country\":\"中国\",\"latitude\":30.67,\"longitude\":104.07}," +
                      "{\"name\":\"成都\",\"admin1\":\"江西\",\"country\":\"中国\",\"latitude\":26.98,\"longitude\":114.21}]}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var places = await service.SearchPlacesAsync("成都", CancellationToken.None);

        Assert.NotNull(places);
        Assert.Equal(2, places!.Count);
        Assert.Equal("成都", places[0].Name);
        Assert.Equal("四川", places[0].Admin);
        Assert.Equal("中国", places[0].Country);
        Assert.Equal(30.67, places[0].Latitude);
        // 展示行：省, 国家
        Assert.Equal("四川, 中国", places[0].AdminLine);
        Assert.Equal("江西, 中国", places[1].AdminLine);
    }

    [Fact]
    public async Task SearchPlacesAsync_NoMatch_ReturnsEmptyList_FailureReturnsNull()
    {
        var noMatch = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["geocoding-api.open-meteo.com/v1/search"] = () => "{\"results\":[]}",
        });
        using (var clientNoMatch = new HttpClient(noMatch))
        {
            var noMatchResult = await new WeatherService(clientNoMatch)
                .SearchPlacesAsync("不存在的城市xyz", CancellationToken.None);
            Assert.NotNull(noMatchResult);
            Assert.Empty(noMatchResult!);
        }

        // 未命中路由 → 404 → EnsureSuccessStatusCode 抛 → 网络失败语义 null
        var failed = new RoutingHandler(new Dictionary<string, Func<string>>());
        using (var clientFailed = new HttpClient(failed))
        {
            Assert.Null(await new WeatherService(clientFailed)
                .SearchPlacesAsync("成都", CancellationToken.None));
        }
    }

    [Fact]
    public async Task LocateByIpAsync_ReturnsPlaceWithAdmin()
    {
        var handler = new RoutingHandler(new Dictionary<string, Func<string>>
        {
            ["ip-api.com/json"] =
                () => "{\"status\":\"success\",\"lat\":30.66,\"lon\":104.06,\"city\":\"成都\",\"regionName\":\"四川\",\"country\":\"China\"}",
        });
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var place = await service.LocateByIpAsync(CancellationToken.None);

        Assert.NotNull(place);
        Assert.Equal("成都", place!.Name);
        Assert.Equal("四川", place.Admin);
        Assert.Equal(30.66, place.Latitude);
        Assert.Equal(104.06, place.Longitude);
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

        var weather = await service.GetWeatherAsync(null, null, null, CancellationToken.None);

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

        var weather = await service.GetWeatherAsync("  ", null, null, CancellationToken.None);

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

        Assert.Null(await service.GetWeatherAsync("不存在的城市xyz", null, null, CancellationToken.None));
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

        public Task<WeatherInfo?> GetWeatherAsync(string? city, double? latitude, double? longitude, CancellationToken cancellationToken)
            => Task.FromResult(Result);

        public Task<System.Collections.Generic.IReadOnlyList<GeoPlace>?> SearchPlacesAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<GeoPlace>?>(null);

        public Task<GeoPlace?> LocateByIpAsync(CancellationToken cancellationToken)
            => Task.FromResult<GeoPlace?>(null);
    }

    [Fact]
    public async Task RefreshWeather_WithResult_ShowsGlyphAndTooltip()
    {
        var weather = new FakeWeatherService
        {
            Result = new WeatherInfo(WeatherKind.Snow, -3.4, "哈尔滨")
        };
        using var vm = new WeatherViewModel(weather, ImmediateScheduler.Instance);

        await vm.RefreshWeatherAsync("哈尔滨");

        Assert.True(vm.IsWeatherVisible);
        Assert.Equal("❄", vm.WeatherGlyph);
        Assert.Contains("哈尔滨", vm.WeatherTooltip);
        Assert.Contains("雪", vm.WeatherTooltip);
        Assert.Contains("-3°C", vm.WeatherTooltip);
        // 设置页回显与 Tooltip 同源
        Assert.Equal(vm.WeatherTooltip, vm.WeatherSummary);
    }

    [Fact]
    public async Task RefreshWeather_WithoutResult_HidesIconAndShowsPlaceholderSummary()
    {
        var weather = new FakeWeatherService { Result = null };
        using var vm = new WeatherViewModel(weather, ImmediateScheduler.Instance);

        await vm.RefreshWeatherAsync(null);

        Assert.False(vm.IsWeatherVisible);
        Assert.Equal(string.Empty, vm.WeatherTooltip);
        Assert.Equal(AppLanguage.T("暂未获取", "Not fetched yet"), vm.WeatherSummary);
    }
}
