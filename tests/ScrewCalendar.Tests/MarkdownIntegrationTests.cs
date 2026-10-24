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
                SetField(window, "_todayMarkdownPreview", "# 外部草稿\n\n---\n\n- [ ] 新任务");
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
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", permanent)!, "Inline save succeeds");
                Check(store.Load().PermanentMarkdown == saved && editor.GetMarkdown() == saved && editor.IsPreviewMode, "Inline save retains leading/trailing whitespace and returns to the same reader");
                Layout(root);
                var task = Descendants<CheckBox>(editor).First();
                task.IsChecked = true;
                Check(store.Load().PermanentMarkdown == saved.Replace("[ ]", "[x]", StringComparison.Ordinal), "Reading task click persists to the isolated data store");
                var liveReader = Field(window, "_todaySection");
                SetField(window, "_todayMarkdownPreview", "preview one");
                Invoke(window, "RefreshTodayMarkdownPreview");
                SetField(window, "_todayMarkdownPreview", "preview two");
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
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)!, "Whitespace-only date document saves");
                Check(store.Load().DateMarkdown[key] == " \r\n\r\n", "Whitespace-only dates also preserve exact source");
                Invoke(window, "BeginInlineMarkdownEdit", today, false, (int?)0);
                dailyNative.Select(0, dailyNative.Document.TextLength);
                dailyNative.SelectedText = "";
                Check((bool)Invoke(window, "SaveInlineMarkdownEdit", daily)!, "Empty date document saves");
                Check(!store.Load().DateMarkdown.ContainsKey(key) && !(bool)Field(window, "_todoLayoutHasToday"), "Clearing today's document restores the single-column layout");
                Check(ReferenceEquals(permanent, Field(window, "_permanentSection")), "Column changes retain the permanent reader");
                Check((bool)Invoke(window, "TryPersistMarkdown", today, false, state.DateMarkdown[key])!, "Restore the date fixture");
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
                Check(inlineTools.Length == 5 && popupTools.Length == 5, "Both toolbar hosts expose paragraph, heading, bold, bullet and task tools");
                for (var i = 0; i < inlineTools.Length; i++)
                    Check(inlineTools[i].Width == 30 && inlineTools[i].Height == 30 && inlineTools[i].ActualWidth == inlineTools[i].ActualHeight &&
                        inlineTools[i].Padding == popupTools[i].Padding && inlineTools[i].Margin == popupTools[i].Margin &&
                        ReferenceEquals(inlineTools[i].Template, popupTools[i].Template), "Inline tools use the same fixed square dimensions and template as popup tools");
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
