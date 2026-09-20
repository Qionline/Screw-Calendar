using System;
using System.Windows;
using System.Windows.Input;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

// Window geometry, resizing, locking, topmost and startup behavior.
public sealed partial class MainWindow
{
    private void RestoreGeometry()
    {
        WindowPlacementService.Restore(this, _state.Geometry);
    }

    private void SaveGeometry()
    {
        WindowPlacementService.Capture(this, _state.Geometry);
        SaveState();
    }

    private void ResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Locked) return;
        _resizeStart = e.GetPosition(this);
        _resizeWidth = Width;
        _resizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_resizeGrip.IsMouseCaptured) return;
        var point = e.GetPosition(this);
        var todoHeight = CurrentTodoHeight();
        var workArea = WindowPlacementService.WorkingArea(this);
        var size = CalendarComponentSizing.FitCalendarWidth(_resizeWidth + point.X - _resizeStart.X, _designHeight, todoHeight, MinWidth, workArea.Height - 36);
        ApplyComponentSize(size);
    }

    private void ResizeGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeGrip.IsMouseCaptured) _resizeGrip.ReleaseMouseCapture();
    }

    private void UpdateGrip()
    {
        _resizeGrip.Visibility = _state.Locked ? Visibility.Collapsed : Visibility.Visible;
        if (_todoResizeGrip is not null)
            _todoResizeGrip.Visibility = _state.ShowTodoPanel && !_state.Locked ? Visibility.Visible : Visibility.Collapsed;
        UpdateLockButtonVisual();
    }
    private void SetLocked(bool enabled)
    {
        _state.Locked = enabled;
        UpdateGrip();
        SaveState();
        CreateTray();
    }

    private void UpdateLockButtonVisual()
    {
        if (_lockButton is null) return;
        _lockButton.Content = BuildLockIcon(_state.Locked);
        _lockButton.ToolTip = Localization.T(_state.Locked ? "nav.unlockTooltip" : "nav.lockTooltip");
    }
    private void SetTopmost(bool enabled)
    {
        _state.Topmost = enabled;
        ApplyWindowLayering();
        SaveState();
        CreateTray();
    }

    private void ApplyWindowLayering()
    {
        _windowLayerController.SetAlwaysOnTop(_state.Topmost);
    }
    private void SetStartupEnabled(bool enabled)
    {
        _state.StartWithWindows = enabled;
        StartupRegistration.SetEnabled("ScrewCalendar", enabled, logError: AppLogger.Error);
        SaveState();
        CreateTray();
    }

    private void ResizeWindowToDesign()
    {
        if (_designHeight <= 0 || Width <= 0) return;
        var todoHeight = CurrentTodoHeight();
        var screen = WindowPlacementService.WorkingArea(this);
        var size = CalendarComponentSizing.FitCalendarWidth(Width, _designHeight, todoHeight, MinWidth, screen.Height - 36);
        ApplyComponentSize(size);
    }

    private double CurrentTodoHeight() => _state.ShowTodoPanel ? _state.TodoPanelHeight : 0;

    private double CurrentCalendarScale() => Math.Clamp(
        (_calendarDisplayWidth > 0 ? _calendarDisplayWidth : Width) / CalendarLayout.CardWidth,
        CalendarLayout.CalendarScaleMinimum,
        CalendarLayout.CalendarScaleMaximum);

    private double ApplyCalendarScale(double scale)
    {
        if (_designHeight <= 0) return CurrentCalendarScale();
        var screen = WindowPlacementService.WorkingArea(this);
        var size = CalendarComponentSizing.FitCalendarWidth(
            CalendarLayout.CardWidth * Math.Clamp(scale, CalendarLayout.CalendarScaleMinimum, CalendarLayout.CalendarScaleMaximum),
            _designHeight,
            CurrentTodoHeight(),
            MinWidth,
            screen.Height - 36);
        ApplyComponentSize(size);
        SaveGeometry();
        return CurrentCalendarScale();
    }

    private double MaximumTodoPanelHeight()
    {
        var workArea = WindowPlacementService.WorkingArea(this);
        var available = Math.Max(CalendarLayout.TodoPanelMinHeight, workArea.Bottom - Top - _calendarDisplayHeight - 12);
        return Math.Min(CalendarLayout.TodoPanelMaxHeight, available);
    }

    private void ApplyComponentSize(CalendarDisplaySize size)
    {
        _calendarDisplayWidth = size.Width;
        Width = size.Width;
        UpdateTodoPanelWidth();
        ApplyComponentHeights(size.CalendarHeight, size.TodoHeight);
    }

    private void ApplyComponentHeights(double calendarHeight, double todoHeight)
    {
        _calendarDisplayHeight = calendarHeight;
        _calendarRow.Height = new GridLength(calendarHeight);
        _todoRow.Height = new GridLength(todoHeight);
        Height = Math.Max(MinHeight, calendarHeight + todoHeight);
    }

    private void TodoResizeGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Locked || !_state.ShowTodoPanel) return;
        _todoResizeStart = e.GetPosition(this);
        _todoResizeHeight = _state.TodoPanelHeight;
        _todoResizeGrip.CaptureMouse();
        e.Handled = true;
    }

    private void TodoResizeGripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_todoResizeGrip.IsMouseCaptured) return;
        var point = e.GetPosition(this);
        var workArea = WindowPlacementService.WorkingArea(this);
        var maximum = MaximumTodoPanelHeight();
        _state.TodoPanelHeight = Math.Clamp(_todoResizeHeight + point.Y - _todoResizeStart.Y, CalendarLayout.TodoPanelMinHeight, maximum);
        ApplyComponentHeights(_calendarDisplayHeight, _state.TodoPanelHeight);
    }

    private void TodoResizeGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_todoResizeGrip.IsMouseCaptured) return;
        _todoResizeGrip.ReleaseMouseCapture();
        SaveState();
    }
}
