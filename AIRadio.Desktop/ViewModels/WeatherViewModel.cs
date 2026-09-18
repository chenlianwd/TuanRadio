using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace AIRadio.Desktop.ViewModels;

/// <summary>
/// 时钟舞台角落的环境指示器（天气图标 + 日历徽标，docs/plans/2026-09-17-weather-calendar-design.md）。
/// 默认只有图标，详情经悬停 Tooltip 展示；天气失败只隐藏图标，不打扰播放主流程。
/// </summary>
public class WeatherViewModel : ViewModelBase, IDisposable
{
    private readonly IWeatherService _weather;
    private readonly IDisposable _refreshTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly IScheduler _uiScheduler;
    private int _generation;
    private int _disposed;
    private string? _lastCity;
    private WeatherInfo? _lastWeather;
    private CalendarDayInfo? _lastCalendar;
    private DateTime _lastCalendarDate;

    [Reactive] public bool IsWeatherVisible { get; private set; }
    [Reactive] public string WeatherGlyph { get; private set; } = string.Empty;
    [Reactive] public string WeatherTooltip { get; private set; } = string.Empty;
    [Reactive] public string CalendarDayBadge { get; private set; } = string.Empty;
    [Reactive] public string CalendarTooltip { get; private set; } = string.Empty;
    [Reactive] public bool IsCalendarHighlighted { get; private set; }

    /// <summary>创建环境指示器，所有异步天气结果均交回界面调度器。</summary>
    public WeatherViewModel(IWeatherService weather, IScheduler? uiScheduler = null)
    {
        _weather = weather;
        _uiScheduler = uiScheduler ?? RxApp.MainThreadScheduler;
        _lifetimeToken = _lifetimeCts.Token;
        UpdateCalendar(DateTime.Now);

        // 30 分钟周期刷新（服务层有同周期缓存，命中时零网络）；异常全内吞
        _refreshTimer = Observable.Interval(TimeSpan.FromMinutes(30))
            .ObserveOn(_uiScheduler)
            .Subscribe(tick => _ = RefreshWeatherAsync(_lastCity));
    }

    /// <summary>启动与设置页城市变更时触发；城市为空走 IP 定位。</summary>
    public async Task RefreshWeatherAsync(string? city)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var generation = Interlocked.Increment(ref _generation);
        _lastCity = city;
        WeatherInfo? result = null;
        try
        {
            result = await _weather.GetWeatherAsync(city, _lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 服务层已静默；这里防御 VM 层意外
        }
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _generation) != generation)
            return;
        await Observable.Start(() =>
        {
            // 城市切换、周期刷新和关闭均可能使请求过期；判定与属性更新一起在 UI 上执行。
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _generation) != generation)
                return;
            _lastWeather = result;
            ApplyWeather();
        }, _uiScheduler).ToTask().ConfigureAwait(false);
    }

    /// <summary>日期变更时重算日历徽标（由主窗口 1s 时钟推进驱动）。</summary>
    public void UpdateCalendar(DateTime now)
    {
        if (now.Date == _lastCalendarDate)
            return;
        _lastCalendarDate = now.Date;
        _lastCalendar = ChineseCalendar.GetInfo(now);
        ApplyCalendar();
    }

    /// <summary>语言切换时经主窗口回调重建双语 Tooltip。</summary>
    public void RebuildTooltips()
    {
        ApplyWeather();
        ApplyCalendar();
    }

    private void ApplyWeather()
    {
        if (_lastWeather == null)
        {
            IsWeatherVisible = false;
            WeatherTooltip = string.Empty;
            return;
        }

        IsWeatherVisible = true;
        WeatherGlyph = _lastWeather.Kind switch
        {
            WeatherKind.Sunny => "☀",
            WeatherKind.Cloudy => "⛅",
            WeatherKind.Overcast => "☁",
            WeatherKind.Rain => "☂",
            WeatherKind.Snow => "❄",
            WeatherKind.Thunder => "⚡",
            WeatherKind.Fog => "☁",
            _ => string.Empty
        };
        var description = _lastWeather.Kind switch
        {
            WeatherKind.Sunny => AppLanguage.T("晴", "Clear"),
            WeatherKind.Cloudy => AppLanguage.T("多云", "Partly cloudy"),
            WeatherKind.Overcast => AppLanguage.T("阴", "Overcast"),
            WeatherKind.Rain => AppLanguage.T("雨", "Rain"),
            WeatherKind.Snow => AppLanguage.T("雪", "Snow"),
            WeatherKind.Thunder => AppLanguage.T("雷雨", "Thunderstorm"),
            WeatherKind.Fog => AppLanguage.T("雾", "Fog"),
            _ => AppLanguage.T("未知", "Unknown")
        };
        var temperature = double.IsNaN(_lastWeather.TemperatureC)
            ? string.Empty
            : $" {_lastWeather.TemperatureC:0}°C";
        WeatherTooltip = $"{_lastWeather.LocationName} · {description}{temperature}";
    }

    private void ApplyCalendar()
    {
        if (_lastCalendar == null)
        {
            CalendarDayBadge = string.Empty;
            CalendarTooltip = string.Empty;
            IsCalendarHighlighted = false;
            return;
        }

        var info = _lastCalendar;
        CalendarDayBadge = info.Day.ToString();
        IsCalendarHighlighted = info.IsHighlighted;

        var weekText = AppLanguage.Current == "en"
            ? $"{_lastCalendarDate.ToString("M/d", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} {_lastCalendarDate.ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}"
            : _lastCalendarDate.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        var parts = new[]
        {
            weekText,
            AppLanguage.T($"农历 {info.LunarText}", $"Lunar {info.LunarText}"),
            info.SolarTerm ?? string.Empty,
            info.Festival ?? string.Empty,
            info.DaysToSaturday == 0
                ? AppLanguage.T("今天是周六", "It's Saturday")
                : AppLanguage.T($"距周六 {info.DaysToSaturday} 天", $"{info.DaysToSaturday} day(s) to Saturday")
        };
        CalendarTooltip = string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>停止刷新并作废全部在途结果，允许重复释放。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Increment(ref _generation);
        _lifetimeCts.Cancel();
        _refreshTimer.Dispose();
        _lifetimeCts.Dispose();
    }
}
