extern alias CalendarApp;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppEditor = CalendarApp::ScrewCalendar.MarkdownBlockEditor;
using AppState = CalendarApp::ScrewCalendar.CalendarState;
using AppStore = CalendarApp::ScrewCalendar.CalendarDataStore;
using AppWindow = CalendarApp::ScrewCalendar.MainWindow;

namespace ScrewCalendar.Tests;

internal static class MarkdownIntegrationTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "ScrewCalendarMarkdownIntegration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/DesktopWidgets.Toolkit;component/Themes/Controls.xaml", UriKind.Relative)
            });
            app.Resources["UiFont"] = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
            AppWindow? window = null;
            try
            {
                var today = DateTime.Today;
                var key = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var store = new AppStore(folder, (_, _) => { });
                var state = new AppState { ShowTodoPanel = true, PermanentMarkdown = "# 常驻待办\n\n- [ ] 保留原始内容\n\n---\n\n**阅读显示排版**", Rows = 5 };
                state.DateMarkdown[key] = "## 今日待办\n\n- [ ] 完成测试\n\n---\n\n[链接](https://example.com)";
                Check(store.Save(state), "Prepare isolated integration data");
                window = new AppWindow(store, false);
                Invoke(window, "Render");
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                using var host = new HwndSource(new HwndSourceParameters("CalendarMarkdownIntegration") { Width = 1000, Height = 1100 });
                host.RootVisual = root;
                Layout(root);
                var permanent = Field(window, "_permanentSection");
                var daily = Field(window, "_todaySection");
                var editor = (AppEditor)Property(permanent, "Editor");
                var dailyEditor = (AppEditor)Property(daily, "Editor");
                Check(editor.IsPreviewMode && dailyEditor.IsPreviewMode, "Both calendar sections start in reading mode");
                var before = editor.GetMarkdown();
                Invoke(window, "BeginInlineMarkdownEdit", today, true, (int?)0);
                var native = (ICSharpCode.AvalonEdit.TextEditor)editor.Child!;
                native.Document.Insert(native.Document.TextLength, "\n\n未保存的草稿\n");
                Check(store.Load().PermanentMarkdown == before, "Inline typing does not save the draft");
                // An external daily preview must not rebuild or discard the permanent draft.
                SetField(window, "_todayMarkdownPreview", new CalendarApp::ScrewCalendar.RepeatDocument("# 外部草稿\n\n---\n\n- [ ] 新任务", []));
                Invoke(window, "RefreshTodayMarkdownPreview");
                Check(ReferenceEquals(permanent, Field(window, "_permanentSection")) && editor.GetMarkdown().Contains("未保存的草稿", StringComparison.Ordinal), "Daily preview retains an independent inline draft");
                Invoke(window, "CancelInlineMarkdownEdit", permanent);
                Check(editor.IsPreviewMode && editor.GetMarkdown() == before && store.Load().PermanentMarkdown == before, "Inline cancel restores saved source without persisting changes");
                SetField(window, "_todayMarkdownPreview", null);
                Invoke(window, "RefreshTodayMarkdownPreview");
                Invoke(window, "BeginInlineMarkdownEdit", today, true, (int?)0);
                const string saved = "\n# 保存原样\r\n\r\n- [ ] 任务\r\n\r\n---\r\n\n";
                native.Select(0, native.Document.TextLength);
                native.SelectedText = saved;
                var permanentHeader = (Grid)((Grid)Property(permanent, "Root")).Children[1];
                var permanentActions = permanentHeader.Children.OfType<Button>().ToArray();
                Check(permanentActions.Length == 1 && Equals(permanentActions[0].Content, "×"), "Inline editing exposes only a close action, without a separate save button");
                permanentActions[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(store.Load().PermanentMarkdown == saved && editor.GetMarkdown() == saved && editor.IsPreviewMode, "Close automatically saves exact source and returns to the same reader");
                Check(Field(window, "_activeInlineSection") is null && !(bool)Field(window, "_todoEditorActive"), "Closing releases the active editor and desktop input interaction");
                Layout(root);
                var task = Descendants<CheckBox>(editor).First();
                task.IsChecked = true;
                Check(store.Load().PermanentMarkdown == saved.Replace("[ ]", "[x]", StringComparison.Ordinal), "Reading task click persists to the isolated data store");
                var liveReader = Field(window, "_todaySection");
                SetField(window, "_todayMarkdownPreview", new CalendarApp::ScrewCalendar.RepeatDocument("preview one", []));
                Invoke(window, "RefreshTodayMarkdownPreview");
                SetField(window, "_todayMarkdownPreview", new CalendarApp::ScrewCalendar.RepeatDocument("preview two", []));
                Invoke(window, "RefreshTodayMarkdownPreview");
                Check(ReferenceEquals(liveReader, Field(window, "_todaySection")) && dailyEditor.GetMarkdown() == "preview two", "Live preview updates reuse the daily section and text control");
                Check(store.Load().DateMarkdown[key] == state.DateMarkdown[key], "External draft updates never persist implicitly");
                SetField(window, "_todayMarkdownPreview", null);
                Invoke(window, "RefreshTodayMarkdownPreview");
                var loaded = (AppState)Field(window, "_state");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)0);
                var dailyNative = (ICSharpCode.AvalonEdit.TextEditor)dailyEditor.Child!;
                dailyNative.Select(0, dailyNative.Document.TextLength);
                dailyNative.SelectedText = " \r\n\r\n";
                var dailyHeader = (Grid)((Grid)Property(daily, "Root")).Children[1];
                dailyHeader.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(store.Load().DateMarkdown[key] == " \r\n\r\n", "Whitespace-only dates also preserve exact source");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)0);
                dailyNative.Select(0, dailyNative.Document.TextLength);
                dailyNative.SelectedText = "";
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)!, "Empty date document saves");
                Check(!store.Load().DateMarkdown.ContainsKey(key) && !(bool)Field(window, "_todoLayoutHasToday"), "Clearing today's document restores the single-column layout");
                Check(ReferenceEquals(permanent, Field(window, "_permanentSection")), "Column changes retain the permanent reader");
                Check((bool)Invoke(window, "TryPersistMarkdown", today, false, new CalendarApp::ScrewCalendar.RepeatDocument(state.DateMarkdown[key], []), null)!, "Restore the date fixture");
                Invoke(window, "RenderTodoPanel");
                var qa = Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR");
                foreach (var style in Enum.GetValues<CalendarApp::ScrewCalendar.CalendarStyle>())
                {
                    loaded.Style = style;
                    foreach (var theme in Enum.GetValues<CalendarApp::ScrewCalendar.CalendarTheme>())
                    {
                        loaded.Theme = theme;
                        Invoke(window, "Render");
                        Layout(root);
                        Check(editor.IsPreviewMode && dailyEditor.IsPreviewMode, "Style changes preserve both readers");
                        if (!string.IsNullOrEmpty(qa)) Save(root, qa, $"calendar-{style}-{theme}.png");
                    }
                }
                Invoke(window, "BeginInlineMarkdownEdit", today, true, (int?)0);
                Layout(root);
                var sectionRoot = (Grid)Property(permanent, "Root");
                var toolbar = Descendants<WrapPanel>(sectionRoot).Single();
                var sharedToolbar = (WrapPanel)Invoke(window, "CreateMarkdownFormatTools", editor)!;
                var inlineTools = toolbar.Children.OfType<Button>().ToArray();
                var popupTools = sharedToolbar.Children.OfType<Button>().ToArray();
                var toolOrder = new[] { "MD", "H", "B", "•", "☑" };
                Check(inlineTools.Select(button => button.Content).SequenceEqual(toolOrder) && popupTools.Select(button => button.Content).SequenceEqual(toolOrder),
                    "Both toolbar hosts put MD first, followed by heading, bold, bullet and task tools");
                for (var i = 0; i < inlineTools.Length; i++)
                    Check(inlineTools[i].Width == 30 && inlineTools[i].Height == 30 && inlineTools[i].ActualWidth == inlineTools[i].ActualHeight &&
                        inlineTools[i].Padding == popupTools[i].Padding && inlineTools[i].Margin == popupTools[i].Margin &&
                        ReferenceEquals(inlineTools[i].Template, popupTools[i].Template), "Inline tools use the same fixed square dimensions and template as popup tools");
                Check(!editor.IsSourceMode, "Inline editing starts with current-line source reveal");
                var sourceTool = inlineTools.Single(button => Equals(button.Content, "MD"));
                var beforeMode = editor.GetMarkdown();
                sourceTool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(editor.IsSourceMode && editor.GetMarkdown() == beforeMode, "Inline MD tool exposes full source without rewriting it");
                sourceTool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var popupEditor = (AppEditor)Invoke(window, "CreateMarkdownEditor", false)!;
                popupEditor.SetMarkdown("# Popup\n\n- [ ] todo\n---");
                var popupToolbar = (WrapPanel)Invoke(window, "CreateMarkdownFormatTools", popupEditor)!;
                popupToolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "MD")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(popupEditor.IsSourceMode && !editor.IsSourceMode, "Popup source fallback operates on its own shared editor instance");
                native.Select(0, 0);
                var beforeBold = editor.GetMarkdown();
                inlineTools.Single(button => Equals(button.Content, "B")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(editor.GetMarkdown() == "****" + beforeBold, "The shared toolbar bold button operates on the active editor");
                native.Undo();
                if (!string.IsNullOrEmpty(qa)) Save(root, qa, "calendar-inline-edit.png");
                var originalSectionWidth = sectionRoot.Width;
                sectionRoot.Width = 145;
                Layout(root);
                foreach (var tool in inlineTools) Check(tool.ActualWidth == 30 && tool.ActualHeight == 30, "Narrow inline toolbars wrap rather than squeezing buttons");
                if (!string.IsNullOrEmpty(qa)) Save(root, qa, "calendar-inline-edit-narrow.png");
                sectionRoot.Width = originalSectionWidth;
                Layout(root);
                loaded.ShowTodoPanel = false;
                Invoke(window, "RenderTodoPanel");
                Check(!(bool)Field(window, "_todoEditorActive"), "Hiding the panel releases desktop input activation");
                loaded.ShowTodoPanel = true;
                Invoke(window, "RenderTodoPanel");
                Check((bool)Field(window, "_todoEditorActive") && !editor.IsPreviewMode, "Showing the panel restores the retained inline editor");
                Invoke(window, "CancelInlineMarkdownEdit", permanent);
                Check(!((bool)Field(window, "_todoEditorActive")), "Save/cancel releases temporary desktop interaction");
                var dateBeforeRepeat = dailyEditor.GetMarkdown();
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)dailyNative.Document.TextLength);
                var dailyTools = Descendants<WrapPanel>((Grid)Property(daily, "Root")).Single().Children.OfType<Button>().ToArray();
                Check(dailyTools.Length == 6 && Equals(dailyTools[^1].Content, "Rp") && !editor.CanUseRepeat && dailyEditor.CanUseRepeat,
                    "Only date editing exposes the shared repeat tool");
                dailyTools[^1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(dailyEditor.GetRepeatDraft().Bindings.Count == 1 && store.Load().RepeatBlocks.Count == 0, "Button registration stays in the unsaved native draft");
                Invoke(window, "CancelInlineMarkdownEdit", daily);
                Check(dailyEditor.GetMarkdown() == dateBeforeRepeat && dailyEditor.GetRepeatDraft().Bindings.Count == 0 && store.Load().RepeatBlocks.Count == 0,
                    "Real inline cancel drops the draft registration and restores its source");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)dailyNative.Document.TextLength);
                dailyTools = Descendants<WrapPanel>((Grid)Property(daily, "Root")).Single().Children.OfType<Button>().ToArray();
                dailyTools[^1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                dailyNative.SelectedText = "**重复正文**\n- [ ] repeat task\n[重复链接](https://example.com)";
                dailyNative.Document.Insert(0, "当天上方\n");
                dailyNative.Document.Insert(dailyNative.Document.TextLength, "\n当天下方");
                var repeatDraft = dailyEditor.GetRepeatDraft();
                var repeatId = repeatDraft.Bindings.Single().Id;
                dailyNative.ScrollToEnd();
                Layout(root);
                if (!string.IsNullOrEmpty(qa)) Save(root, qa, "calendar-repeat-inline.png");
                var dailyRoot = (Grid)Property(daily, "Root");
                dailyRoot.Width = 145;
                Layout(root);
                Check(dailyTools.All(button => button.ActualWidth == 30 && button.ActualHeight == 30), "All six date tools keep square dimensions when wrapping");
                if (!string.IsNullOrEmpty(qa)) Save(root, qa, "calendar-repeat-inline-narrow.png");
                dailyRoot.Width = double.NaN;
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)!, "Real inline close persists a repeat draft");
                var storedRepeat = store.Load();
                Check(storedRepeat.RepeatBlocks.Count == 1 && storedRepeat.DateRepeatReferences[key].Single().Id == repeatId &&
                    CalendarApp::ScrewCalendar.RepeatDocumentService.Load(storedRepeat, key).Markdown == repeatDraft.Markdown &&
                    storedRepeat.DateMarkdown[key].Contains("当天上方", StringComparison.Ordinal) && !storedRepeat.DateMarkdown[key].Contains("重复正文", StringComparison.Ordinal),
                    "Real save persists hidden reference plus independent date text and reopens exact Markdown");
                dailyNative.ScrollToEnd();
                Layout(root);
                Check(Descendants<TextBlock>(dailyEditor).Any(label => label.Text == "周提醒"), "Saving a real date draft switches its repeat tags into a visual header");
                if (!string.IsNullOrEmpty(qa)) Save(root, qa, "calendar-repeat-reading.png");
                Descendants<CheckBox>(dailyEditor).Last().IsChecked = true;
                Check(store.Load().RepeatBlocks[repeatId].Contains("[x] repeat task", StringComparison.Ordinal) && store.Load().RepeatBlocks.Count == 1,
                    "Reading checkbox changes update shared source while preserving identity");
                var datePopup = (AppEditor)Invoke(window, "CreateMarkdownEditor", false)!;
                datePopup.LoadRepeatDocument(today, CalendarApp::ScrewCalendar.RepeatDocumentService.Load(store.Load(), key));
                var datePopupTools = ((WrapPanel)Invoke(window, "CreateMarkdownFormatTools", datePopup)!).Children.OfType<Button>().ToArray();
                Check(datePopupTools.Select(button => button.Content).SequenceEqual(dailyTools.Select(button => button.Content)) &&
                    datePopupTools.All(button => button.Width == 30 && button.Height == 30), "Date popup and inline hosts share the same repeat toolbar");
                var draftPreview = datePopup.GetRepeatDraft();
                SetField(window, "_todayMarkdownPreview", draftPreview);
                Invoke(window, "RefreshTodayMarkdownPreview");
                dailyNative.ScrollToEnd();
                Layout(root);
                Check(dailyEditor.GetRepeatDraft().Bindings.Single().Id == repeatId && Descendants<TextBlock>(dailyEditor).Any(label => label.Text == "周提醒"),
                    "Popup draft previews preserve hidden bindings and render repeat cards");
                SetField(window, "_todayMarkdownPreview", null);
                Invoke(window, "RefreshTodayMarkdownPreview");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)0);
                var closingOffset = dailyEditor.GetMarkdown().LastIndexOf("[/repeat]", StringComparison.Ordinal);
                dailyNative.Document.Remove(closingOffset, 1);
                var residual = dailyEditor.GetMarkdown();
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)! && store.Load().RepeatBlocks.Count == 0 &&
                    store.Load().DateRepeatReferences.Count == 0 && store.Load().DateMarkdown[key] == residual,
                    "Saving damaged repeat tags cleans registration and preserves all remaining text");
                loaded.DateMarkdown.Remove(key);
                Check(store.Save(loaded), "Reset isolated today text for a projection-only day");
                Invoke(window, "RenderTodoPanel");
                Check(!(bool)Field(window, "_todoLayoutHasToday"), "An empty today starts without a second column");
                var sourceDate = today.AddDays(-7);
                var sourceKey = sourceDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var futureDate = today.AddDays(7);
                var futureKey = futureDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var seriesId = Guid.NewGuid().ToString("N");
                var series = $"[repeat type=\"w\" startdate=\"{sourceKey}\" enddate=\"{today.AddDays(14):yyyy-MM-dd}\"]\n**跨日期同步正文**\n- [ ] 同步勾选\n[/repeat]";
                Check((bool)Invoke(window, "TryPersistMarkdown", sourceDate, false, new CalendarApp::ScrewCalendar.RepeatDocument(series, [new(seriesId, 0, series.Length)]), null)!, "Save an isolated recurring source on a previous date");
                Invoke(window, "Render");
                Layout(root);
                Check((bool)Field(window, "_todoLayoutHasToday") && dailyEditor.GetRepeatDraft().Bindings.Single().IsProjection && !store.Load().DateMarkdown.ContainsKey(key),
                    "A projected occurrence alone appears in today's area without a stored date document");
                var dateCells = (System.Collections.Generic.Dictionary<string, Border>)Field(window, "_dateCells");
                foreach (var dayKey in new[] { sourceKey, key, futureKey })
                    Check(dateCells.TryGetValue(dayKey, out var cell) && Descendants<TextBlock>(cell).Count(label => label.Text.Contains("跨日期同步正文", StringComparison.Ordinal)) == 1,
                        "Calendar source and matching dates each render one synchronized summary: " + dayKey);
                dailyNative.ScrollToHome();
                Layout(root);
                Descendants<CheckBox>(dailyEditor).First().IsChecked = true;
                Check(store.Load().RepeatBlocks[seriesId].Contains("[x] 同步勾选", StringComparison.Ordinal) && !store.Load().DateRepeatReferences.ContainsKey(key),
                    "Today's projected checkbox changes the shared body without pinning today");
                Check(((string)Invoke(window, "GetMarkdownDocument", futureDate, false)!).Contains("[x] 同步勾选", StringComparison.Ordinal) &&
                    Descendants<TextBlock>(dateCells[futureKey]).Any(label => label.Text.Contains("☑", StringComparison.Ordinal)), "Checkbox changes immediately refresh other date summaries");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)0);
                dailyNative.Document.Insert(0, "仅今日顶部\n");
                dailyNative.Document.Insert(dailyNative.Document.TextLength, "\n仅今日底部");
                var positioned = dailyEditor.GetMarkdown();
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)! && dailyEditor.GetMarkdown() == positioned, "Saving local text preserves the projected block's position and exact separators");
                var localOnly = store.Load().DateMarkdown[key];
                Check(localOnly.Contains("仅今日顶部", StringComparison.Ordinal) && localOnly.Contains("仅今日底部", StringComparison.Ordinal) && !localOnly.Contains("跨日期同步正文", StringComparison.Ordinal),
                    "Only date-local surrounding text is stored in DateMarkdown");
                var futureDraft = CalendarApp::ScrewCalendar.RepeatDocumentService.Load(store.Load(), futureKey);
                Check((bool)Invoke(window, "TryPersistMarkdown", futureDate, false,
                    new CalendarApp::ScrewCalendar.RepeatDocument(futureDraft.Markdown.Replace("同步正文", "更新正文", StringComparison.Ordinal), futureDraft.Bindings), null)!, "Edit shared body from another occurrence date");
                Invoke(window, "Render");
                Layout(root);
                Check(dailyEditor.GetMarkdown() == positioned.Replace("同步正文", "更新正文", StringComparison.Ordinal) && store.Load().DateMarkdown[key] == localOnly &&
                    !store.Load().DateRepeatReferences.ContainsKey(futureKey), "Shared edits refresh today's view and preserve its independent surrounding text");
                foreach (var style in Enum.GetValues<CalendarApp::ScrewCalendar.CalendarStyle>())
                {
                    loaded.Style = style;
                    foreach (var theme in Enum.GetValues<CalendarApp::ScrewCalendar.CalendarTheme>())
                    {
                        loaded.Theme = theme;
                        Invoke(window, "Render"); Layout(root);
                        Check(Descendants<TextBlock>(dateCells[futureKey]).Any(label => label.Text.Contains("跨日期更新正文", StringComparison.Ordinal)), "Projected summaries survive style and theme changes");
                        if (!string.IsNullOrEmpty(qa)) Save(root, qa, $"calendar-recurrence-{style}-{theme}.png");
                    }
                }
                var limited = CalendarApp::ScrewCalendar.RepeatDocumentService.Load(store.Load(), futureKey);
                var shortRange = limited.Markdown.Replace(sourceKey, key, StringComparison.Ordinal).Replace(today.AddDays(14).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), key, StringComparison.Ordinal);
                Check((bool)Invoke(window, "TryPersistMarkdown", futureDate, false, new CalendarApp::ScrewCalendar.RepeatDocument(shortRange, limited.Bindings), null)!, "Shorten recurrence range from a projected date");
                Invoke(window, "Render"); Layout(root);
                Check(!Descendants<TextBlock>(dateCells[sourceKey]).Any(label => label.Text.Contains("跨日期更新正文", StringComparison.Ordinal)) &&
                    !Descendants<TextBlock>(dateCells[futureKey]).Any(label => label.Text.Contains("跨日期更新正文", StringComparison.Ordinal)) && dailyEditor.GetRepeatDraft().Bindings.Count == 1,
                    "Changing date range removes out-of-range calendar occurrences and retains today's valid occurrence");
                Check(((CalendarApp::ScrewCalendar.RepeatDocument)Invoke(window, "GetRepeatDocument", sourceDate, false)!).Bindings.Count == 0 &&
                    !store.Load().DateRepeatReferences.ContainsKey(sourceKey) && store.Load().DateRepeatReferences[key].Single().Id == seriesId,
                    "Out-of-range source editor no longer loads the reminder, whose reference remains on its new start date");
                var movedEditor = (AppEditor)Invoke(window, "CreateMarkdownEditor", false)!;
                Invoke(window, "LoadMarkdownEditor", movedEditor, sourceDate, false);
                Check(movedEditor.GetRepeatDraft().Bindings.Count == 0 && !movedEditor.GetMarkdown().Contains("[repeat", StringComparison.Ordinal),
                    "Reloading the old date through the app editor path matches its browsing view after a range change");
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null)
                {
                    window.DisposeHolidayUpdates();
                    SetField(window, "_allowExit", true);
                    window.Close();
                }
                app.Shutdown();
                Directory.Delete(folder, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new InvalidOperationException("Calendar Markdown integration timed out");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static object? Invoke(object target, string method, params object?[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(target, arguments);
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(1000, 1100));
        root.Arrange(new Rect(0, 0, 1000, 1100));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void Save(FrameworkElement root, string folder, string file)
    {
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(1000, 1100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, file));
        encoder.Save(stream);
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
