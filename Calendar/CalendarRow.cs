using System;

namespace ScrewCalendar;

public sealed class CalendarRow
{
    public DateTime?[] Dates { get; } = new DateTime?[7];
    public string? MonthLabel { get; set; }
}
