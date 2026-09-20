using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopWidgets.Toolkit.Windows;

/// <summary>
/// Controls the two useful layers for desktop widgets: always-on-top, or
/// directly above the Windows desktop and below ordinary application windows.
/// It also prevents Show Desktop from minimizing the controlled window.
/// </summary>
public sealed class WindowLayerController : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const uint GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly Window _window;
    private readonly bool _allowFullscreenCover;
    private readonly WinEventDelegate _foregroundChangedCallback;
    private HwndSource? _source;
    private IntPtr _foregroundHook;
    private bool _alwaysOnTop;
    private bool _interactive;
    private bool _fullscreenForeground;
    private bool _placementPending;
    private bool _disposed;

    public WindowLayerController(Window window, bool allowFullscreenCover = true)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _allowFullscreenCover = allowFullscreenCover;
        _foregroundChangedCallback = OnForegroundWindowChanged;
        _window.SourceInitialized += OnSourceInitialized;
        _window.Activated += OnWindowLayerChanged;
        _window.Deactivated += OnWindowLayerChanged;
    }

    public bool AlwaysOnTop => _alwaysOnTop;

    /// <summary>
    /// Temporarily lets a desktop-layer widget activate for text input or other
    /// direct interaction. The widget remains below ordinary windows when the
    /// interaction ends.
    /// </summary>
    public void SetInteractive(bool enabled)
    {
        if (_disposed) return;
        _interactive = enabled;
        ApplyMode();
        if (enabled && !_alwaysOnTop && _window.IsVisible)
            _window.Activate();
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        if (_disposed) return;
        _alwaysOnTop = enabled;
        ApplyMode();
    }

    /// <summary>Re-applies the selected layer after showing the window.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        if (_alwaysOnTop) UpdateFullscreenState();
        else QueueDesktopPlacement();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowProc);
        if (_allowFullscreenCover)
            _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                _foregroundChangedCallback, 0, 0, WineventOutOfContext);
        ApplyMode();
    }

    private void OnWindowLayerChanged(object? sender, EventArgs e) => Refresh();

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmSysCommand && ((long)wParam & 0xFFF0L) == ScMinimize)
        {
            handled = true;
            Refresh();
        }
        return IntPtr.Zero;
    }

    private void ApplyMode()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        ApplyNoActivateStyle(handle, !_alwaysOnTop && !_interactive);
        _window.ShowActivated = _alwaysOnTop || _interactive;
        if (_alwaysOnTop)
        {
            UpdateFullscreenState();
            return;
        }

        _fullscreenForeground = false;
        _window.Topmost = false;
        QueueDesktopPlacement();
    }

    private void ApplyNoActivateStyle(IntPtr handle, bool enabled)
    {
        var current = GetWindowLongPointer(handle, GwlExStyle).ToInt64();
        var desired = enabled ? current | WsExNoActivate : current & ~WsExNoActivate;
        if (desired == current) return;
        SetWindowLongPointer(handle, GwlExStyle, new IntPtr(desired));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void OnForegroundWindowChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || !_alwaysOnTop) return;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, UpdateFullscreenState);
    }

    private void UpdateFullscreenState()
    {
        if (_disposed || !_alwaysOnTop) return;
        var ownHandle = new WindowInteropHelper(_window).Handle;
        var foreground = GetForegroundWindow();
        var fullscreen = _allowFullscreenCover && foreground != IntPtr.Zero && foreground != ownHandle &&
                         !IsDesktopShellWindow(foreground) && IsFullscreen(foreground) && WindowsOverlap(foreground, ownHandle);
        if (_fullscreenForeground == fullscreen && _window.Topmost == !fullscreen) return;
        _fullscreenForeground = fullscreen;
        _window.Topmost = !fullscreen;
        if (fullscreen)
        {
            // Changing Topmost to false can place the widget at the top of the
            // normal window band. Explicitly put it behind the fullscreen
            // foreground window so a borderless game also remains unobstructed.
            SetWindowPos(ownHandle, foreground, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    private static bool IsFullscreen(IntPtr window)
    {
        if (!IsWindowVisible(window) || !GetWindowRect(window, out var bounds)) return false;
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        return bounds.Left <= info.Monitor.Left && bounds.Top <= info.Monitor.Top &&
               bounds.Right >= info.Monitor.Right && bounds.Bottom >= info.Monitor.Bottom;
    }

    private static bool WindowsOverlap(IntPtr first, IntPtr second)
    {
        if (!GetWindowRect(first, out var a) || !GetWindowRect(second, out var b)) return false;
        return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    }

    private static bool IsDesktopShellWindow(IntPtr window)
    {
        var className = new char[64];
        var length = GetClassName(window, className, className.Length);
        if (length <= 0) return false;
        var value = new string(className, 0, length);
        return string.Equals(value, "Progman", StringComparison.Ordinal) || string.Equals(value, "WorkerW", StringComparison.Ordinal);
    }

    private void QueueDesktopPlacement()
    {
        if (_placementPending || _alwaysOnTop || _disposed) return;
        _placementPending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _placementPending = false;
            PlaceImmediatelyAboveDesktop();
        });
    }

    private void PlaceImmediatelyAboveDesktop()
    {
        if (_alwaysOnTop || _disposed || !_window.IsVisible) return;
        foreach (Window ownedWindow in _window.OwnedWindows)
            if (ownedWindow.IsVisible) return;
        var handle = new WindowInteropHelper(_window).Handle;
        var desktopHost = FindDesktopHost();
        if (handle == IntPtr.Zero || desktopHost == IntPtr.Zero) return;
        var windowAboveDesktop = GetWindow(desktopHost, GwHwndPrev);
        if (windowAboveDesktop == handle || windowAboveDesktop == IntPtr.Zero) return;
        SetWindowPos(handle, windowAboveDesktop, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
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
        return result != IntPtr.Zero ? result : FindWindow("Progman", null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Activated -= OnWindowLayerChanged;
        _window.Deactivated -= OnWindowLayerChanged;
        _source?.RemoveHook(WindowProc);
        _source = null;
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
    }

    private static IntPtr GetWindowLongPointer(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPointer(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(IntPtr window, int index, int newValue);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, [Out] char[] className, int maximumCount);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
}
