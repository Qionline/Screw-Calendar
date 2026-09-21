using System;
using System.IO;
using System.Reflection;
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
            if (!enabled)
            {
                using var existingKey = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                existingKey?.DeleteValue(valueName, throwOnMissingValue: false);
                return true;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                logError?.Invoke("Failed to update Windows startup registration: the Run registry key could not be created.", null);
                return false;
            }

            key.SetValue(valueName, ResolveCommand(executablePath, arguments), RegistryValueKind.String);
            return true;
        }
        catch (Exception exception)
        {
            logError?.Invoke("Failed to update Windows startup registration.", exception);
            return false;
        }
    }

    private static string ResolveCommand(string? executablePath, string? arguments)
    {
        if (!string.IsNullOrWhiteSpace(executablePath)) return FormatCommand(executablePath, arguments);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("The current executable path is unavailable.");

        if (!IsDotNetHost(processPath)) return FormatCommand(processPath, arguments);

        var entryAssembly = Assembly.GetEntryAssembly();
        var assemblyName = entryAssembly?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(assemblyName))
        {
            var appHostPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe");
            if (File.Exists(appHostPath)) return FormatCommand(appHostPath, arguments);
        }

        var assemblyPath = entryAssembly?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            throw new InvalidOperationException("The current application path is unavailable.");

        return $"{Quote(processPath)} {Quote(assemblyPath)}{FormatArguments(arguments)}";
    }

    private static bool IsDotNetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static string FormatCommand(string executablePath, string? arguments) =>
        $"{Quote(Path.GetFullPath(executablePath))}{FormatArguments(arguments)}";

    private static string FormatArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim();

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"")}\"";
}
