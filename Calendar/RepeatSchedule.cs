using System;

namespace ScrewCalendar;

public static class RepeatSchedule
{
    public static bool Matches(RepeatBlock rule, DateOnly date)
    {
        if (!rule.IsValid || rule.StartDate is not { } start || date < start || rule.EndDate is { } end && date > end) return false;
        return rule.Type switch
        {
            "d" => true,
            "w" => date.DayOfWeek == start.DayOfWeek,
            // Always use the original anchor day; February must not shift March to the 28th.
            "m" => date.Day == Math.Min(start.Day, DateTime.DaysInMonth(date.Year, date.Month)),
            _ => false
        };
    }
}
