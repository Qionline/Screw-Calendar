using System;
using System.Windows;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private SingleInstanceGuard? _instanceGuard;

    public App() => InitializeComponent();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var session = Guid.Empty;
        var hasSession = e.Args.Length == 2 && Guid.TryParseExact(e.Args[1], "N", out session);
        if (e.Args.Length > 0 && e.Args[0] == AppUpdateInstaller.ApplyArgument)
        {
            Shutdown(hasSession ? AppUpdateInstaller.Run(session) : 1);
            return;
        }

        var confirming = hasSession && e.Args[0] == AppUpdateInstaller.ConfirmArgument &&
            AppUpdateInstaller.CanConfirmStartup(session);
        using var updateGate = confirming ? null : new SingleInstanceGuard(AppUpdateInstaller.MutexName);
        if (updateGate is { IsPrimaryInstance: false })
        {
            Shutdown();
            return;
        }

        _instanceGuard = new SingleInstanceGuard("Local\\ScrewCalendar.SingleInstance");
        if (!_instanceGuard.IsPrimaryInstance)
        {
            Shutdown();
            return;
        }

        _window = new MainWindow();
        var rolledBack = hasSession && e.Args[0] == AppUpdateInstaller.RolledBackArgument;
        if (confirming || rolledBack)
        {
            _window.ContentRendered += ConfirmRenderedWindow;
            void ConfirmRenderedWindow(object? sender, EventArgs args)
            {
                _window.ContentRendered -= ConfirmRenderedWindow;
                if (confirming)
                {
                    AppUpdateInstaller.ConfirmStartup(session);
                    return;
                }
                AppUpdateInstaller.ScheduleCleanup(session);
                System.Windows.MessageBox.Show(_window, Localization.T("update.rolledBack"), Localization.T("app.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.DisposeHolidayUpdates();
        _window?.DisposeTray();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) _window?.FlushPendingGeometrySave();
    }
}
