using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
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
    ("Markdown image selection replacement", TestMarkdownImageSelectionReplacement),
    ("Archived Markdown images", TestMarkdownImageStore),
    ("Localization keys", TestLocalizationKeys),
    ("Calendar metadata", TestCalendarMetadata),
    ("Release metadata", TestReleaseMetadata)
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
            True(editor.Child is TextEditor, "Markdown editor uses one native AvalonEdit selection host");
            editor.SetMarkdown("Toolbar test");
            editor.ApplyKind(MarkdownLineKind.Heading1);
            editor.ApplyKind(MarkdownLineKind.Heading2);
            editor.ApplyKind(MarkdownLineKind.Heading3);
            editor.ApplyKind(MarkdownLineKind.Bullet);
            editor.ApplyKind(MarkdownLineKind.Task);
            True(editor.GetMarkdown().StartsWith("- [ ] Toolbar test", StringComparison.Ordinal), "Toolbar updates the active visual block without reparenting errors");

            editor.SetMarkdown("First\nSecond\nThird");
            var textEditor = (TextEditor)editor.Child!;
            textEditor.Select(0, textEditor.Document.TextLength);
            editor.ApplyKind(MarkdownLineKind.Task);
            Equal(string.Join("\n", new[] { "- [ ] First", "- [ ] Second", "- [ ] Third" }), editor.GetMarkdown(), "Native selection formats every selected paragraph");

            editor.SetMarkdown("- [ ] First\n- [x] Second\n- Plain");
            textEditor = (TextEditor)editor.Child!;
            var originalClipboard = Clipboard.GetDataObject();
            try
            {
                textEditor.Select(0, textEditor.Document.TextLength);
                textEditor.Copy();
                Equal(string.Join("\n", new[] { "- [ ] First", "- [x] Second", "- Plain" }), Clipboard.GetText().Replace("\r\n", "\n", StringComparison.Ordinal), "Copy exports selected Markdown syntax");

                var pasted = new MarkdownBlockEditor(Brushes.Black, Brushes.Gray, Brushes.White, Brushes.LightGray, Brushes.Blue, new FontFamily("Segoe UI"), _ => null);
                pasted.SetMarkdown(string.Empty);
                pasted.FocusEditor();
                ((TextEditor)pasted.Child!).Paste();
                Equal(string.Join("\n", new[] { "- [ ] First", "- [x] Second", "- Plain" }), pasted.GetMarkdown().Replace("\r\n", "\n", StringComparison.Ordinal), "Paste restores Markdown block kinds");
            }
            finally
            {
                if (originalClipboard is not null) Clipboard.SetDataObject(originalClipboard);
            }

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

static void TestMarkdownImageSelectionReplacement()
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
                _ => BitmapSource.Create(
                    2,
                    1,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    new byte[] { 12, 34, 56, 255, 78, 90, 123, 255 },
                    8));
            editor.ContentChanged += (_, _) => _ = editor.GetMarkdown();
            editor.SetMarkdown("Before\n![test](assets/test.png)\nAfter");
            var textEditor = (TextEditor)editor.Child!;
            textEditor.SelectAll();
            textEditor.Document.Replace(textEditor.SelectionStart, textEditor.SelectionLength, "replacement");
            Equal("replacement", editor.GetMarkdown(), "Typing replaces a selection that contains an image");

            editor.SetMarkdown("Before\n![test](assets/test.png)\nAfter");
            textEditor = (TextEditor)editor.Child!;
            var imageLine = textEditor.Document.Lines.Single(line => textEditor.Document.GetText(line.Offset, line.Length).StartsWith("![", StringComparison.Ordinal));
            textEditor.Select(imageLine.Offset, imageLine.Length);
            textEditor.Document.Replace(textEditor.SelectionStart, textEditor.SelectionLength, "replacement");
            Equal(string.Join("\n", new[] { "Before", "replacement", "After" }), editor.GetMarkdown(), "Typing replaces an image-only selection without deleting the editor");

            editor.SetMarkdown("Before\n![test](assets/test.png)\nAfter");
            textEditor = (TextEditor)editor.Child!;
            imageLine = textEditor.Document.Lines.Single(line => textEditor.Document.GetText(line.Offset, line.Length).StartsWith("![", StringComparison.Ordinal));
            textEditor.Select(imageLine.Offset, imageLine.Length);
            textEditor.Document.Remove(textEditor.SelectionStart, textEditor.SelectionLength);
            True(!editor.GetMarkdown().Contains("assets/test.png", StringComparison.Ordinal), "Backspace removes an image-only selection");

            editor.SetMarkdown("第一行\r\n- 项目\r\n# 标题\r\n\r\n![logo](assets/test.png)\r\n\r\n- [x] 已完成\r\n- [ ]");
            using var visualHost = new HwndSource(new HwndSourceParameters("ScrewCalendarMarkdownEmptyTaskTest") { Width = 320, Height = 220 });
            visualHost.RootVisual = editor;
            editor.Measure(new Size(320, 220));
            editor.Arrange(new Rect(0, 0, 320, 220));
            editor.UpdateLayout();
            textEditor.TextArea.TextView.EnsureVisualLines();
            editor.FocusEditor();
            Equal("- [ ]", textEditor.Document.GetText(textEditor.Document.Lines.Last().Offset, textEditor.Document.Lines.Last().Length), "Empty task line remains valid Markdown");

            editor.SetMarkdown("Before\nAfter");
            textEditor = (TextEditor)editor.Child!;
            textEditor.CaretOffset = textEditor.Document.GetLineByNumber(1).EndOffset;
            editor.AddImage("assets/test.png", "test");
            Equal(string.Join("\n", new[] { "Before", "![test](assets/test.png)", "After" }), editor.GetMarkdown().Replace("\r\n", "\n", StringComparison.Ordinal), "Image insertion creates one source line without duplicate blank lines");
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Image selection replacement timed out");
    if (failure is not null) throw new InvalidOperationException("Image selection replacement failed.", failure);
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
                var decoded = store.Load(archived);
                True(decoded is WriteableBitmap { IsFrozen: true }, "Archived image is loaded as a frozen public bitmap");

                var editor = new MarkdownBlockEditor(Brushes.Black, Brushes.Gray, Brushes.White, Brushes.LightGray, Brushes.Blue, new FontFamily("Segoe UI"), store.Load);
                editor.ContentChanged += (_, _) => _ = editor.GetMarkdown();
                editor.SetMarkdown($"![test]({archived})");
                var textEditor = (TextEditor)editor.Child!;
                using var source = new HwndSource(new HwndSourceParameters("ScrewCalendarMarkdownImageTest") { Width = 640, Height = 480 });
                source.RootVisual = editor;
                editor.Measure(new Size(640, 480));
                editor.Arrange(new Rect(0, 0, 640, 480));
                editor.UpdateLayout();
                textEditor.TextArea.TextView.EnsureVisualLines();
                var remove = FindVisualChildren<Button>(editor).SingleOrDefault();
                True(remove is not null, "Image is rendered as an AvalonEdit inline visual");
                remove!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                True(!editor.GetMarkdown().Contains(archived, StringComparison.Ordinal), "Image button removes only the source reference line");
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

    const string remoteJson = """
        { "year": 2027, "papers": [], "days": [
          { "name": "测试节日", "date": "2027-01-01", "isOffDay": true },
          { "name": "测试调班", "date": "2027-01-02", "isOffDay": false }
        ] }
        """;
    True(metadata.TryApplyHolidayDocument(remoteJson, 2027), "Remote holiday document validation");
    True(metadata.GetHoliday(new DateTime(2027, 1, 1))?.IsOffDay == true, "Remote holiday merge");
    True(metadata.GetHoliday(new DateTime(2027, 1, 2))?.IsOffDay == false, "Remote workday merge");
    True(!metadata.TryApplyHolidayDocument(remoteJson, 2028), "Reject mismatched remote holiday year");

    WithTemporaryDirectory(directory =>
    {
        var cachedMetadata = new CalendarMetadataService(path, directory);
        using var updater = new HolidayUpdateService(cachedMetadata, directory, (_, _) => System.Threading.Tasks.Task.FromResult<string?>(remoteJson));
        updater.RefreshForCalendarYearAsync(2027).GetAwaiter().GetResult();
        True(File.Exists(Path.Combine(directory, "2027.json")), "Remote holiday cache write");
        True(cachedMetadata.GetHoliday(new DateTime(2027, 1, 1))?.IsOffDay == true, "Downloaded holiday applied");
        var reloadedMetadata = new CalendarMetadataService(path, directory);
        True(reloadedMetadata.GetHoliday(new DateTime(2027, 1, 2))?.IsOffDay == false, "Holiday cache reload");
    });
}

static void TestReleaseMetadata()
{
    var repositoryDirectory = Path.Combine(AppContext.BaseDirectory, "Repository");
    var project = XDocument.Load(Path.Combine(repositoryDirectory, "ScrewCalendar.csproj"));
    var version = project.Descendants("Version").SingleOrDefault()?.Value;
    True(Version.TryParse(version, out var parsedVersion), "Application version must be numeric SemVer");
    True(parsedVersion is not null && parsedVersion.Major >= 0 && parsedVersion.Minor >= 0 && parsedVersion.Build >= 0,
        "Application version must contain major, minor, and patch numbers");
    True(!project.Descendants("AssemblyVersion").Any(), "Assembly version should derive from Version");
    True(!project.Descendants("FileVersion").Any(), "File version should derive from Version");

    var publishedFiles = project.Descendants("None")
        .Where(item => item.Attribute("CopyToPublishDirectory") is not null)
        .Select(item => item.Attribute("Update")?.Value)
        .Where(item => item is not null)
        .Select(item => item!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    True(publishedFiles.Contains("LICENSE"), "Publish includes GPL license");
    True(publishedFiles.Contains("README.md"), "Publish includes README");
    True(publishedFiles.Contains("THIRD_PARTY_NOTICES.md"), "Publish includes third-party notices");

    using var sdkDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryDirectory, "global.json")));
    var sdk = sdkDocument.RootElement.GetProperty("sdk");
    True(Version.TryParse(sdk.GetProperty("version").GetString(), out var sdkVersion) && sdkVersion.Build >= 0,
        "SDK version must be fully qualified");
    Equal("latestFeature", sdk.GetProperty("rollForward").GetString(), "SDK roll-forward policy");

    var packageVersions = XDocument.Load(Path.Combine(repositoryDirectory, "Directory.Packages.props"));
    var markdigVersion = packageVersions.Descendants("PackageVersion")
        .Single(item => string.Equals(item.Attribute("Include")?.Value, "Markdig", StringComparison.Ordinal))
        .Attribute("Version")?.Value;
    True(Version.TryParse(markdigVersion, out _), "Markdig version must be centrally managed");
    var thirdPartyNotices = File.ReadAllText(Path.Combine(repositoryDirectory, "THIRD_PARTY_NOTICES.md"));
    True(thirdPartyNotices.Contains($"## Markdig {markdigVersion}", StringComparison.Ordinal),
        "Third-party notice must match the centrally managed Markdig version");
    var avalonEditVersion = packageVersions.Descendants("PackageVersion")
        .Single(item => string.Equals(item.Attribute("Include")?.Value, "AvalonEdit", StringComparison.Ordinal))
        .Attribute("Version")?.Value;
    True(Version.TryParse(avalonEditVersion, out _), "AvalonEdit version must be centrally managed");
    True(thirdPartyNotices.Contains($"## AvalonEdit {avalonEditVersion}", StringComparison.Ordinal),
        "Third-party notice must match the centrally managed AvalonEdit version");
    True(!project.Descendants("PackageReference").Any(item => item.Attribute("Version") is not null),
        "Application package references should use central versions");

    var workflowDirectory = Path.Combine(repositoryDirectory, ".github", "workflows");
    const string releaseSdkVersion = "8.0.425";
    foreach (var workflowName in new[] { "build.yml", "release.yml" })
    {
        var workflow = File.ReadAllText(Path.Combine(workflowDirectory, workflowName));
        True(workflow.Contains($"dotnet-version: {releaseSdkVersion}", StringComparison.Ordinal),
            $"{workflowName} must use the locked release SDK {releaseSdkVersion}");
        True(!workflow.Contains("global-json-file: global.json", StringComparison.Ordinal),
            $"{workflowName} must not use the rolling developer SDK policy");
        True(!workflow.Contains("--self-contained", StringComparison.Ordinal) &&
             !workflow.Contains("-r win-x64", StringComparison.Ordinal),
            $"{workflowName} must use project publish settings");
        True(!workflow.Contains("win-x64", StringComparison.Ordinal),
            $"{workflowName} must derive the runtime identifier from the project");
    }
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

static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
{
    if (root is null) yield break;
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is T match) yield return match;
        foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
    }
}

static void Near(double expected, double actual, string message, double tolerance = 0.000001)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
}
