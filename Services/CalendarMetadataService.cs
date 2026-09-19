using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ScrewCalendar;

public sealed class CalendarMetadataService
{
    private sealed class MetadataDocument
    {
        public Dictionary<string, HolidayRecord> Holidays { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> SolarTerms { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class HolidayRecord
    {
        public string Name { get; set; } = string.Empty;
        public bool IsOffDay { get; set; }
    }

    private readonly Dictionary<string, HolidayInfo> _holidays;
    private readonly Dictionary<string, string> _solarTerms;

    public CalendarMetadataService(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<MetadataDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MetadataDocument();
            _holidays = new Dictionary<string, HolidayInfo>(StringComparer.Ordinal);
            foreach (var item in document.Holidays)
                _holidays[item.Key] = new HolidayInfo(item.Value.Name, item.Value.IsOffDay);
            _solarTerms = new Dictionary<string, string>(document.SolarTerms, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Failed to load calendar metadata: {path}", exception);
            _holidays = new Dictionary<string, HolidayInfo>(StringComparer.Ordinal);
            _solarTerms = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static CalendarMetadataService CreateDefault() => new(Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "calendar-data.json"));
    public HolidayInfo? GetHoliday(DateTime date) => _holidays.TryGetValue(Key(date), out var value) ? value : null;
    public string? GetSolarTerm(DateTime date) => _solarTerms.TryGetValue(Key(date), out var value) ? value : null;
    private static string Key(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
