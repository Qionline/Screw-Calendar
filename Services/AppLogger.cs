using System;
using System.IO;

namespace ScrewCalendar;

public static class AppLogger
{
    private static readonly object Gate = new();
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewCalendar", "logs");
    public static string LogPath => Path.Combine(LogDirectory, "app.log");

    public static void Error(string operation, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var detail = exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}";
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} | ERROR | {operation}{detail}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never make the calendar fail to start or exit.
        }
    }
}
