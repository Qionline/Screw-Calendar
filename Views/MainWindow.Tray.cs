using System;
using System.Windows;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

// System-tray integration and close-to-tray lifecycle.
public sealed partial class MainWindow
{
    private void CreateTray()
    {
        void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            if (_state.Topmost) Activate();
            else _windowLayerController.Refresh();
        }
        _tray.Show(ApplicationIconLoader.CurrentExecutableOrDefault(logError: AppLogger.Error), Localization.T("app.title"),
            () => { if (IsVisible) Hide(); else ShowWindow(); },
            [
                new TrayMenuItem(Localization.T("tray.show"), ShowWindow),
                TrayMenuItem.Separator(),
                new TrayMenuItem(Localization.T("tray.topmost"), () => SetTopmost(!_state.Topmost), true, _state.Topmost),
                new TrayMenuItem(Localization.T("tray.locked"), () => SetLocked(!_state.Locked), true, _state.Locked),
                new TrayMenuItem(Localization.T("tray.startup"), () => SetStartupEnabled(!_state.StartWithWindows), true, _state.StartWithWindows),
                TrayMenuItem.Separator(),
                new TrayMenuItem(Localization.T("tray.exit"), () => { _allowExit = true; Application.Current.Shutdown(); })
            ]);
    }

    public void DisposeTray()
    {
        _windowLayerController.Dispose();
        _tray.Dispose();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            SaveGeometry();
            return;
        }
        SaveGeometry();
        DisposeHolidayUpdates();
        DisposeTray();
    }
}
