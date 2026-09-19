using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrewCalendar;

var tests = new (string Name, Action Run)[]
{
    ("Calendar math", TestCalendarMath),
    ("Fixed layout contract", TestLayoutContract),
    ("Independent component sizing", TestComponentSizing),
    ("Primary persistence", TestPrimaryPersistence),
    ("Backup recovery", TestBackupRecovery),
    ("Version 3 validation", TestValidation),
    ("Markdown documents", TestMarkdownDocuments),
    ("Normal editor formatting toolbar", TestNormalEditorFormattingToolbar),
    ("Archived Markdown images", TestMarkdownImageStore),
    ("Localization keys", TestLocalizationKeys),
    ("Calendar metadata", TestCalendarMetadata)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception exception) { failures.Add($"FAIL  {test.Name}: {exception.Message}"); }
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

static void TestCalendarMath()
{
    Equal(0, CalendarMath.MondayColumn(new DateTime(2026, 9, 14)), "Monday column");
    Equal(6, CalendarMath.MondayColumn(new DateTime(2026, 9, 20)), "Sunday column");
    Equal(new DateTime(2026, 9, 14), CalendarMath.MondayOf(new DateTime(2026, 9, 19)), "MondayOf");
    var rows = CalendarMath.BuildRows(new DateTime(2026, 9, 19), 7, false);
    Equal("2026-09", rows[0].MonthLabel, "Month label");
    Equal(new DateTime(2026, 9, 1), rows[0].Dates[1], "September 2026 starts Tuesday");
}

static void TestLayoutContract()
{
    Equal(5, CalendarLayout.MinimumRows, "Minimum rows");
    Equal(10, CalendarLayout.MaximumRows, "Maximum rows");
    Equal(10, CalendarLayout.ClampRows(12), "Rows clamp to current maximum");
    Equal(118d, CalendarLayout.CellWidth, "Cell width");
    Equal(2d, CalendarLayout.CellMargin, "Cell margin");
    Equal(122d, CalendarLayout.ColumnWidth, "Column width");
    Equal(854d, CalendarLayout.GridWidth, "Grid width");
    Equal(105d, CalendarLayout.CellHeight, "Cell height");
    Equal(16d, CalendarLayout.DialogHorizontalPadding, "Dialog horizontal padding");
    Equal(14d, CalendarLayout.DialogVerticalPadding, "Dialog vertical padding");
    Equal(20d, CalendarLayout.SettingsDialogLeftPadding, "Settings left padding");
    Equal(12d, CalendarLayout.SettingsScrollBarLeftMargin, "Settings scrollbar left spacing");
}

static void TestComponentSizing()
{
    var withTodo = CalendarComponentSizing.FitCalendarWidth(800, 800, 220, 300, 1040);
    Equal(220d, withTodo.TodoHeight, "Calendar resize preserves todo height");
    Equal(withTodo.CalendarHeight + 220, withTodo.TotalHeight, "Total height composition");
    Near(withTodo.Width * CalendarLayout.GridWidth / CalendarLayout.CardWidth, CalendarComponentSizing.TodoPanelWidth(withTodo.Width, true), "Widget todo width follows visible calendar content");
}

static void TestPrimaryPersistence()
{
    WithTemporaryDirectory(path =>
    {
        var store = new CalendarDataStore(path, (message, exception) => throw new InvalidOperationException(message, exception));
        var state = ValidState("# Primary");
        True(store.Save(state), "Save should succeed");
        var loaded = store.Load();
        Equal("# Primary", loaded.DateMarkdown["2026-09-19"], "Loaded Markdown");
        Equal(CalendarLoadStatus.Primary, store.LastLoadStatus, "Load status");
    });
}

static void TestBackupRecovery()
{
    WithTemporaryDirectory(path =>
    {
        var store = new CalendarDataStore(path, (_, _) => { });
        True(store.Save(ValidState("- [ ] Backup")), "First save");
        True(store.Save(ValidState("- [ ] Primary")), "Second save");
        File.WriteAllText(store.StatePath, "{ damaged json");
        var recovered = store.Load();
        Equal("- [ ] Backup", recovered.DateMarkdown["2026-09-19"], "Recovered backup Markdown");
        Equal(CalendarLoadStatus.Backup, store.LastLoadStatus, "Backup status");
    });
}

static void TestValidation()
{
    var valid = ValidState("- [ ] Valid");
    True(CalendarDataStore.Validate(valid), "Version 3 state accepted");
    valid.Version = 2;
    True(!CalendarDataStore.Validate(valid), "Old state version rejected");
    valid.Version = 3;
    valid.Rows = 11;
    True(!CalendarDataStore.Validate(valid), "Rows above current maximum rejected");
    valid.Rows = 7;
    valid.DateMarkdown["invalid"] = "text";
    True(!CalendarDataStore.Validate(valid), "Invalid date key rejected");
}

static void TestMarkdownDocuments()
{
    const string markdown = "# Today\n\n- [ ] First\n- [x] Second\n- Plain";
    var lines = MarkdownDocumentService.ParseLines(markdown);
    Equal(MarkdownLineKind.Heading1, lines[0].Kind, "Heading parsed");
    Equal(MarkdownLineKind.Task, lines[1].Kind, "Task parsed");
    True(lines[2].IsChecked, "Completed task parsed");
    var toggled = MarkdownDocumentService.ToggleTask(markdown, 0, true);
    True(toggled.Contains("- [x] First", StringComparison.Ordinal), "Task state written to Markdown");
    var roundTrip = MarkdownDocumentService.SerializeLines(lines);
    True(roundTrip.Contains("# Today", StringComparison.Ordinal), "Visual editor preserves heading");
}

static void TestNormalEditorFormattingToolbar()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var editor = new MarkdownBlockEditor(
                Brushes.Black,
                Brushes.Gray,
                Brushes.White,
                Brushes.LightGray,
                Brushes.Blue,
                new FontFamily("Segoe UI"),
                _ => null);
            editor.SetMarkdown("Toolbar test");
            editor.ApplyKind(MarkdownLineKind.Heading1);
            editor.ApplyKind(MarkdownLineKind.Heading2);
            editor.ApplyKind(MarkdownLineKind.Heading3);
            editor.ApplyKind(MarkdownLineKind.Bullet);
            editor.ApplyKind(MarkdownLineKind.Task);
            True(editor.GetMarkdown().StartsWith("- [ ] Toolbar test", StringComparison.Ordinal), "Toolbar updates the active visual block without reparenting errors");
            editor.AddImage("assets/test.png", "test");
            True(editor.GetMarkdown().Contains("![test](assets/test.png)", StringComparison.Ordinal), "Visual editor inserts an archived image block");
            editor.FocusEditor();
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Normal editor toolbar failed.", failure);
}

static void TestMarkdownImageStore()
{
    WithTemporaryDirectory(path =>
    {
        True(MarkdownImageStore.IsSupportedSourceFile("photo.PNG"), "PNG file accepted regardless of case");
        True(MarkdownImageStore.IsSupportedSourceFile("photo.jpeg"), "JPEG file accepted");
        True(!MarkdownImageStore.IsSupportedSourceFile("notes.txt"), "Non-image file rejected");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var store = new MarkdownImageStore(Path.Combine(path, "assets"));
                var pixels = new byte[] { 12, 34, 56, 255, 78, 90, 123, 255 };
                var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
                var first = store.Save(image);
                var second = store.Save(image);
                Equal(first, second, "Identical image content uses one hash name");
                Equal(64, Path.GetFileNameWithoutExtension(first).Length, "SHA-256 image file name");

                var original = Path.Combine(path, "original.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(original)) encoder.Save(stream);
                var archived = store.Import(original);
                File.Delete(original);
                True(store.Load(archived) is not null, "Archived image survives deletion of its original file");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Markdown image archive failed.", failure);
    });
}

static void TestLocalizationKeys()
{
    var localeDirectory = Path.Combine(AppContext.BaseDirectory, "locales");
    var chinese = ReadKeys(Path.Combine(localeDirectory, "zh-CN.json"));
    var english = ReadKeys(Path.Combine(localeDirectory, "en-US.json"));
    Equal(chinese.Count, english.Count, "Locale key count");
    True(chinese.SetEquals(english), "Chinese and English locale keys differ");
}

static void TestCalendarMetadata()
{
    var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "calendar-data.json");
    var metadata = new CalendarMetadataService(path);
    var holiday = metadata.GetHoliday(new DateTime(2026, 10, 1));
    True(holiday is not null && holiday.IsOffDay, "Holiday lookup");
    Equal("秋分", metadata.GetSolarTerm(new DateTime(2026, 9, 23)), "Solar-term lookup");
}

static CalendarState ValidState(string markdown) => new()
{
    PermanentMarkdown = "- [ ] Permanent",
    DateMarkdown = new Dictionary<string, string> { ["2026-09-19"] = markdown }
};

static HashSet<string> ReadKeys(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
}

static void WithTemporaryDirectory(Action<string> action)
{
    var path = Path.Combine(Path.GetTempPath(), "ScrewCalendarTests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(path);
    try { action(path); }
    finally { Directory.Delete(path, true); }
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}

static void Near(double expected, double actual, string message, double tolerance = 0.000001)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}
