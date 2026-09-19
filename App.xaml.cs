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
        _instanceGuard = new SingleInstanceGuard("Local\\ScrewCalendar.SingleInstance");
        if (!_instanceGuard.IsPrimaryInstance)
        {
            Shutdown();
            return;
        }

        _window = new MainWindow();
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.DisposeTray();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
