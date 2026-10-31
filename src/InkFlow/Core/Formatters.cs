namespace InkFlow.Core;

public static class Formatters
{
    /// <summary>Formats seconds as mm:ss, switching to h:mm:ss after the first hour.</summary>
    public static string Clock(long totalSeconds)
    {
        if (totalSeconds < 0)
        {
            totalSeconds = 0;
        }

        var time = TimeSpan.FromSeconds(totalSeconds);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
        }

        return $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public static string Clock(double totalSeconds) => Clock((long)Math.Round(totalSeconds));

    public static string Count(long value) => value.ToString("N0");

    public static string Speed(double value) => Math.Round(Math.Max(0, value)).ToString("0");

    public static string Percent(double fraction) => $"{Math.Round(Math.Clamp(fraction, 0, 1) * 100):0}%";

    public static string PercentWhole(double fraction) => $"{(int)Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%";

    public static string WeekdayShort(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    public static string DateShort(DateTime date) => $"{date.Month}/{date.Day}";

    public static string FriendlyDate(DateTime date) => $"{date.Month} 月 {date.Day} 日 · {WeekdayShort(date)}";

    public static string TimeOfDay(DateTime time) => time.ToString("HH:mm");

    public static string TodayLabel()
    {
        var today = DateTime.Today;
        return $"{today.Month} 月 {today.Day} 日 {WeekdayShort(today)}";
    }
}
