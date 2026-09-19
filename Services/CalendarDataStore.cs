using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrewCalendar;

public enum CalendarLoadStatus { Default, Primary, Backup }

public sealed class CalendarDataStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Action<string, Exception?> _logError;
    public CalendarDataStore(string dataDirectory, Action<string, Exception?>? logError = null)
    {
        DataDirectory = dataDirectory;
        _logError = logError ?? AppLogger.Error;
    }

    public string DataDirectory { get; }
    public string StatePath => Path.Combine(DataDirectory, "calendar.json");
    public string BackupPath => Path.Combine(DataDirectory, "calendar.json.bak");
    public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
    public CalendarLoadStatus LastLoadStatus { get; private set; }

    public static CalendarDataStore CreateDefault()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewCalendar", "data");
        return new CalendarDataStore(appData);
    }

    public CalendarState Load()
    {
        if (TryRead(StatePath, out var primary))
        {
            LastLoadStatus = CalendarLoadStatus.Primary;
            return primary;
        }

        if (TryRead(BackupPath, out var backup))
        {
            LastLoadStatus = CalendarLoadStatus.Backup;
            RestorePrimaryFromBackup();
            return backup;
        }

        LastLoadStatus = CalendarLoadStatus.Default;
        return new CalendarState();
    }

    public bool Save(CalendarState state)
    {
        if (!Validate(state))
        {
            _logError("Refused to save invalid calendar state.", null);
            return false;
        }

        var temporaryPath = StatePath + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(temporaryPath, Serialize(state));
            if (TryRead(StatePath, out _)) File.Copy(StatePath, BackupPath, true);
            File.Move(temporaryPath, StatePath, true);
            return true;
        }
        catch (Exception exception)
        {
            _logError("Failed to save calendar state.", exception);
            TryDeleteTemporaryFile(temporaryPath);
            return false;
        }
    }

    public CalendarState Import(string path)
    {
        var state = Deserialize(File.ReadAllText(path));
        if (state is null || !Validate(state)) throw new InvalidDataException("Calendar data is invalid.");
        return state;
    }

    public void Export(string path, CalendarState state) => File.WriteAllText(path, Serialize(state));

    public static bool Validate(CalendarState? state)
    {
        if (state is null || state.Version != 3 || state.Rows is < CalendarLayout.MinimumRows or > CalendarLayout.MaximumRows || state.Opacity is < 0.4 or > 1.0 || state.TodoPanelHeight is < CalendarLayout.TodoPanelMinHeight or > CalendarLayout.TodoPanelMaxHeight)
            return false;
        if (!Enum.IsDefined(state.Style) || !Enum.IsDefined(state.Theme) || !Enum.IsDefined(state.ThemeColor) || !Enum.IsDefined(state.Language))
            return false;
        if (state.Geometry is null || !double.IsFinite(state.Geometry.Left) || !double.IsFinite(state.Geometry.Top) || !double.IsFinite(state.Geometry.Width) || !double.IsFinite(state.Geometry.Height) || state.Geometry.Width <= 0 || state.Geometry.Height <= 0)
            return false;
        if (state.PermanentMarkdown is null || state.PermanentMarkdown.Length > CalendarLayout.MaximumMarkdownLength || state.DateMarkdown is null || state.DateMarkdown.Count > 10000)
            return false;

        return state.DateMarkdown.All(item =>
            DateTime.TryParseExact(item.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
            item.Value is not null && item.Value.Length <= CalendarLayout.MaximumMarkdownLength);
    }

    public static string Serialize(CalendarState state) => JsonSerializer.Serialize(state, Options);
    public static CalendarState? Deserialize(string json) => JsonSerializer.Deserialize<CalendarState>(json, Options);

    private bool TryRead(string path, out CalendarState state)
    {
        state = null!;
        if (!File.Exists(path)) return false;
        try
        {
            var candidate = Deserialize(File.ReadAllText(path));
            if (!Validate(candidate))
            {
                _logError($"Calendar data failed validation: {path}", null);
                return false;
            }
            state = candidate!;
            return true;
        }
        catch (Exception exception)
        {
            _logError($"Failed to read calendar data: {path}", exception);
            return false;
        }
    }

    private void RestorePrimaryFromBackup()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.Copy(BackupPath, StatePath, true);
        }
        catch (Exception exception)
        {
            _logError("Loaded backup but failed to restore the primary data file.", exception);
        }
    }

    private void TryDeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) { _logError("Failed to remove temporary data file.", exception); }
    }
}
