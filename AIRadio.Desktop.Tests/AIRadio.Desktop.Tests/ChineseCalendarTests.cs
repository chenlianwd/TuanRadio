using System;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ChineseCalendarTests
{
    /// <summary>香港天文台年历锚点：闰年的一二月尚未经历当年闰日。</summary>
    [Theory]
    [InlineData(2024, 1, 6, "小寒")]
    [InlineData(2024, 1, 20, "大寒")]
    [InlineData(2024, 2, 4, "立春")]
    [InlineData(2024, 2, 19, "雨水")]
    [InlineData(2028, 1, 6, "小寒")]
    [InlineData(2028, 1, 20, "大寒")]
    [InlineData(2028, 2, 4, "立春")]
    [InlineData(2028, 2, 19, "雨水")]
    [InlineData(2026, 2, 18, "雨水")]
    public void GetInfo_EarlyYearSolarTerms_MatchAlmanac(int year, int month, int day, string term)
    {
        // 来源：https://www.hko.gov.hk/tc/gts/time/calendar/pdf/files/{year}.pdf
        var date = new DateTime(year, month, day);
        Assert.Equal(term, ChineseCalendar.GetInfo(date)!.SolarTerm);
        Assert.NotEqual(term, ChineseCalendar.GetInfo(date.AddDays(-1))!.SolarTerm);
        Assert.NotEqual(term, ChineseCalendar.GetInfo(date.AddDays(1))!.SolarTerm);
    }

    /// <summary>闰五月初五不重复庆祝端午，普通五月初五仍正常识别。</summary>
    [Fact]
    public void GetInfo_LeapFifthMonth_DoesNotRepeatDragonBoatFestival()
    {
        var calendar = new System.Globalization.ChineseLunisolarCalendar();
        var leapMonth = calendar.GetLeapMonth(2028);
        Assert.Equal(6, leapMonth);
        var normalDay = calendar.ToDateTime(2028, 5, 5, 0, 0, 0, 0);
        var leapDay = calendar.ToDateTime(2028, leapMonth, 5, 0, 0, 0, 0);
        Assert.Equal("端午节", ChineseCalendar.GetInfo(normalDay)!.Festival);
        Assert.Equal("闰五月初五", ChineseCalendar.GetInfo(leapDay)!.LunarText);
        Assert.Null(ChineseCalendar.GetInfo(leapDay)!.Festival);
    }

    [Fact]
    public void GetInfo_SpringFestival2026_IsFirstDayOfFirstLunarMonth()
    {
        var info = ChineseCalendar.GetInfo(new DateTime(2026, 2, 17));

        Assert.NotNull(info);
        Assert.Equal("正月初一", info.LunarText);
        Assert.Equal("春节", info.Festival);
        Assert.True(info.IsHighlighted);
    }

    [Fact]
    public void GetInfo_DayBeforeSpringFestival2026_IsChineseNewYearEve()
    {
        var info = ChineseCalendar.GetInfo(new DateTime(2026, 2, 16));

        Assert.NotNull(info);
        Assert.Equal("除夕", info.Festival);
    }

    [Fact]
    public void GetInfo_MidAutumn2025_IsEighthMonthFifteenth()
    {
        var info = ChineseCalendar.GetInfo(new DateTime(2025, 10, 6));

        Assert.NotNull(info);
        Assert.Equal("八月十五", info.LunarText);
        Assert.Equal("中秋节", info.Festival);
    }

    [Theory]
    [InlineData(2024, 12, 21, "冬至")]
    [InlineData(2025, 2, 3, "立春")]
    [InlineData(2025, 4, 4, "清明")]
    [InlineData(2026, 12, 22, "冬至")]
    [InlineData(2026, 9, 23, "秋分")]
    public void GetInfo_SolarTermAnchors(int year, int month, int day, string expectedTerm)
    {
        var info = ChineseCalendar.GetInfo(new DateTime(year, month, day));

        Assert.NotNull(info);
        Assert.Equal(expectedTerm, info.SolarTerm);
    }

    [Fact]
    public void GetInfo_NationalDay2026_HighlightedWithWeekendDistance()
    {
        // 2026-10-01 是周四：距周六 2 天
        var info = ChineseCalendar.GetInfo(new DateTime(2026, 10, 1));

        Assert.NotNull(info);
        Assert.Equal(1, info.Day);
        Assert.Equal("国庆节", info.Festival);
        Assert.Equal(2, info.DaysToSaturday);
    }

    [Fact]
    public void GetInfo_OrdinaryDay_NoHighlight()
    {
        var info = ChineseCalendar.GetInfo(new DateTime(2026, 9, 18));

        Assert.NotNull(info);
        Assert.Null(info.SolarTerm);
        Assert.Null(info.Festival);
        Assert.False(info.IsHighlighted);
        // 2026-09-18 周五：距周六 1 天
        Assert.Equal(1, info.DaysToSaturday);
    }

    [Fact]
    public void GetInfo_SaturdayItself_HasZeroDaysToWeekend()
    {
        // 2026-09-19 是周六
        var info = ChineseCalendar.GetInfo(new DateTime(2026, 9, 19));

        Assert.NotNull(info);
        Assert.Equal(0, info.DaysToSaturday);
    }

    [Fact]
    public void GetInfo_LeapLunarMonth2025_IsPrefixedWithRun()
    {
        // 2025 农历有闰六月（闰六月初一=2025-07-25）：闰月内的日期文本带"闰"前缀且月份不串位
        var info = ChineseCalendar.GetInfo(new DateTime(2025, 8, 1));

        Assert.NotNull(info);
        Assert.StartsWith("闰六月", info.LunarText);
    }

    [Fact]
    public void GetInfo_BeforeLunarCalendarSupport_ReturnsNull()
    {
        Assert.Null(ChineseCalendar.GetInfo(new DateTime(1899, 6, 1)));
    }
}
