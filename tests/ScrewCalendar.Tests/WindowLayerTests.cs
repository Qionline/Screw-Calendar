using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar.Tests;

internal static class WindowLayerTests
{
    public static void StyleTransitions() => RunSta(() =>
    {
        var window = CreateWindow();
        using var controller = new WindowLayerController(window, allowFullscreenCover: false);
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            CheckStyles(handle, noActivate: true);
            controller.SetAlwaysOnTop(true);
            CheckStyles(handle, noActivate: false);
            controller.SetAlwaysOnTop(false);
            CheckStyles(handle, noActivate: true);
            controller.SetInteractive(true);
            CheckStyles(handle, noActivate: false);
            controller.SetInteractive(false);
            CheckStyles(handle, noActivate: true);

            SendMessage(handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
            Require(window.WindowState != WindowState.Minimized, "Minimize command must not minimize the widget");
        }
        finally { window.Close(); }
    });

    public static void DesktopPlacement() => RunSta(() =>
    {
        var desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero)
        {
            Console.WriteLine("SKIP  Desktop layer recovery requires an interactive Explorer desktop");
            return;
        }
        var window = CreateWindow();
        using var controller = new WindowLayerController(window, allowFullscreenCover: false);
        try
        {
            window.Show();
            controller.Refresh();
            DrainEvents();
            var handle = new WindowInteropHelper(window).Handle;
            Require(IsAboveDesktop(handle, desktop), "Widget must be above the desktop");

            // Move only the test widget behind the shell. Its native position
            // notification must repair the order without requiring activation.
            SetWindowPos(handle, desktop, 0, 0, 0, 0, 0x0013);
            DrainEvents();
            Require(IsAboveDesktop(handle, desktop), "Widget must recover after being covered by the desktop");
            Require(!window.Topmost, "Desktop recovery must not enable always-on-top");
            Require((GetWindowLongPtr(handle, -20).ToInt64() & 0x8) == 0, "Native window must remain in the non-topmost band");
            CheckStyles(handle, noActivate: true);

            window.Hide();
            controller.Refresh();
            DrainEvents();
            Require(!window.IsVisible && !IsWindowVisible(handle), "Tray-style hiding must remain effective");
            window.Show();
            controller.Refresh();
            DrainEvents();
            Require(IsAboveDesktop(handle, desktop), "Showing the widget must restore its desktop position");
        }
        finally { window.Close(); }
    });

    private static Window CreateWindow() => new()
    {
        Title = "ScrewCalendar window layer regression",
        Width = 100,
        Height = 100,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Opacity = 0,
        ShowActivated = false,
        ShowInTaskbar = false,
        ResizeMode = ResizeMode.NoResize
    };

    private static void CheckStyles(IntPtr handle, bool noActivate)
    {
        var styles = IntPtr.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
        Require((styles & 0x80) != 0, "Widget must be a tool window excluded from Alt+Tab");
        Require((styles & 0x40000) == 0, "Widget must not have the application-window flag");
        Require(((styles & 0x08000000) != 0) == noActivate, "Keyboard activation must match the current interaction mode");
    }

    private static IntPtr FindDesktopHost()
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsAboveDesktop(IntPtr handle, IntPtr desktop)
    {
        // Explorer and WPF can insert helper windows between the two handles.
        // Their exact adjacency is not part of the visible layering contract.
        for (var window = GetWindow(desktop, 3); window != IntPtr.Zero; window = GetWindow(window, 3))
            if (window == handle) return true;
        return false;
    }

    private static void DrainEvents()
    {
        var frame = new DispatcherFrame();
        // Shell activity can keep SystemIdle work pending indefinitely. The
        // normal-priority timeout still allows queued placement work to run,
        // while guaranteeing that assertions and cleanup get a turn.
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Window layer regression timed out");
        if (failure is not null) throw new InvalidOperationException($"Window layer regression failed: {failure.Message}", failure);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
