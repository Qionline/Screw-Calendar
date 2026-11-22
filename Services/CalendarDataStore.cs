using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrewCalendar;

public enum CalendarLoadStatus { Default, Primary, Backup, Temporary, StartupBackup }

public sealed class CalendarDataStore
{
    private enum SnapshotStatus { Missing, Valid, Corrupt, Unavailable }

    private const string StartupBackupTimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
    private static readonly TimeSpan StartupBackupRetention = TimeSpan.FromDays(7);

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
    internal string StartupBackupsDirectory => Path.Combine(DataDirectory, "backups");
    public CalendarLoadStatus LastLoadStatus { get; private set; }
    internal bool NeedsRecovery { get; private set; }

    public static CalendarDataStore CreateDefault()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrewCalendar", "data");
        return new CalendarDataStore(appData);
    }

    internal CalendarState LoadOnStartup(DateTimeOffset now)
    {
        var state = Load();
        if (LastLoadStatus == CalendarLoadStatus.Default) return state;

        try
        {
            Directory.CreateDirectory(StartupBackupsDirectory);
            var timestamp = now.UtcDateTime.ToString(StartupBackupTimestampFormat, CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(StartupBackupsDirectory, $"calendar-{timestamp}-{Guid.NewGuid():N}.json");
            WriteSnapshot(backupPath, Serialize(state), SnapshotStatus.Missing);
        }
        catch (Exception exception)
        {
            _logError("Failed to create a startup calendar backup.", exception);
            return state;
        }

        var cutoff = now.UtcDateTime - StartupBackupRetention;
        foreach (var (path, createdAt) in GetStartupBackups(out _))
        {
            if (createdAt >= cutoff) continue;
            try { File.Delete(path); }
            catch (Exception exception) { _logError($"Failed to remove an expired startup calendar backup: {path}", exception); }
        }

        return state;
    }

    public CalendarState Load()
    {
        NeedsRecovery = false;
        var primaryStatus = ReadSnapshot(StatePath, out var primary, out _);
        if (primaryStatus == SnapshotStatus.Valid)
        {
            LastLoadStatus = CalendarLoadStatus.Primary;
            return primary;
        }

        var backupStatus = ReadSnapshot(BackupPath, out var backup, out _);
        if (backupStatus == SnapshotStatus.Valid)
        {
            LastLoadStatus = CalendarLoadStatus.Backup;
            RestorePrimary(backup, primaryStatus);
            return backup;
        }

        var temporaryStatus = ReadSnapshot(StatePath + ".tmp", out var temporary, out _);
        if (temporaryStatus == SnapshotStatus.Valid)
        {
            LastLoadStatus = CalendarLoadStatus.Temporary;
            RestorePrimary(temporary, primaryStatus);
            return temporary;
        }

        var startupBackups = GetStartupBackups(out var backupScanFailed);
        foreach (var (path, _) in startupBackups.OrderByDescending(backup => backup.CreatedAt))
        {
            if (ReadSnapshot(path, out var snapshot, out _) != SnapshotStatus.Valid) continue;
            LastLoadStatus = CalendarLoadStatus.StartupBackup;
            RestorePrimary(snapshot, primaryStatus);
            return snapshot;
        }

        NeedsRecovery = primaryStatus != SnapshotStatus.Missing || backupStatus != SnapshotStatus.Missing ||
            temporaryStatus != SnapshotStatus.Missing || backupScanFailed || startupBackups.Count > 0;
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

        try
        {
            var primaryStatus = ReadSnapshot(StatePath, out _, out var previousJson);
            var backupStatus = ReadSnapshot(BackupPath, out _, out _);
            if (primaryStatus == SnapshotStatus.Unavailable || backupStatus == SnapshotStatus.Unavailable)
                return false;

            Directory.CreateDirectory(DataDirectory);
            var json = Serialize(state);
            if (primaryStatus == SnapshotStatus.Valid && (previousJson != json || backupStatus != SnapshotStatus.Valid))
                WriteSnapshot(BackupPath, previousJson, backupStatus);

            if (primaryStatus != SnapshotStatus.Valid || previousJson != json)
                WriteSnapshot(StatePath, json, primaryStatus);
            NeedsRecovery = false;

            if (primaryStatus != SnapshotStatus.Valid && backupStatus != SnapshotStatus.Valid)
            {
                try { WriteSnapshot(BackupPath, json, backupStatus); }
                catch (Exception exception) { _logError("Saved calendar state but failed to create a backup.", exception); }
            }

            return true;
        }
        catch (Exception exception)
        {
            _logError("Failed to save calendar state.", exception);
            return false;
        }
    }

    public CalendarState Import(string path) => DeserializeValidated(File.ReadAllText(path));

    private static CalendarState DeserializeValidated(string json)
    {
        var state = Deserialize(json);
        if (state is null || !Validate(state)) throw new InvalidDataException("Calendar data is invalid.");
        RepeatDocumentService.NormalizeReferences(state);
        // Moving source anchors can increase the destination's combined document length.
        if (!Validate(state)) throw new InvalidDataException("Normalized calendar data exceeds storage limits.");
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
            item.Value is not null && item.Value.Length <= CalendarLayout.MaximumMarkdownLength) && ValidateRepeats(state);
    }

    private static bool ValidateRepeats(CalendarState state)
    {
        if (state.RepeatBlocks is null || state.DateRepeatReferences is null || state.RepeatBlocks.Count > 10000 ||
            state.DateRepeatReferences.Count > 10000 || state.DateMarkdown.Keys.Union(state.DateRepeatReferences.Keys).Count() > 10000) return false;
        foreach (var (id, source) in state.RepeatBlocks)
        {
            if (!Guid.TryParseExact(id, "N", out _) || source is null || source.Length > CalendarLayout.MaximumMarkdownLength) return false;
            var parsed = RepeatBlockParser.Parse(source);
            if (parsed.Count != 1 || parsed[0].Start != 0 || parsed[0].Length != source.Length) return false;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (date, references) in state.DateRepeatReferences)
        {
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                references is null || references.Count is 0 or > 10000) return false;
            var local = state.DateMarkdown.GetValueOrDefault(date, string.Empty);
            long length = local.Length;
            var position = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                if (reference is null || reference.Id is null || !ids.Add(reference.Id) ||
                    !state.RepeatBlocks.TryGetValue(reference.Id, out var block) || reference.Offset < position || reference.Offset > local.Length) return false;
                used.Add(reference.Id);
                position = reference.Offset;
                length += block.Length;
            }
            if (length > CalendarLayout.MaximumMarkdownLength) return false;
        }
        return used.Count == state.RepeatBlocks.Count;
    }

    public static string Serialize(CalendarState state) => JsonSerializer.Serialize(state, Options);
    public static CalendarState? Deserialize(string json) => JsonSerializer.Deserialize<CalendarState>(json, Options);

    private SnapshotStatus ReadSnapshot(string path, out CalendarState state, out string json)
    {
        state = null!;
        json = string.Empty;
        try
        {
            json = File.ReadAllText(path);
            state = DeserializeValidated(json);
            return SnapshotStatus.Valid;
        }
        catch (FileNotFoundException)
        {
            return SnapshotStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return SnapshotStatus.Missing;
        }
        catch (Exception exception)
        {
            _logError($"Failed to read calendar data: {path}", exception);
            return exception is JsonException or InvalidDataException ? SnapshotStatus.Corrupt : SnapshotStatus.Unavailable;
        }
    }

    private List<(string Path, DateTime CreatedAt)> GetStartupBackups(out bool scanFailed)
    {
        var backups = new List<(string Path, DateTime CreatedAt)>();
        scanFailed = false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(StartupBackupsDirectory, "calendar-*.json"))
            {
                var nameParts = Path.GetFileNameWithoutExtension(path).Split('-');
                if (nameParts.Length == 3 && Guid.TryParseExact(nameParts[2], "N", out _) &&
                    DateTime.TryParseExact(nameParts[1], StartupBackupTimestampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
                    backups.Add((path, createdAt));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The directory is created after the first successful load.
        }
        catch (Exception exception)
        {
            scanFailed = true;
            _logError("Failed to list startup calendar backups.", exception);
        }

        return backups;
    }

    private void RestorePrimary(CalendarState state, SnapshotStatus primaryStatus)
    {
        if (primaryStatus == SnapshotStatus.Unavailable) return;
        try
        {
            // A recovered .tmp may be the only good copy, so use separate staging.
            WriteSnapshot(StatePath, Serialize(state), primaryStatus, ".restore.tmp");
        }
        catch (Exception exception)
        {
            _logError("Loaded a recovery snapshot but failed to restore the primary data file.", exception);
        }
    }

    private void WriteSnapshot(string path, string json, SnapshotStatus previousStatus, string temporarySuffix = ".tmp")
    {
        var temporaryPath = path + temporarySuffix;
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true))
                    writer.Write(json);
                stream.Flush(flushToDisk: true);
            }

            if (previousStatus == SnapshotStatus.Corrupt)
            {
                var preservedPath = path + "." + Guid.NewGuid().ToString("N") + ".corrupt";
                File.Copy(path, preservedPath);
                _logError($"Preserved unreadable calendar data: {preservedPath}", null);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private void TryDeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) { _logError("Failed to remove temporary data file.", exception); }
    }
}
