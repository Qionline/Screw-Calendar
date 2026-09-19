using System;
using System.Drawing;

namespace DesktopWidgets.Toolkit.Windows;

public static class ApplicationIconLoader
{
    public static Icon CurrentExecutableOrDefault(Icon? fallback = null, Action<string, Exception?>? logError = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (icon is not null) return icon;
            }
        }
        catch (Exception exception) { logError?.Invoke("Failed to load the current executable icon.", exception); }
        return fallback ?? SystemIcons.Application;
    }
}
