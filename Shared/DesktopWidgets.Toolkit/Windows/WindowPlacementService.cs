using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace DesktopWidgets.Toolkit.Windows;

public sealed class WindowPlacementState
{
    public double Left { get; set; } = 100;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 800;
    public double Height { get; set; } = 600;
    public string Monitor { get; set; } = string.Empty;
}

public static class WindowPlacementService
{
    public static void Restore(Window window, WindowPlacementState state, double edgeMargin = 30, double fallbackInset = 24)
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == state.Monitor) ?? Forms.Screen.PrimaryScreen!;
        var working = screen.WorkingArea;
        var left = state.Monitor == screen.DeviceName ? state.Left : working.Left + fallbackInset;
        var top = state.Monitor == screen.DeviceName ? state.Top : working.Top + fallbackInset;
        window.Width = Math.Clamp(state.Width, window.MinWidth, Math.Max(window.MinWidth, working.Width - edgeMargin));
        window.Height = Math.Clamp(state.Height, window.MinHeight, Math.Max(window.MinHeight, working.Height - edgeMargin));
        window.Left = Math.Clamp(left, working.Left, working.Right - window.Width);
        window.Top = Math.Clamp(top, working.Top, working.Bottom - window.Height);
    }

    public static void Capture(Window window, WindowPlacementState state)
    {
        if (!window.IsLoaded || window.WindowState == WindowState.Minimized) return;
        state.Left = window.Left;
        state.Top = window.Top;
        state.Width = window.Width;
        state.Height = window.Height;
        state.Monitor = Forms.Screen.AllScreens.FirstOrDefault(screen =>
                new System.Drawing.Rectangle((int)window.Left, (int)window.Top, (int)window.Width, (int)window.Height).IntersectsWith(screen.WorkingArea))?.DeviceName
            ?? Forms.Screen.PrimaryScreen?.DeviceName
            ?? string.Empty;
    }

    public static System.Drawing.Rectangle WorkingArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero ? Forms.Screen.FromHandle(handle).WorkingArea : Forms.Screen.PrimaryScreen!.WorkingArea;
    }
}
