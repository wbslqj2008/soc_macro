namespace SocMacro.Core;

/// <summary>
/// Daily scheduled start, evaluated in Korea Standard Time regardless of the PC's time zone.
/// Pure logic (no UI) so it can be reasoned about and tested on its own.
/// </summary>
public static class Schedule
{
    /// <summary>
    /// How late the program may notice the scheduled time and still start. If the PC/program was off
    /// at the scheduled time and is turned on later, the run is skipped for that day instead of
    /// starting the macro out of the blue.
    /// </summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(10);

    private static readonly TimeZoneInfo Kst = FindKst();

    private static TimeZoneInfo FindKst()
    {
        foreach (var id in new[] { "Korea Standard Time", "Asia/Seoul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    }

    public static DateTime NowKst() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Kst);

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>
    /// Whether the schedule should fire now. days is indexed by DayOfWeek (Sunday = 0);
    /// lastRun is the KST date (yyyy-MM-dd) it last fired.
    /// </summary>
    public static bool ShouldFire(DateTime nowKst, TimeSpan time, bool[] days, string lastRun)
    {
        if (days.Length != 7 || !days[(int)nowKst.DayOfWeek]) return false;
        if (lastRun == nowKst.ToString("yyyy-MM-dd")) return false;
        var at = nowKst.Date + time;
        return nowKst >= at && nowKst < at + GraceWindow;
    }

    /// <summary>The next scheduled start after nowKst, or null if no weekday is enabled.</summary>
    public static DateTime? Next(DateTime nowKst, TimeSpan time, bool[] days, string lastRun)
    {
        if (days.Length != 7) return null;
        for (int i = 0; i < 8; i++)
        {
            var day = nowKst.Date.AddDays(i);
            if (!days[(int)day.DayOfWeek]) continue;
            var at = day + time;
            bool doneToday = i == 0 && lastRun == day.ToString("yyyy-MM-dd");
            if (!doneToday && at + GraceWindow > nowKst) return at;
        }
        return null;
    }
}
