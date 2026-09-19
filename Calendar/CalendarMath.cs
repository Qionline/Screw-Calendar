using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScrewCalendar;

public static class CalendarMath
{
    public static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static int MondayColumn(DateTime date) => ((int)date.DayOfWeek + 6) % 7;
    public static DateTime MondayOf(DateTime date) => date.Date.AddDays(-MondayColumn(date));

    public static List<CalendarRow> BuildRows(DateTime anchor, int count, bool home)
    {
        var current = home ? MondayOf(anchor).AddDays(-7) : new DateTime(anchor.Year, anchor.Month, 1);
        var rows = new List<CalendarRow>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new CalendarRow();
            var column = MondayColumn(current);
            var month = current.Month;
            row.MonthLabel = current.Day == 1 ? current.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null;
            while (column < 7 && current.Month == month)
            {
                row.Dates[column] = current;
                current = current.AddDays(1);
                column++;
            }
            rows.Add(row);
        }
        return rows;
    }
}
