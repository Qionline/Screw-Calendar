using System;
using Microsoft.Win32;

namespace DesktopWidgets.Toolkit.Windows;

public static class StartupRegistration
{
    private const string RegistryPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    public static bool SetEnabled(string valueName, bool enabled, string? executablePath = null, string? arguments = null, Action<string, Exception?>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(valueName)) throw new ArgumentException("A registry value name is required.", nameof(valueName));
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
            if (key is null) return false;
            if (!enabled) { key.DeleteValue(valueName, false); return true; }
            var path = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable.");
            var command = $"\"{path}\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim());
            key.SetValue(valueName, command);
            return true;
        }
        catch (Exception exception)
        {
            logError?.Invoke("Failed to update Windows startup registration.", exception);
            return false;
        }
    }
}
