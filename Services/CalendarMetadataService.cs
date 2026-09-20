using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ScrewCalendar;

public sealed class CalendarMetadataService
{
    private sealed class MetadataDocument
    {
        public Dictionary<string, HolidayRecord> Holidays { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SolarTerms { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RemoteHolidayDocument
    {
        public int Year { get; set; }
        public List<RemoteHolidayRecord> Days { get; set; } = [];
    }

    private class HolidayRecord
    {
        public string Name { get; set; } = string.Empty;
        public bool IsOffDay { get; set; }
    }

    private sealed class RemoteHolidayRecord : HolidayRecord
    {
        public string Date { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object _holidayGate = new();
    private readonly Dictionary<string, HolidayInfo> _builtInHolidays = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Dictionary<string, HolidayInfo>> _remoteHolidayYears = [];
    private readonly Dictionary<string, string> _solarTerms;
    private Dictionary<string, HolidayInfo> _holidays = new(StringComparer.Ordinal);

    public CalendarMetadataService(string path, string? holidayCacheDirectory = null)
    {
        try
        {
            var document = JsonSerializer.Deserialize<MetadataDocument>(File.ReadAllText(path), JsonOptions) ?? new MetadataDocument();
            foreach (var item in document.Holidays)
                _builtInHolidays[item.Key] = new HolidayInfo(item.Value.Name, item.Value.IsOffDay);
            _solarTerms = new Dictionary<string, string>(document.SolarTerms, StringComparer.Ordinal);
            _holidays = new Dictionary<string, HolidayInfo>(_builtInHolidays, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Failed to load calendar metadata: {path}", exception);
            _solarTerms = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        LoadHolidayCache(holidayCacheDirectory);
    }

    public static CalendarMetadataService CreateDefault() => new(
        Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "calendar-data.json"),
        HolidayUpdateService.DefaultCacheDirectory);

    public HolidayInfo? GetHoliday(DateTime date)
    {
        var holidays = Volatile.Read(ref _holidays);
        return holidays.TryGetValue(Key(date), out var value) ? value : null;
    }

    public string? GetSolarTerm(DateTime date) => _solarTerms.TryGetValue(Key(date), out var value) ? value : null;

    internal bool TryApplyHolidayDocument(string json, int expectedYear)
    {
        if (!TryParseHolidayDocument(json, expectedYear, out var holidays)) return false;
        lock (_holidayGate)
        {
            _remoteHolidayYears[expectedYear] = holidays;
            RebuildHolidaySnapshot();
        }
        return true;
    }

    internal static bool TryParseHolidayDocument(string json, int expectedYear, out Dictionary<string, HolidayInfo> holidays)
    {
        holidays = new Dictionary<string, HolidayInfo>(StringComparer.Ordinal);
        if (expectedYear is < 2007 or > 9998 || string.IsNullOrWhiteSpace(json) || json.Length > HolidayUpdateService.MaximumDocumentCharacters)
            return false;

        try
        {
            var document = JsonSerializer.Deserialize<RemoteHolidayDocument>(json, JsonOptions);
            if (document is null || document.Year != expectedYear || document.Days.Count > 100) return false;
            foreach (var day in document.Days)
            {
                if (string.IsNullOrWhiteSpace(day.Name) || day.Name.Length > 32 ||
                    !DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    date.Year < expectedYear - 1 || date.Year > expectedYear ||
                    !holidays.TryAdd(Key(date), new HolidayInfo(day.Name.Trim(), day.IsOffDay)))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void LoadHolidayCache(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
            try
            {
                if (!TryApplyHolidayDocument(File.ReadAllText(path), year))
                    AppLogger.Error($"Ignored invalid holiday cache: {path}");
            }
            catch (Exception exception)
            {
                AppLogger.Error($"Failed to load holiday cache: {path}", exception);
            }
        }
    }

    private void RebuildHolidaySnapshot()
    {
        var snapshot = new Dictionary<string, HolidayInfo>(_builtInHolidays, StringComparer.Ordinal);
        foreach (var remoteYear in _remoteHolidayYears.OrderBy(item => item.Key))
        {
            foreach (var key in snapshot.Keys.Where(key => DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year == remoteYear.Key).ToArray())
                snapshot.Remove(key);
            foreach (var holiday in remoteYear.Value)
                snapshot[holiday.Key] = holiday.Value;
        }
        Volatile.Write(ref _holidays, snapshot);
    }

    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
