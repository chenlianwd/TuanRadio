using System;
using System.Globalization;

namespace AIRadio.Desktop.Services;

/// <summary>日历指示器的单日信息（公历日号 + 农历 + 节气 + 节日 + 距周末）。</summary>
public sealed record CalendarDayInfo(
    int Day,
    string LunarText,
    int LunarMonth,
    int LunarDay,
    bool IsLeapMonth,
    string? SolarTerm,
    string? Festival,
    int DaysToSaturday)
{
    /// <summary>徽标高亮：当天有节气或节日。</summary>
    public bool IsHighlighted => SolarTerm != null || Festival != null;
}

/// <summary>
/// 纯本地农历/节气/节日计算（docs/plans/2026-09-17-weather-calendar-design.md §2）。
/// 农历用 BCL ChineseLunisolarCalendar（1901-2100）；节气用寿星通用公式（2001-2099 适用）。
/// </summary>
public static class ChineseCalendar
{
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] LunarMonths =
        { "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月" };

    private static readonly string[] LunarDays =
    {
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    // 节气名按年内先后（小寒→冬至）；公历月-起点旬用于快速定位
    private static readonly string[] SolarTerms =
    {
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨",
        "立夏", "小满", "芒种", "夏至", "小暑", "大暑", "立秋", "处暑",
        "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至"
    };

    // 寿星公式 21 世纪 C 值（2001-2099 适用），与 SolarTerms 一一对应
    private static readonly double[] CenturyC =
    {
        5.4055, 20.12, 3.87, 18.73, 5.63, 20.646, 4.81, 20.1,
        5.52, 21.04, 5.678, 21.37, 7.108, 22.83, 7.5, 23.13,
        7.646, 23.042, 8.318, 23.438, 7.438, 22.36, 7.18, 21.94
    };

    /// <summary>返回指定公历日期的农历、节日及节气信息。</summary>
    public static CalendarDayInfo? GetInfo(DateTime date)
    {
        if (!TryGetLunar(date, out var month, out var day, out var isLeap) || month < 1 || month > 12 || day is < 1 or > 30)
            return null;

        var monthName = (isLeap ? "闰" : string.Empty) + LunarMonths[month - 1];
        var festival = GetFestival(date, month, day, isLeap);
        var solarTerm = GetSolarTerm(date);

        // 距周六天数（周六为 0）
        var daysToSaturday = ((6 - (int)date.DayOfWeek) % 7 + 7) % 7;

        return new CalendarDayInfo(
            date.Day,
            $"{monthName}{LunarDays[day - 1]}",
            month,
            day,
            isLeap,
            solarTerm,
            festival,
            daysToSaturday);
    }

    private static bool TryGetLunar(DateTime date, out int month, out int day, out bool isLeap)
    {
        month = 0;
        day = 0;
        isLeap = false;
        try
        {
            var lunarYear = Calendar.GetYear(date);
            var rawMonth = Calendar.GetMonth(date);
            day = Calendar.GetDayOfMonth(date);
            var leap = Calendar.GetLeapMonth(lunarYear);
            if (leap > 0)
            {
                if (rawMonth == leap)
                {
                    isLeap = true;
                    rawMonth -= 1;
                }
                else if (rawMonth > leap)
                {
                    rawMonth -= 1;
                }
            }
            month = rawMonth;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // 超出 BCL 农历支持范围（1901-2100）
            return false;
        }
    }

    private static string? GetSolarTerm(DateTime date)
    {
        if (date.Year is < 2001 or > 2099)
            return null;

        for (var termIndex = 0; termIndex < 24; termIndex++)
        {
            if (MonthOfTerm(termIndex) != date.Month)
                continue;
            if (SolarTermDay(date.Year, termIndex) == date.Day)
                return SolarTerms[termIndex];
        }

        return null;
    }

    private static int MonthOfTerm(int termIndex) => termIndex switch
    {
        0 or 1 => 1,
        2 or 3 => 2,
        4 or 5 => 3,
        6 or 7 => 4,
        8 or 9 => 5,
        10 or 11 => 6,
        12 or 13 => 7,
        14 or 15 => 8,
        16 or 17 => 9,
        18 or 19 => 10,
        20 or 21 => 11,
        _ => 12
    };

    /// <summary>寿星近似公式：一二月只扣除此前已发生的闰日，特殊年份按年历校正。</summary>
    private static int SolarTermDay(int year, int termIndex)
    {
        var y = year % 100;
        var leapDays = (termIndex < 4 ? y - 1 : y) / 4;
        var day = (int)(y * 0.2422 + CenturyC[termIndex]) - leapDays;
        // 香港天文台 2026 年历：雨水为 2 月 18 日，通用近似公式会多算一天。
        // https://www.hko.gov.hk/tc/gts/time/calendar/pdf/files/2026.pdf
        return year == 2026 && termIndex == 3 ? day - 1 : day;
    }

    /// <summary>农历节日只在正月序出现；闰月仍可显示公历节日。</summary>
    private static string? GetFestival(DateTime date, int lunarMonth, int lunarDay, bool isLeap)
    {
        // 除夕优先（腊月最后一日，次日为正月初一）
        if (lunarMonth == 12 && TryGetLunar(date.AddDays(1), out var nextMonth, out var nextDay, out var nextIsLeap) &&
            nextMonth == 1 && nextDay == 1 && !nextIsLeap)
            return "除夕";

        var lunar = isLeap ? null : (lunarMonth, lunarDay) switch
        {
            (1, 1) => "春节",
            (1, 15) => "元宵节",
            (5, 5) => "端午节",
            (7, 7) => "七夕",
            (8, 15) => "中秋节",
            (9, 9) => "重阳节",
            (12, 8) => "腊八节",
            _ => null
        };
        if (lunar != null)
            return lunar;

        return (date.Month, date.Day) switch
        {
            (1, 1) => "元旦",
            (5, 1) => "劳动节",
            (6, 1) => "儿童节",
            (10, 1) => "国庆节",
            _ => null
        };
    }
}
