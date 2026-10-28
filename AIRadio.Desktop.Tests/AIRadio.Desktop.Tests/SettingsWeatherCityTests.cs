using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>天气城市搜索联想/点选落定/定位命令/坐标持久化的回归测试。</summary>
public sealed class SettingsWeatherCityTests
{
    private sealed class FakeWeatherService : IWeatherService
    {
        public IReadOnlyList<GeoPlace>? SearchResult { get; set; }
        /// <summary>可编程搜索时序：设置后优先于 SearchResult（挂起用 TaskCompletionSource）。</summary>
        public Func<string, Task<IReadOnlyList<GeoPlace>?>>? SearchHandler { get; set; }
        public GeoPlace? Located { get; set; }
        /// <summary>非空时 LocateByIpAsync 抛该异常（网络异常路径）。</summary>
        public Exception? LocateException { get; set; }

        public Task<WeatherInfo?> GetWeatherAsync(string? city, double? latitude, double? longitude, CancellationToken cancellationToken)
            => Task.FromResult<WeatherInfo?>(null);

        public Task<IReadOnlyList<GeoPlace>?> SearchPlacesAsync(string query, CancellationToken cancellationToken)
            => SearchHandler?.Invoke(query) ?? Task.FromResult(SearchResult);

        public Task<GeoPlace?> LocateByIpAsync(CancellationToken cancellationToken)
            => LocateException is { } ex
                ? Task.FromException<GeoPlace?>(ex)
                : Task.FromResult(Located);
    }

    private static readonly GeoPlace Chengdu = new("成都", "四川", "中国", 30.67, 104.07);
    private static readonly GeoPlace Chongqing = new("重庆", "重庆", "中国", 29.56, 106.55);

    private static SettingsViewModel CreateVm(IWeatherService weather, string settingsFile)
        => new(new Mock<ILLMService>().Object, new Mock<ISecureStorage>().Object, settingsFile,
            weatherService: weather);

    private static string CreateTempSettingsFile()
        => Path.Combine(Path.GetTempPath(), $"airadio-weather-{Guid.NewGuid():N}.json");

    /// <summary>保存经 gate 串行且写盘在后台线程，轮询短窗等待 weather_city 落盘。</summary>
    private static async Task<string> WaitForSettingsAsync(string file)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(file))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    if (json.Contains("weather_city"))
                        return json;
                }
                catch (IOException)
                {
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("settings.json 未在预算内落盘");
    }

    [Fact]
    public async Task SelectSuggestion_LocksNameAndCoords_AndPersistsForRestart()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(new FakeWeatherService(), settingsFile);

            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();

            Assert.Equal("成都", vm.WeatherCity);
            Assert.Equal(30.67, vm.WeatherLatitude);
            Assert.Equal(104.07, vm.WeatherLongitude);
            Assert.Empty(vm.WeatherCitySuggestions);

            // 点选即跟随保存（同主题切换语义）：城市与坐标一并落盘
            var json = await WaitForSettingsAsync(settingsFile);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("成都", doc.RootElement.GetProperty("weather_city").GetString());
            Assert.Equal(30.67, doc.RootElement.GetProperty("weather_latitude").GetDouble());
            Assert.Equal(104.07, doc.RootElement.GetProperty("weather_longitude").GetDouble());

            // 重启还原三件套
            using var restored = CreateVm(new FakeWeatherService(), settingsFile);
            await restored.LoadAsync();
            Assert.Equal("成都", restored.WeatherCity);
            Assert.Equal(30.67, restored.WeatherLatitude);
            Assert.Equal(104.07, restored.WeatherLongitude);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task TypedDifferentText_DropsCoords_AndShowsSuggestions()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(
                new FakeWeatherService { SearchResult = new[] { Chongqing } }, settingsFile);
            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();

            // 模拟手输（TextBox 逐键的最终值）：改写为不同文本即弃坐标、弹联想
            vm.WeatherCity = "重庆";
            await vm.HandleWeatherCityTypedAsync("重庆");

            Assert.Null(vm.WeatherLatitude);
            Assert.Null(vm.WeatherLongitude);
            Assert.Single(vm.WeatherCitySuggestions);
            Assert.Equal("重庆", vm.WeatherCitySuggestions[0].Name);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task ConfirmedTextKeptAsIs_NoSuggestions_CoordsSurvive()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(new FakeWeatherService(), settingsFile);
            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();

            // 已落定标准名原样再触发（加载/点选赋值都会流经节流链）：确认态不弹不清
            await vm.HandleWeatherCityTypedAsync("成都");

            Assert.Equal(30.67, vm.WeatherLatitude);
            Assert.Empty(vm.WeatherCitySuggestions);
            Assert.False(vm.WeatherCityNoMatch);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task EmptyText_ClearsSelection_BackToIpAutoLocate()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(new FakeWeatherService(), settingsFile);
            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();

            // 真实链路：用户清空输入框，TextBox 已把空文本写回 WeatherCity
            vm.WeatherCity = "   ";
            await vm.HandleWeatherCityTypedAsync("   ");

            Assert.Null(vm.WeatherLatitude);
            Assert.Null(vm.WeatherLongitude);
            Assert.Empty(vm.WeatherCitySuggestions);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchFeedback_SeparatesNoMatchFromFailure(bool noMatch)
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(
                new FakeWeatherService { SearchResult = noMatch ? Array.Empty<GeoPlace>() : null },
                settingsFile);

            vm.WeatherCity = "不存在";
            await vm.HandleWeatherCityTypedAsync("不存在");

            Assert.Equal(noMatch, vm.WeatherCityNoMatch);
            Assert.Equal(!noMatch, vm.WeatherCitySearchFailed);
            Assert.Empty(vm.WeatherCitySuggestions);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    /// <summary>
    /// 在途搜索的迟到结果不得覆盖后续状态：手输触发搜索（挂起）后清空文本、
    /// 或改回已落定标准名，这两条"不再搜索"分支都会作废在途代次。
    /// 段 1 的清空会销毁落定态，段 2 前须重新落定；段 2 的手输"重庆"
    /// 按设计清坐标，末尾坐标断言为 Null（坐标存续由 ConfirmedText 用例锚定）。
    /// </summary>
    [Fact]
    public async Task InFlightSearch_Discarded_ByEmptyTextAndConfirmedText()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            var pending = new TaskCompletionSource<IReadOnlyList<GeoPlace>?>();
            var fake = new FakeWeatherService
            {
                SearchHandler = _ => pending.Task,
            };
            using var vm = CreateVm(fake, settingsFile);
            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();

            // 段 1：手输"重"触发搜索（挂起）→ 清空文本 → 迟到结果到达 → 候选不得弹出
            vm.WeatherCity = "重";
            var inFlight = vm.HandleWeatherCityTypedAsync("重");
            vm.WeatherCity = "";
            await vm.HandleWeatherCityTypedAsync("");
            pending.SetResult(new[] { Chongqing });
            await inFlight;
            Assert.Empty(vm.WeatherCitySuggestions);
            Assert.Null(vm.WeatherLatitude);

            // 段 2：重新落定后，手输"重庆"触发搜索（挂起）→ 改回已落定"成都" → 迟到结果不得弹出
            var pending2 = new TaskCompletionSource<IReadOnlyList<GeoPlace>?>();
            fake.SearchHandler = _ => pending2.Task;
            vm.SelectWeatherCityCommand.Execute(Chengdu).Subscribe();
            Assert.Equal(30.67, vm.WeatherLatitude);
            vm.WeatherCity = "重庆";
            var inFlight2 = vm.HandleWeatherCityTypedAsync("重庆");
            vm.WeatherCity = "成都";
            await vm.HandleWeatherCityTypedAsync("成都");
            pending2.SetResult(new[] { Chongqing });
            await inFlight2;
            Assert.Empty(vm.WeatherCitySuggestions);
            // 段 2 的手输"重庆"按设计清了坐标（改回已落定名只是确认态，不恢复坐标）
            Assert.Null(vm.WeatherLatitude);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task LocateCommand_FillsPlaceAndCoords_OrReportsFailure()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(new FakeWeatherService { Located = Chengdu }, settingsFile);
            await vm.LocateWeatherCityCommand.Execute().FirstAsync();

            Assert.Equal("成都", vm.WeatherCity);
            Assert.Equal(30.67, vm.WeatherLatitude);
            Assert.False(vm.IsWeatherLocating);
            // 成功文案与异步保存完成的"设置已保存"存在竞态，不断言具体文案；
            // 落定反馈由输入框值 + 回显行承担

            using var failed = CreateVm(new FakeWeatherService(), settingsFile);
            await failed.LocateWeatherCityCommand.Execute().FirstAsync();

            Assert.Equal(string.Empty, failed.WeatherCity);
            Assert.Null(failed.WeatherLatitude);
            Assert.Contains("定位失败", failed.StatusMessage);
            Assert.False(failed.IsWeatherLocating);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task LocateCommand_ServiceException_ReportsFailureWithoutThrowing()
    {
        var settingsFile = CreateTempSettingsFile();
        try
        {
            using var vm = CreateVm(
                new FakeWeatherService { LocateException = new HttpRequestException("network down") },
                settingsFile);

            await vm.LocateWeatherCityCommand.Execute().FirstAsync();

            Assert.Contains("定位失败", vm.StatusMessage);
            Assert.Equal(string.Empty, vm.WeatherCity);
            Assert.False(vm.IsWeatherLocating);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }
}
