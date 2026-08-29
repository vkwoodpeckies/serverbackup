namespace BackupManager.Core;
public static class ScheduleCalculator
{
    public static string FrequencySuffix(Schedule schedule) => schedule.Kind switch
    {
        "EveryMinutes" when schedule.EveryMinutes is > 0 => $"_{schedule.EveryMinutes}min",
        "EveryHours" when schedule.EveryHours is > 0 => $"_{schedule.EveryHours}hr",
        "Hourly" => "_1hr",
        "Daily" => "_daily",
        "Weekly" => "_weekly",
        "Monthly" => "_monthly",
        _ => "_manual"
    };
    public static DateTimeOffset? Next(Schedule schedule, DateTimeOffset from)
    {
        // BackupResult timestamps are stored in UTC, but a schedule is defined by the
        // server's local wall clock. Always align boundaries after converting to local time.
        from = from.ToLocalTime();
        return schedule.Kind switch
        {
            "Manual" => null,
            "Hourly" => NextHourBoundary(from, 1),
            "EveryHours" when schedule.EveryHours is > 0 => NextHourBoundary(from, schedule.EveryHours.Value),
            "EveryMinutes" when schedule.EveryMinutes is > 0 => NextMinuteBoundary(from, schedule.EveryMinutes.Value),
            "Daily" => AtTime(from.AddDays(1), schedule.Time),
            "Weekly" => NextDay(from, schedule.Day ?? DayOfWeek.Sunday, schedule.Time),
            "Monthly" => AtTime(new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, from.Offset).AddMonths(1), schedule.Time),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), "Unsupported schedule.")
        };
    }
    private static DateTimeOffset AtTime(DateTimeOffset day, TimeOnly? time) { var t = time ?? new TimeOnly(2, 0); return new DateTimeOffset(day.Year, day.Month, day.Day, t.Hour, t.Minute, 0, day.Offset); }
    private static DateTimeOffset NextDay(DateTimeOffset from, DayOfWeek day, TimeOnly? time) { var candidate = AtTime(from, time); while (candidate <= from || candidate.DayOfWeek != day) candidate = candidate.AddDays(1); return candidate; }
    private static DateTimeOffset NextMinuteBoundary(DateTimeOffset from, int interval)
    {
        // Minute schedules are anchored to the clock (00:00), not to completion time.
        // Thus 13:04 -> 13:05 -> 13:10 for a five-minute schedule.
        var midnight = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, from.Offset);
        var elapsedMinutes = (from - midnight).TotalMinutes;
        var nextSlot = Math.Ceiling(elapsedMinutes / interval) * interval;
        var candidate = midnight.AddMinutes(nextSlot);
        return candidate <= from ? candidate.AddMinutes(interval) : candidate;
    }
    private static DateTimeOffset NextHourBoundary(DateTimeOffset from, int interval)
    {
        var midnight = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, from.Offset);
        var elapsedHours = (from - midnight).TotalHours;
        var candidate = midnight.AddHours(Math.Ceiling(elapsedHours / interval) * interval);
        return candidate <= from ? candidate.AddHours(interval) : candidate;
    }
}
