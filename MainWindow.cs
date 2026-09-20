using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopWidgets.Toolkit.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ScrewCalendar;

// Main window shell and calendar rendering. Auxiliary responsibilities live in partial view files and services.
public sealed partial class MainWindow : Window
{
    private readonly CalendarState _state;
    private readonly CalendarDataStore _dataStore;
    private readonly MarkdownImageStore _imageStore;
    private readonly CalendarMetadataService _metadata;
    private readonly HolidayUpdateService _holidayUpdates;
    private readonly TrayIconService _tray = new();
    private readonly WindowLayerController _windowLayerController;
    private readonly FontFamily _uiFont;
    private readonly Border _card;
    private readonly Border _toolbar;
    private readonly Border _weekdayBar;
    private readonly Grid _weekdayPanel;
    private readonly Grid _datesGrid;
    private readonly Grid _outer;
    private readonly Viewbox _calendarViewbox;
    private readonly RowDefinition _calendarRow;
    private readonly RowDefinition _todoRow;
    private readonly Dictionary<string, Border> _dateCells = new(StringComparer.Ordinal);
    private Border _todoPanel = null!;
    private Border _todoResizeGrip = null!;
    private Viewbox _calendarLogo = null!;
    private TextBlock _currentDateText = null!;
    private TextBlock _currentWeekText = null!;
    private TextBlock _viewMonthText = null!;
    private Border _toolbarSeparator = null!;
    private Button _styleButton = null!;
    private Button _previousButton = null!;
    private Button _nextButton = null!;
    private Button _todayButton = null!;
    private Button _themeButton = null!;
    private Button _lockButton = null!;
    private Button _settingsButton = null!;
    private readonly Border _resizeGrip;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _today = DateTime.Today;
    private DateTime _anchor = DateTime.Today;
    private bool _home = true;
    private bool _allowExit;
    private bool _todoEditorActive;
    private double _designHeight;
    private Point _resizeStart;
    private double _resizeWidth;
    private double _calendarDisplayWidth;
    private double _calendarDisplayHeight;
    private Point _todoResizeStart;
    private double _todoResizeHeight;
    private Window? _settingsDialog;
    private readonly Dictionary<string, Window> _markdownEditorDialogs = new(StringComparer.Ordinal);
    private string? _todayMarkdownPreview;
    private Action<string>? _todayMarkdownPreviewSink;

    private static readonly string[] WeekdayKeys = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public MainWindow()
    {
        _uiFont = (FontFamily)Application.Current.Resources["UiFont"];
        _dataStore = CalendarDataStore.CreateDefault();
        _imageStore = new MarkdownImageStore(_dataStore.AssetsDirectory);
        _metadata = CalendarMetadataService.CreateDefault();
        _holidayUpdates = new HolidayUpdateService(_metadata);
        _holidayUpdates.DataUpdated += (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Render);
        };
        _state = _dataStore.Load();
        _windowLayerController = new WindowLayerController(this, allowFullscreenCover: true);
        _windowLayerController.SetAlwaysOnTop(_state.Topmost);
        Localization.SetLanguage(_state.Language);
        UpdateThemeResources();
        _state.Rows = CalendarLayout.ClampRows(_state.Rows);
        _state.Opacity = Math.Clamp(_state.Opacity, .4, 1.0);
        _state.TodoPanelHeight = Math.Clamp(_state.TodoPanelHeight, CalendarLayout.TodoPanelMinHeight, CalendarLayout.TodoPanelMaxHeight);
        Title = Localization.T("app.title");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = _state.Topmost;
        ShowActivated = _state.Topmost;
        MinWidth = CalendarLayout.MinimumWindowWidth;
        MinHeight = CalendarLayout.MinimumWindowHeight;
        Width = Math.Max(MinWidth, _state.Geometry.Width);
        Height = Math.Max(MinHeight, _state.Geometry.Height);

        _outer = new Grid { Background = Brushes.Transparent };
        _calendarRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        _todoRow = new RowDefinition { Height = new GridLength(0) };
        _outer.RowDefinitions.Add(_calendarRow);
        _outer.RowDefinitions.Add(_todoRow);
        var calendarLayer = new Grid { Background = Brushes.Transparent };
        _card = new Border { Width = CalendarLayout.CardWidth, SnapsToDevicePixels = true };
        _calendarViewbox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Child = _card };
        calendarLayer.Children.Add(_calendarViewbox);
        _resizeGrip = new Border { Width = 23, Height = 23, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Background = Brushes.Transparent };
        _resizeGrip.MouseLeftButtonDown += ResizeGripMouseDown;
        _resizeGrip.MouseMove += ResizeGripMouseMove;
        _resizeGrip.MouseLeftButtonUp += ResizeGripMouseUp;
        calendarLayer.Children.Add(_resizeGrip);
        Grid.SetRow(calendarLayer, 0);
        _outer.Children.Add(calendarLayer);
        var todoLayer = BuildTodoPanel();
        Grid.SetRow(todoLayer, 1);
        _outer.Children.Add(todoLayer);
        Content = _outer;

        var inner = new Grid { ClipToBounds = true };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(43) });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _card.Child = inner;

        _toolbar = BuildToolbar();
        Grid.SetRow(_toolbar, 0);
        inner.Children.Add(_toolbar);

        _weekdayPanel = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        AddCalendarColumns(_weekdayPanel);
        for (var i = 0; i < 7; i++)
        {
            var text = Text(WeekdayName(i), 13, FontWeights.SemiBold, WeekdayForeground(i));
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(11, 0, 0, 0);
            var header = new Border { Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Child = text };
            Grid.SetColumn(header, i);
            _weekdayPanel.Children.Add(header);
        }
        _weekdayBar = new Border
        {
            Child = _weekdayPanel,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(_weekdayBar, 1);
        inner.Children.Add(_weekdayBar);

        _datesGrid = new Grid { Margin = new Thickness(CalendarLayout.GridHorizontalMargin, 0, CalendarLayout.GridHorizontalMargin, CalendarLayout.GridBottomMargin) };
        Grid.SetRow(_datesGrid, 2);
        inner.Children.Add(_datesGrid);

        _clock.Tick += (_, _) => RefreshToday();
        _clock.Start();
        Loaded += (_, _) =>
        {
            RestoreGeometry();
            Render();
            CreateTray();
            Visibility = Visibility.Visible;
            WindowState = WindowState.Normal;
            ShowInTaskbar = false;
            if (_state.Topmost) Activate();
            else _windowLayerController.Refresh();
            _ = _holidayUpdates.RefreshForCalendarYearAsync(_anchor.Year);
        };
        LocationChanged += (_, _) => SaveGeometry();
        SizeChanged += (_, _) => SaveGeometry();
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            WindowState = WindowState.Normal;
            _windowLayerController.Refresh();
        };
    }

    private Border BuildToolbar()
    {
        var toolbar = new Border { Padding = new Thickness(22, 0, 19, 0) };
        toolbar.MouseLeftButtonDown += (_, e) => { if (!_state.Locked && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _calendarLogo = new Viewbox { Width = 37.2, Height = 37.2, Stretch = Stretch.Uniform, Child = BuildThemeLogo(), Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(_calendarLogo);
        var dateStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _currentDateText = Text("", 19, FontWeights.SemiBold, Foreground());
        _currentWeekText = Text("", 13, FontWeights.Normal, Muted());
        dateStack.Children.Add(_currentDateText);
        dateStack.Children.Add(_currentWeekText);
        left.Children.Add(dateStack);
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _viewMonthText = Text("", 14, FontWeights.SemiBold, Foreground());
        _viewMonthText.HorizontalAlignment = HorizontalAlignment.Center;
        _viewMonthText.VerticalAlignment = VerticalAlignment.Center;
        var monthHost = new Border { Width = 112, Height = 34, VerticalAlignment = VerticalAlignment.Center, Child = _viewMonthText };
        right.Children.Add(monthHost);
        _previousButton = Button(Localization.T("nav.previous"), Localization.T("nav.previousTooltip"), 34);
        _previousButton.Click += (_, _) => Navigate(-1);
        _nextButton = Button(Localization.T("nav.next"), Localization.T("nav.nextTooltip"), 34);
        _nextButton.Click += (_, _) => Navigate(1);
        right.Children.Add(_previousButton);
        right.Children.Add(_nextButton);
        _todayButton = Button(Localization.T("nav.today"), Localization.T("nav.todayTooltip"), 58);
        _todayButton.Click += (_, _) => GoToday();
        right.Children.Add(_todayButton);
        _toolbarSeparator = new Border { Width = 1, Height = 22, Margin = new Thickness(7, 0, 7, 0), Background = Line() };
        right.Children.Add(_toolbarSeparator);
        _styleButton = Button(StyleName(), Localization.T("nav.styleTooltip"), 92);
        _styleButton.Click += (_, _) => CycleStyle();
        right.Children.Add(_styleButton);
        _themeButton = Button("◐", Localization.T("nav.themeTooltip"), 34);
        _themeButton.FontSize = 19;
        _themeButton.Click += (_, _) => { _state.Theme = _state.Theme == CalendarTheme.Light ? CalendarTheme.Dark : CalendarTheme.Light; SaveAndRender(); };
        right.Children.Add(_themeButton);
        _lockButton = Button(string.Empty, string.Empty, 34);
        _lockButton.FontSize = 16;
        _lockButton.Click += (_, _) => SetLocked(!_state.Locked);
        right.Children.Add(_lockButton);
        _settingsButton = Button("⚙", Localization.T("nav.settingsTooltip"), 34);
        _settingsButton.FontSize = 18;
        _settingsButton.Click += (_, _) => ShowSettings(Mouse.GetPosition(this));
        right.Children.Add(_settingsButton);
        UpdateLockButtonVisual();
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        toolbar.Child = row;
        return toolbar;
    }

    private string WeekdayName(int index) => Localization.T($"weekday.{WeekdayKeys[index]}");

    private string FormatCurrentDate(DateTime date) => Localization.Current == CalendarLanguage.English
        ? date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
        : $"{date:yyyy年M月d日}";

    private string FormatMonth(DateTime date) => Localization.Current == CalendarLanguage.English
        ? date.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
        : $"{date:yyyy年 M月}";

    private string FormatDay(DateTime date) => Localization.Current == CalendarLanguage.English
        ? date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)
        : $"{date:yyyy年M月d日}";

    private void RefreshLocalizedUi()
    {
        Title = Localization.T("app.title");
        _previousButton.Content = Localization.T("nav.previous");
        _previousButton.ToolTip = Localization.T("nav.previousTooltip");
        _nextButton.Content = Localization.T("nav.next");
        _nextButton.ToolTip = Localization.T("nav.nextTooltip");
        _todayButton.Content = Localization.T("nav.today");
        _todayButton.ToolTip = Localization.T("nav.todayTooltip");
        _styleButton.ToolTip = Localization.T("nav.styleTooltip");
        _themeButton.ToolTip = Localization.T("nav.themeTooltip");
        UpdateLockButtonVisual();
        _settingsButton.ToolTip = Localization.T("nav.settingsTooltip");
        Render();
        CreateTray();
    }

    private void Render()
    {
        _currentDateText.Text = FormatCurrentDate(_today);
        _currentWeekText.Text = WeekdayName(CalendarMath.MondayColumn(_today));
        _viewMonthText.Text = FormatMonth(_anchor);
        _styleButton.Content = StyleName();
        _dateCells.Clear();
        _datesGrid.Children.Clear();
        _datesGrid.RowDefinitions.Clear();
        var rows = CalendarMath.BuildRows(_anchor, _state.Rows, _home);
        foreach (var row in rows)
        {
            var hasLabel = row.MonthLabel is not null;
            var dateRow = hasLabel ? 1 : 0;
            _datesGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CalendarLayout.CellHeight + (hasLabel ? CalendarLayout.MonthLabelHeight : 0)) });
            var holder = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            AddCalendarColumns(holder);
            if (hasLabel) holder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CalendarLayout.MonthLabelHeight) });
            holder.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CalendarLayout.CellHeight) });
            var cells = new Grid();
            AddCalendarColumns(cells);
            if (hasLabel)
            {
                var minimal = _state.Style == CalendarStyle.Minimal;
                var label = new Border
                {
                    Width = CalendarLayout.CellWidth,
                    Background = minimal ? Brushes.Transparent : SurfaceSecondary(),
                    BorderBrush = minimal ? MinimalLine() : Brushes.Transparent,
                    BorderThickness = minimal ? new Thickness(0, 0, 0, 1) : new Thickness(0),
                    CornerRadius = minimal ? new CornerRadius(0) : new CornerRadius(6),
                    Margin = new Thickness(CalendarLayout.CellMargin)
                };
                var firstDate = row.Dates.FirstOrDefault(d => d.HasValue);
                var monthLabel = firstDate.HasValue
                    ? (Localization.Current == CalendarLanguage.English ? firstDate.Value.ToString("MMMM", CultureInfo.InvariantCulture) : $"{firstDate.Value.Month}月")
                    : row.MonthLabel!;
                var labelText = Text(monthLabel, 14, FontWeights.SemiBold, Accent());
                labelText.HorizontalAlignment = HorizontalAlignment.Center;
                labelText.VerticalAlignment = VerticalAlignment.Center;
                label.Child = labelText;
                var labelCol = row.Dates.Select((d, i) => (d, i)).FirstOrDefault(x => x.d.HasValue).i;
                Grid.SetColumn(label, labelCol);
                Grid.SetRow(label, 0);
                holder.Children.Add(label);
            }
            foreach (var date in row.Dates.Select((d, i) => (d, i)))
            {
                var cell = date.d.HasValue
                    ? BuildDateCell(date.d.Value)
                    : new Border { Width = CalendarLayout.CellWidth, Background = Brushes.Transparent, Margin = new Thickness(CalendarLayout.CellMargin) };
                if (date.d is DateTime dateValue)
                    _dateCells[CalendarMath.Key(dateValue)] = cell;
                Grid.SetColumn(cell, date.i);
                Grid.SetRow(cell, dateRow);
                cells.Children.Add(cell);
            }
            Grid.SetRow(cells, dateRow);
            Grid.SetColumnSpan(cells, 7);
            holder.Children.Add(cells);
            Grid.SetRow(holder, _datesGrid.RowDefinitions.Count - 1);
            _datesGrid.Children.Add(holder);
        }
        _designHeight = 115 + rows.Count * CalendarLayout.CellHeight + rows.Count(row => row.MonthLabel is not null) * CalendarLayout.MonthLabelHeight + CalendarLayout.GridBottomMargin;
        _card.Height = _designHeight;
        ResizeWindowToDesign();
        ApplyVisuals();
        RenderTodoPanel();
        UpdateCalendarContentWidths();
    }

    private static void AddCalendarColumns(Grid grid)
    {
        for (var column = 0; column < 7; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CalendarLayout.ColumnWidth) });
    }

    private Border BuildDateCell(DateTime date)
    {
        var key = CalendarMath.Key(date);
        var isToday = key == CalendarMath.Key(_today);
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var minimal = _state.Style == CalendarStyle.Minimal;
        var cell = new Border
        {
            Width = CalendarLayout.CellWidth,
            Margin = new Thickness(CalendarLayout.CellMargin),
            Padding = new Thickness(8, 7, 7, 5),
            CornerRadius = minimal ? new CornerRadius(0) : new CornerRadius(6),
            BorderThickness = minimal ? new Thickness(0, isToday ? 3 : 1, 0, 0) : new Thickness(isToday ? 2 : 1),
            BorderBrush = minimal ? (isToday ? MinimalTodayLine() : MinimalLine()) : (isToday ? Accent() : Line()),
            Background = minimal ? (isToday ? MinimalTodayBackground() : (isWeekend ? MinimalWeekendBackground() : Brushes.Transparent)) : (isToday ? TodayBackground() : (isWeekend ? WeekendBackground() : CellBackground())),
            Focusable = true,
            Tag = key,
            ToolTip = Localization.T("calendar.markdownTooltip", key)
        };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var number = Text(date.Day.ToString(CultureInfo.InvariantCulture), 18, FontWeights.SemiBold, Foreground());
        number.VerticalAlignment = VerticalAlignment.Center;
        UIElement numberElement = number;
        if (isToday)
        {
            number.Foreground = minimal && _state.Theme == CalendarTheme.Dark ? Brushes.Black : Brushes.White;
            numberElement = new Border
            {
                Background = minimal ? MinimalTodayBadge() : Accent(),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(-4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = number
            };
        }
        Grid.SetColumn(numberElement, 0);
        top.Children.Add(numberElement);
        var term = _metadata.GetSolarTerm(date);
        if (term is not null)
        {
            var termText = Text(Localization.Term(term), 12, FontWeights.Normal, Accent(), new Thickness(5, 0, 0, 0));
            termText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(termText, 1);
            top.Children.Add(termText);
        }
        var holiday = _metadata.GetHoliday(date);
        if (holiday is not null)
        {
            var holidayLabel = Localization.Holiday(holiday.Name);
            var holidaySuffix = holiday.IsOffDay ? " · " + Localization.T("holiday.off") : " · " + Localization.T("holiday.work");
            var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = holiday.IsOffDay ? OffBrush() : WorkBrush(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = holidayLabel + holidaySuffix };
            var badgeText = Text(holiday.IsOffDay ? Localization.T("holiday.off") : Localization.T("holiday.work"), 11, FontWeights.SemiBold, holiday.IsOffDay ? OffBrush() : WorkBrush());
            badgeText.HorizontalAlignment = HorizontalAlignment.Center;
            badgeText.VerticalAlignment = VerticalAlignment.Center;
            badgeText.TextAlignment = TextAlignment.Center;
            badge.Child = badgeText;
            Grid.SetColumn(badge, 2);
            top.Children.Add(badge);
        }
        Grid.SetRow(top, 0);
        content.Children.Add(top);

        if (_state.DateMarkdown.TryGetValue(key, out var markdown) && !string.IsNullOrWhiteSpace(markdown))
        {
            var preview = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ClipToBounds = true,
                Child = RenderMarkdown(markdown, true),
                ToolTip = Localization.T("markdown.editTooltip")
            };
            preview.MouseLeftButtonDown += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                ShowMarkdownEditor(date, false, Mouse.GetPosition(this));
            };
            Grid.SetRow(preview, 1);
            content.Children.Add(preview);
        }
        cell.Child = content;
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { e.Handled = true; ShowMarkdownEditor(date, false, Mouse.GetPosition(this)); }
        };
        cell.KeyDown += (_, e) => { if (e.Key == Key.Enter) ShowMarkdownEditor(date, false); };
        return cell;
    }

    private void CycleStyle()
    {
        _state.Style = _state.Style switch { CalendarStyle.Windows => CalendarStyle.Minimal, CalendarStyle.Minimal => CalendarStyle.Widget, _ => CalendarStyle.Windows };
        _state.Opacity = _state.Style == CalendarStyle.Widget ? .86 : 1.0;
        SaveAndRender();
    }

    private void Navigate(int delta) { _anchor = new DateTime(_anchor.Year, _anchor.Month, 1).AddMonths(delta); _home = false; Render(); _ = _holidayUpdates.RefreshForCalendarYearAsync(_anchor.Year); }
    private void GoToday() { RefreshToday(); _anchor = _today; _home = true; Render(); _ = _holidayUpdates.RefreshForCalendarYearAsync(_anchor.Year); }
    private void RefreshToday() { var now = DateTime.Today; if (now != _today) { _today = now; if (_home) _anchor = now; Render(); } }

    internal void DisposeHolidayUpdates() => _holidayUpdates.Dispose();

    private void ApplyVisuals()
    {
        UpdateThemeResources();
        var dark = _state.Theme == CalendarTheme.Dark;
        var style = _state.Style;
        var minimal = style == CalendarStyle.Minimal;
        var surface = dark ? Color.FromRgb(32, 37, 44) : (minimal ? Color.FromRgb(255, 255, 255) : Color.FromRgb(248, 250, 252));
        var alpha = (byte)Math.Round(_state.Opacity * 255);
        var widget = style == CalendarStyle.Widget;
        _toolbar.HorizontalAlignment = HorizontalAlignment.Center;
        if (widget)
        {
            // The widget floats over the desktop. Only its own surfaces are painted;
            // the unused area around the toolbar and date grid stays transparent.
            var widgetSurface = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
            var widgetLine = new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + 20), dark ? (byte)57 : (byte)223, dark ? (byte)65 : (byte)229, dark ? (byte)75 : (byte)236));
            _card.Background = Brushes.Transparent;
            _card.BorderBrush = Brushes.Transparent;
            _card.BorderThickness = new Thickness(0);
            _card.CornerRadius = new CornerRadius(0);
            _toolbar.Background = widgetSurface;
            _toolbar.BorderBrush = widgetLine;
            _toolbar.BorderThickness = new Thickness(1);
            _toolbar.CornerRadius = new CornerRadius(15);
            // Match the effective inset of a date cell: the date grid has a 16px
            // inset and each cell adds a 2px outer margin.
            _toolbar.Margin = new Thickness(0, 8, 0, 5);
            _weekdayBar.Background = widgetSurface;
            _weekdayBar.BorderBrush = widgetLine;
            _weekdayBar.BorderThickness = new Thickness(1);
            _weekdayBar.CornerRadius = new CornerRadius(11);
            _weekdayBar.Margin = new Thickness(CalendarLayout.WidgetOuterInset, 0, CalendarLayout.WidgetOuterInset, 5);
        }
        else
        {
            _card.Background = new SolidColorBrush(Color.FromArgb(alpha, surface.R, surface.G, surface.B));
            _card.BorderBrush = minimal ? MinimalLine() : new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + 20), (byte)(dark ? 58 : 223), (byte)(dark ? 65 : 229), (byte)(dark ? 75 : 236)));
            _card.BorderThickness = new Thickness(1);
            _card.CornerRadius = minimal ? new CornerRadius(0) : new CornerRadius(14);
            _toolbar.Background = Brushes.Transparent;
            _toolbar.BorderBrush = minimal ? MinimalLine() : Brushes.Transparent;
            _toolbar.BorderThickness = minimal ? new Thickness(0, 0, 0, 1) : new Thickness(0);
            _toolbar.CornerRadius = new CornerRadius(0);
            _toolbar.Margin = new Thickness(0);
            _weekdayBar.Background = Brushes.Transparent;
            _weekdayBar.BorderBrush = Brushes.Transparent;
            _weekdayBar.BorderThickness = new Thickness(0);
            _weekdayBar.CornerRadius = new CornerRadius(0);
            _weekdayBar.Margin = new Thickness(0);
        }
        ApplyToolbarVisuals();
        ApplyWeekdayVisuals();
        _calendarLogo.Child = BuildThemeLogo();
        UpdateGrip();
        Background = Brushes.Transparent;
        // A DropShadowEffect on the root card rasterizes the whole visual tree
        // before drawing the shadow. In Windows style that makes every text
        // glyph look soft, while the other styles (which have no effect) stay
        // sharp. Keep the card vector-rendered; its border and corner radius
        // provide the elevation without blurring the content.
        _card.Effect = null;
        ApplyWindowLayering();
    }

    private void ApplyWeekdayVisuals()
    {
        var minimal = _state.Style == CalendarStyle.Minimal;
        for (var i = 0; i < _weekdayPanel.Children.Count; i++)
        {
            if (_weekdayPanel.Children[i] is not Border header) continue;
            header.BorderBrush = minimal ? MinimalLine() : Brushes.Transparent;
            header.BorderThickness = minimal ? new Thickness(0, 0, 0, 1) : new Thickness(0);
            if (header.Child is TextBlock text)
            {
                text.Text = WeekdayName(i);
                text.Foreground = WeekdayForeground(i);
            }
        }
    }

    private void ApplyToolbarVisuals()
    {
        var foreground = Foreground();
        _currentDateText.Foreground = foreground;
        _currentWeekText.Foreground = Muted();
        _viewMonthText.Foreground = foreground;
        _toolbarSeparator.Background = Line();

        foreach (var button in new[] { _previousButton, _nextButton, _todayButton, _styleButton, _themeButton, _lockButton, _settingsButton })
        {
            button.Foreground = foreground;
            button.BorderBrush = Line();
        }
    }

    private Canvas BuildThemeLogo()
    {
        var accent = AccentColor();
        var deep = _state.Theme == CalendarTheme.Dark ? Blend(accent, Color.FromRgb(0, 0, 0), .35) : accent;
        var top = Blend(deep, Color.FromRgb(0, 0, 0), .14);
        var paper = Color.FromRgb(255, 255, 255);
        var blocks = Blend(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(43, 47, 52) : Color.FromRgb(214, 214, 214), accent, .18);
        var logo = new Canvas { Width = 1024, Height = 1024, ClipToBounds = false, SnapsToDevicePixels = true };

        logo.Children.Add(LogoRect(112, 170, 800, 760, 120, new SolidColorBrush(deep)));
        logo.Children.Add(LogoPath("M232,170 H792 A120,120 0 0 1 912,290 V395 H112 V290 A120,120 0 0 1 232,170 Z", new SolidColorBrush(top)));
        logo.Children.Add(LogoPath("M248,395 H868 V678 Q868,724 832,754 L706,858 H248 Q188,858 188,798 V455 Q188,395 248,395 Z", new SolidColorBrush(paper)));

        logo.Children.Add(LogoRect(236, 92, 108, 220, 54, new SolidColorBrush(deep)));
        logo.Children.Add(LogoRect(262, 118, 56, 168, 28, new SolidColorBrush(paper)));
        logo.Children.Add(LogoRect(680, 92, 108, 220, 54, new SolidColorBrush(deep)));
        logo.Children.Add(LogoRect(706, 118, 56, 168, 28, new SolidColorBrush(paper)));

        var screw = new Canvas { Width = 1024, Height = 1024, RenderTransform = new ScaleTransform(.85, .85, 512, 320) };
        screw.Children.Add(LogoCircle(512, 320, 116, new SolidColorBrush(deep)));
        screw.Children.Add(LogoCircle(512, 320, 92, new SolidColorBrush(paper)));
        screw.Children.Add(LogoRect(491, 260, 42, 120, 20, new SolidColorBrush(deep)));
        screw.Children.Add(LogoRect(452, 299, 120, 42, 20, new SolidColorBrush(deep)));
        logo.Children.Add(screw);

        foreach (var block in new[]
        {
            (242d, 448d, 132d, 108d), (406d, 448d, 132d, 108d), (570d, 448d, 132d, 108d), (734d, 448d, 108d, 108d),
            (242d, 582d, 132d, 108d), (406d, 582d, 132d, 108d), (734d, 582d, 108d, 108d),
            (242d, 716d, 132d, 92d), (406d, 716d, 132d, 92d), (570d, 716d, 132d, 92d)
        }) logo.Children.Add(LogoRect(block.Item1, block.Item2, block.Item3, block.Item4, 18, new SolidColorBrush(blocks)));

        logo.Children.Add(LogoRect(570, 582, 132, 108, 18, new SolidColorBrush(accent)));
        logo.Children.Add(LogoStrokePath("M604,636 L632,664 L678,615", new SolidColorBrush(paper), 22));
        return logo;
    }

    private Path BuildLockIcon(bool locked)
    {
        return new Path
        {
            Data = Geometry.Parse(locked
                ? "M5,8 V6 A4,4 0 0 1 13,6 V8 M3,8 H15 V16 H3 Z"
                : "M6,8 V6 A4,4 0 0 1 13,4 M3,8 H15 V16 H3 Z"),
            Stroke = Foreground(),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform,
            Width = 17,
            Height = 17
        };
    }

    private static Rectangle LogoRect(double x, double y, double width, double height, double radius, Brush fill)
    {
        var rect = new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius, Fill = fill };
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y); return rect;
    }

    private static Ellipse LogoCircle(double centerX, double centerY, double radius, Brush fill)
    {
        var circle = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill };
        Canvas.SetLeft(circle, centerX - radius); Canvas.SetTop(circle, centerY - radius); return circle;
    }

    private static System.Windows.Shapes.Path LogoPath(string data, Brush fill) => new() { Data = Geometry.Parse(data), Fill = fill };
    private static System.Windows.Shapes.Path LogoStrokePath(string data, Brush stroke, double width) => new() { Data = Geometry.Parse(data), Fill = Brushes.Transparent, Stroke = stroke, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };

    private void UpdateCalendarContentWidths()
    {
        // Date columns keep their fixed 854px design width. The toolbar and
        // weekday surface extend into the surrounding whitespace by style.
        var outerInset = _state.Style == CalendarStyle.Widget ? CalendarLayout.WidgetOuterInset : CalendarLayout.ToolbarOuterInset;
        var outerWidth = CalendarLayout.CardWidth - outerInset * 2;
        _weekdayBar.Width = outerWidth;
        _weekdayPanel.Width = CalendarLayout.GridWidth;
        _toolbar.Width = outerWidth;
        foreach (var holder in _datesGrid.Children.OfType<Grid>())
            holder.Width = CalendarLayout.GridWidth;
    }
    private string StyleName() => _state.Style switch { CalendarStyle.Windows => Localization.T("style.windows"), CalendarStyle.Minimal => Localization.T("style.minimal"), _ => Localization.T("style.widget") };
    private static string ThemeColorName(CalendarThemeColor color) => color switch
    {
        CalendarThemeColor.Teal => Localization.T("color.teal"),
        CalendarThemeColor.Purple => Localization.T("color.purple"),
        CalendarThemeColor.Orange => Localization.T("color.orange"),
        CalendarThemeColor.Gray => Localization.T("color.gray"),
        CalendarThemeColor.Red => Localization.T("color.red"),
        _ => Localization.T("color.blue")
    };
    private Brush ThemeColorBrush(CalendarThemeColor color) => new SolidColorBrush(AccentColor(color));
    private Button Button(string content, string tip, double width) => new() { Content = content, ToolTip = tip, Width = width, Height = 34, Margin = new Thickness(2), Padding = new Thickness(8, 0, 8, 0), FontSize = 13, Background = Brushes.Transparent, BorderBrush = Line(), Foreground = Foreground(), Template = RoundedButtonTemplate() };

    private ControlTemplate RoundedButtonTemplate(bool suppressHover = false) =>
        Resource<ControlTemplate>(suppressHover ? "ToolkitRoundedButtonNoHoverTemplate" : "ToolkitRoundedButtonTemplate");

    private static T Resource<T>(string key) where T : class => (T)Application.Current.FindResource(key);

    private void UpdateThemeResources()
    {
        var accent = AccentColor();
        var dark = _state.Theme == CalendarTheme.Dark;
        Application.Current.Resources["ToolkitAccentBrush"] = new SolidColorBrush(accent);
        Application.Current.Resources["ToolkitInputOutlineBrush"] = new SolidColorBrush(dark ? Color.FromRgb(79, 90, 103) : Color.FromRgb(216, 224, 233));
        Application.Current.Resources["ToolkitInputUnderlineBrush"] = new SolidColorBrush(dark ? Color.FromRgb(126, 138, 153) : Color.FromRgb(91, 103, 117));
        Application.Current.Resources["ToolkitSliderTrackBrush"] = new SolidColorBrush(dark ? Color.FromRgb(72, 83, 96) : Color.FromRgb(174, 188, 202));
        Application.Current.Resources["ToolkitScrollThumbBrush"] = new SolidColorBrush(dark ? Color.FromRgb(118, 134, 152) : Color.FromRgb(174, 188, 202));
        Application.Current.Resources["ToolkitScrollThumbHoverBrush"] = new SolidColorBrush(dark ? Color.FromRgb(145, 161, 179) : Color.FromRgb(143, 160, 178));
    }
    private TextBlock Text(string content, double size, FontWeight weight, Brush brush, Thickness? margin = null) => new() { Text = content, FontSize = size, FontWeight = weight, Foreground = brush, FontFamily = _uiFont, Margin = margin ?? new Thickness(0) };
    private TextBlock Text(string content, double size, Brush brush, Thickness? margin = null) => Text(content, size, FontWeights.Normal, brush, margin);
    private Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private new Brush Foreground() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(231, 237, 245) : Color.FromRgb(38, 52, 67));
    private Brush Muted() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(154, 169, 186) : Color.FromRgb(120, 134, 149));
    private Brush WeekdayForeground(int column) => column >= 5 ? Accent() : Muted();
    private Brush Accent() => new SolidColorBrush(AccentColor());
    private Brush MarkdownSelectionBrush()
    {
        if (_state.Theme == CalendarTheme.Dark)
            return Accent();

        // A light, opaque tint keeps the current theme color visible without
        // overpowering selected Markdown text in light mode.
        return new SolidColorBrush(Blend(Color.FromRgb(255, 255, 255), AccentColor(), .14));
    }
    private Color AccentColor() => AccentColor(_state.ThemeColor);
    private Color AccentColor(CalendarThemeColor color)
    {
        var dark = _state.Theme == CalendarTheme.Dark;
        return color switch
        {
            CalendarThemeColor.Teal => dark ? Color.FromRgb(100, 214, 196) : Color.FromRgb(0, 128, 116),
            CalendarThemeColor.Purple => dark ? Color.FromRgb(201, 169, 255) : Color.FromRgb(106, 69, 184),
            CalendarThemeColor.Orange => dark ? Color.FromRgb(255, 187, 115) : Color.FromRgb(184, 92, 0),
            CalendarThemeColor.Gray => dark ? Color.FromRgb(186, 196, 210) : Color.FromRgb(95, 107, 122),
            CalendarThemeColor.Red => dark ? Color.FromRgb(255, 137, 145) : Color.FromRgb(194, 65, 75),
            _ => dark ? Color.FromRgb(117, 186, 255) : Color.FromRgb(0, 103, 192)
        };
    }
    private Brush Line()
    {
        var color = _state.Theme == CalendarTheme.Dark ? Color.FromRgb(57, 65, 75) : Color.FromRgb(223, 229, 236);
        return new SolidColorBrush(_state.ThemeColor == CalendarThemeColor.Blue ? color : Blend(color, AccentColor(), .12));
    }
    private Brush MinimalLine() => Line();
    private Brush MinimalWeekendBackground() => WeekendBackground();
    private Brush MinimalTodayBackground() => TodayBackground();
    private Brush MinimalTodayLine() => Accent();
    private Brush MinimalTodayBadge() => Accent();
    private Brush CardBackground() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(32, 37, 44) : Color.FromRgb(248, 250, 252));
    private Brush DialogBackground() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(32, 37, 44) : Color.FromRgb(248, 250, 252));
    private Brush InputBackground() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(45, 51, 59) : Color.FromRgb(255, 255, 255));
    private Brush CellBackground() => new SolidColorBrush(SurfaceColor(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(40, 46, 54) : Color.FromRgb(255, 255, 255)));
    private Brush WeekendBackground() => new SolidColorBrush(SurfaceColor(ThemeSurface(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(37, 44, 54) : Color.FromRgb(241, 245, 251))));
    private Brush TodayBackground() => new SolidColorBrush(SurfaceColor(ThemeSurface(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(38, 62, 86) : Color.FromRgb(237, 246, 255))));
    private Brush SurfaceSecondary() => new SolidColorBrush(SurfaceColor(ThemeSurface(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(51, 62, 75) : Color.FromRgb(232, 240, 249))));
    private Color ThemeSurface(Color color) => _state.ThemeColor == CalendarThemeColor.Blue ? color : Blend(color, AccentColor(), _state.Theme == CalendarTheme.Dark ? .16 : .10);
    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb((byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
    private Color SurfaceColor(Color color) => Color.FromArgb((byte)Math.Round(_state.Opacity * 255), color.R, color.G, color.B);
    private Brush OffBrush() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(106, 206, 156) : Color.FromRgb(24, 129, 81));
    private Brush WorkBrush() => new SolidColorBrush(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(255, 144, 144) : Color.FromRgb(212, 68, 68));
    private Brush EventBackground(string hex)
    {
        var color = ((SolidColorBrush)Brush(hex)).Color;
        var baseAlpha = _state.Theme == CalendarTheme.Dark ? 80 : 25;
        var eventAlpha = (byte)Math.Round(baseAlpha * _state.Opacity);
        return new SolidColorBrush(Color.FromArgb(eventAlpha, color.R, color.G, color.B));
    }
}
