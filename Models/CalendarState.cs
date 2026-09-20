using System.Collections.Generic;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

public enum CalendarStyle { Windows, Minimal, Widget }
public enum CalendarTheme { Light, Dark }
public enum CalendarThemeColor { Blue, Teal, Purple, Orange, Gray, Red }
public enum CalendarLanguage { ChineseSimplified, English }

public sealed class CalendarState
{
    public int Version { get; set; } = 3;
    public int Rows { get; set; } = 7;
    public CalendarStyle Style { get; set; } = CalendarStyle.Windows;
    public CalendarTheme Theme { get; set; } = CalendarTheme.Light;
    public CalendarThemeColor ThemeColor { get; set; } = CalendarThemeColor.Blue;
    public CalendarLanguage Language { get; set; } = CalendarLanguage.ChineseSimplified;
    public double Opacity { get; set; } = 1.0;
    public bool Locked { get; set; }
    public bool Topmost { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ShowTodoPanel { get; set; }
    public double TodoPanelHeight { get; set; } = CalendarLayout.TodoPanelDefaultHeight;
    public WindowPlacementState Geometry { get; set; } = new() { Left = 80, Top = 80, Width = 980, Height = 890 };
    public string PermanentMarkdown { get; set; } = string.Empty;
    public Dictionary<string, string> DateMarkdown { get; set; } = [];
}
