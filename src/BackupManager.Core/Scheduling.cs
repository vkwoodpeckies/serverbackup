namespace BackupManager.Core;
public static class ScheduleCalculator
{
    public static DateTimeOffset? Next(Schedule schedule, DateTimeOffset from)
    {
        return schedule.Kind switch
        {
            "Manual" => null,
            "Hourly" => from.AddHours(1),
            "EveryHours" when schedule.EveryHours is > 0 => from.AddHours(schedule.EveryHours.Value),
            "Daily" => AtTime(from.AddDays(1), schedule.Time),
            "Weekly" => NextDay(from, schedule.Day ?? DayOfWeek.Sunday, schedule.Time),
            "Monthly" => AtTime(new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, from.Offset).AddMonths(1), schedule.Time),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), "Unsupported schedule.")
        };
    }
    private static DateTimeOffset AtTime(DateTimeOffset day, TimeOnly? time) { var t = time ?? new TimeOnly(2, 0); return new DateTimeOffset(day.Year, day.Month, day.Day, t.Hour, t.Minute, 0, day.Offset); }
    private static DateTimeOffset NextDay(DateTimeOffset from, DayOfWeek day, TimeOnly? time) { var candidate = AtTime(from, time); while (candidate <= from || candidate.DayOfWeek != day) candidate = candidate.AddDays(1); return candidate; }
}
