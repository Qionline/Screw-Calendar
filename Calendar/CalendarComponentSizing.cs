using System;

namespace ScrewCalendar;

public readonly record struct CalendarDisplaySize(double Width, double CalendarHeight, double TodoHeight)
{
    public double TotalHeight => CalendarHeight + TodoHeight;
}

public static class CalendarComponentSizing
{
    public static CalendarDisplaySize FitCalendarWidth(
        double requestedWidth,
        double calendarDesignHeight,
        double todoHeight,
        double minimumWidth,
        double maximumTotalHeight)
    {
        if (calendarDesignHeight <= 0) throw new ArgumentOutOfRangeException(nameof(calendarDesignHeight));
        var ratio = CalendarLayout.CardWidth / calendarDesignHeight;
        var width = Math.Max(minimumWidth, requestedWidth);
        var calendarHeight = width / ratio;
        var minimumCalendarHeight = minimumWidth / ratio;
        var effectiveTodoHeight = Math.Max(0, todoHeight);
        if (minimumCalendarHeight + effectiveTodoHeight > maximumTotalHeight)
            effectiveTodoHeight = Math.Max(0, maximumTotalHeight - minimumCalendarHeight);
        var maximumCalendarHeight = Math.Max(minimumCalendarHeight, maximumTotalHeight - effectiveTodoHeight);
        if (calendarHeight > maximumCalendarHeight)
        {
            calendarHeight = maximumCalendarHeight;
            width = calendarHeight * ratio;
        }
        return new CalendarDisplaySize(width, calendarHeight, effectiveTodoHeight);
    }

    public static double TodoPanelWidth(double calendarDisplayWidth, bool alignToCalendarContent) =>
        calendarDisplayWidth * (alignToCalendarContent ? CalendarLayout.GridWidth / CalendarLayout.CardWidth : 1);
}
