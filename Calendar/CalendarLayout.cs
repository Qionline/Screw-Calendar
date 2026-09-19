using System;

namespace ScrewCalendar;

public static class CalendarLayout
{
    // Fixed design coordinates. Keep synchronized with UI_STYLE_CONTRACT.md.
    public const int MinimumRows = 5;
    public const int MaximumRows = 10;
    public const double CardWidth = 886;
    public const double GridHorizontalMargin = 16;
    public const double GridBottomMargin = 16;
    public const double CellWidth = 118;
    public const double CellMargin = 2;
    public const double ColumnWidth = 122;
    public const double GridWidth = 854;
    public const double CellHeight = 105;
    public const double MonthLabelHeight = 22;
    public const double TodoPanelDefaultHeight = 220;
    public const double TodoPanelMinHeight = 140;
    public const double TodoPanelMaxHeight = 520;
    public const double TodoResizeGripHeight = 8;
    public const double MinimumWindowWidth = 300;
    public const double MinimumWindowHeight = 280;
    public const double DialogHorizontalPadding = 16;
    public const double DialogVerticalPadding = 14;
    public const double SettingsDialogLeftPadding = 20;
    public const double SettingsScrollBarLeftMargin = 12;
    public const int MaximumMarkdownLength = 100000;

    public static int ClampRows(int rows) => Math.Clamp(rows, MinimumRows, MaximumRows);
}
