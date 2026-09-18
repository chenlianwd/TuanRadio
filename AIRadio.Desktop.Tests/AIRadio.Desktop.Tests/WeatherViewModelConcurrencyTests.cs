using System.Reactive.Concurrency;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Moq;

namespace AIRadio.Desktop.Tests;

/// <summary>天气请求乱序、关闭与 UI 派发的回归测试。</summary>
public sealed class WeatherViewModelConcurrencyTests
{
    /// <summary>旧请求成功或失败均不得覆盖新城市结果。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_LatestCityWins(bool oldRequestFails)
    {
        var oldResult = new TaskCompletionSource<WeatherInfo?>();
        var weather = new Mock<IWeatherService>();
        weather.Setup(x => x.GetWeatherAsync("旧城市", It.IsAny<CancellationToken>())).Returns(oldResult.Task);
        weather.Setup(x => x.GetWeatherAsync("新城市", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherInfo(WeatherKind.Sunny, 25, "新城市"));
        using var vm = new WeatherViewModel(weather.Object, ImmediateScheduler.Instance);
        var first = vm.RefreshWeatherAsync("旧城市");
        await vm.RefreshWeatherAsync("新城市");
        if (oldRequestFails)
            oldResult.SetException(new IOException("旧请求失败"));
        else
            oldResult.SetResult(new WeatherInfo(WeatherKind.Rain, 10, "旧城市"));
        await first;
        Assert.StartsWith("新城市", vm.WeatherTooltip);
        Assert.True(vm.IsWeatherVisible);
    }

    /// <summary>服务即使忽略取消，关闭后的晚到结果也不得更新界面。</summary>
    [Fact]
    public async Task Dispose_IgnoredCancellation_DiscardsLateResult()
    {
        var result = new TaskCompletionSource<WeatherInfo?>();
        var weather = new Mock<IWeatherService>();
        weather.Setup(x => x.GetWeatherAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(result.Task);
        using var vm = new WeatherViewModel(weather.Object, ImmediateScheduler.Instance);
        var request = vm.RefreshWeatherAsync(null);
        vm.Dispose();
        result.SetResult(new WeatherInfo(WeatherKind.Sunny, 25, "晚到结果"));
        await request;
        Assert.False(vm.IsWeatherVisible);
        Assert.Empty(vm.WeatherTooltip);
        await vm.RefreshWeatherAsync("关闭后不再请求");
        weather.Verify(x => x.GetWeatherAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>结果必须经指定的界面调度器执行，不能在请求完成线程直接赋值。</summary>
    [Fact]
    public async Task Refresh_AppliesResultOnUiScheduler()
    {
        var scheduler = new HistoricalScheduler();
        var weather = new Mock<IWeatherService>();
        weather.Setup(x => x.GetWeatherAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WeatherInfo(WeatherKind.Sunny, 25, "界面城市"));
        using var vm = new WeatherViewModel(weather.Object, scheduler);
        var request = vm.RefreshWeatherAsync(null);
        Assert.False(vm.IsWeatherVisible);
        scheduler.Start();
        await request;
        Assert.StartsWith("界面城市", vm.WeatherTooltip);
    }
}
