using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace ScrewCalendar.Tests;

internal static class MarkdownTests
{
    internal const string Sample = "# Markdown 阅读与源码\n\n普通文本，**加粗**、__粗体__、*斜体*、~~删除线~~、`inline code`。\n\n---\n\n## 待办清单\n- [ ] 尚未完成\n- [x] 已经完成\n  - 嵌套列表\n\n1. 第一项\n2. 第二项\n\n> 引用内容 **保留格式**\n> 第二行引用\n\n```text\n---\n- [ ] 这里是代码，不是任务\n**这里也不是加粗**\n```\n\n[示例链接](https://example.com) 与 \\*普通星号\\* &amp; 实体\n\n![示例图片](assets/test.png)\n\n下划线标题\n---\n\n###### 六级标题\n";

    public static void SemanticBoundaries()
    {
        const string source = "\r\n---\r\n\r\n标题\r\n---\r\n\r\n```md\r\n---\r\n- [ ] fake\r\n```\r\n\r\n`- [ ] inline`\r\n\r\n> - [ ] real\r\n  - [x] nested\r\n\r\n[ ] plain\r\n";
        var snapshot = MarkdownDocumentService.Parse(source);
        Check(snapshot.Lines[1].Replacements.Any(item => item.Kind == MarkdownReplacementKind.Rule), "Standalone thematic break is a rule");
        Check(snapshot.Lines[3].HeadingLevel == 0 && snapshot.Lines[4].Replacements.Any(item => item.Kind == MarkdownReplacementKind.Rule), "Dashes immediately after text remain a visible separator");
        Check(snapshot.Lines[7].IsCode && snapshot.Lines[7].Replacements.Count == 0, "A rule inside a code fence stays literal");
        Check(snapshot.Tasks.Count == 2, "Only parsed task list items are interactive");
        var expected = source.Remove(snapshot.Tasks[0].MarkerOffset, 1).Insert(snapshot.Tasks[0].MarkerOffset, "x");
        Check(MarkdownDocumentService.ToggleTask(source, 0, true) == expected, "Task toggle changes exactly one source character including CRLF preservation");
        Check(MarkdownDocumentService.ToggleTask(source, -1, true) == source, "Invalid task index is harmless");
        Check(MarkdownDocumentService.ToggleTask(source, 9, true) == source, "Out-of-range task index is harmless");
        Check(snapshot.Source == source, "Parsing preserves leading/trailing whitespace and line delimiters");
        foreach (var separator in new[] { "---", "before\n---", "before\n\n---\nafter", "before\r\n---\r\nafter", "first\nsecond\n---", "> quoted\n> ---", "- item\n  ---" })
        {
            var parsed = MarkdownDocumentService.Parse(separator);
            Check(parsed.Lines.Any(line => line.Replacements.Any(item => item.Kind == MarkdownReplacementKind.Rule)), "Dash separator is visible regardless of preceding content: " + separator);
            Check(parsed.Lines.All(line => line.HeadingLevel == 0) && parsed.Source == separator, "Separator interpretation preserves text size and exact source");
        }
        Check(MarkdownDocumentService.Parse("title\n===").Lines[0].HeadingLevel == 1, "Equals-style headings retain their existing behavior");
        Check(MarkdownDocumentService.Parse("title\n--").Lines[0].HeadingLevel == 2, "Short Setext underlines retain their existing behavior");
        Check(ReferenceEquals(snapshot, MarkdownDocumentService.Parse(source)), "Repeated views share cached semantic results");
        foreach (var heading in Enumerable.Range(1, 6))
            Check(MarkdownDocumentService.Parse(new string('#', heading) + " 标题").Lines[0].HeadingLevel == heading, "All six ATX heading levels parse");
        const string inline = "**粗体** __也加粗__ *斜体* ~~删除~~ [链接](https://example.com) \\*普通\\* &amp; ` code `";
        var plain = MarkdownDocumentService.Parse(inline).PlainText(0, inline.Length);
        Check(plain == "粗体 也加粗 斜体 删除 链接 *普通* & code", "Reading text decodes escapes/entities and hides only recognized syntax: " + plain);
        Check(MarkdownDocumentService.Parse("![alt](assets/a.png)").SummaryLines().Single() == "[alt]", "Compact images show alt text instead of storage paths");
        Check(MarkdownDocumentService.Parse("<unrecognized>text</unrecognized>").PlainText(0, 34).Contains("<unrecognized>", StringComparison.Ordinal), "Disabled HTML stays literal");
        Check(MarkdownDocumentService.Parse("**unfinished").PlainText(0, 12) == "**unfinished", "Unfinished syntax stays editable and visible");
        foreach (var line in MarkdownDocumentService.Parse(Sample).Lines)
        {
            var end = line.Offset;
            foreach (var replacement in line.Replacements)
            {
                Check(replacement.Start >= end && replacement.Start + replacement.Length <= line.Offset + line.Length, "Reading replacements never overlap or consume line breaks");
                end = replacement.Start + replacement.Length;
            }
        }
    }

    public static void ReadingModes() => InSta(() =>
    {
        var editor = Create();
        var native = (TextEditor)editor.Child!;
        const string original = "\n# 标题\r\n\r\n---\r\n\n";
        editor.SetMarkdown(original);
        var document = native.Document;
        native.CaretOffset = native.Document.TextLength;
        native.Document.Insert(native.CaretOffset, "输入内容");
        native.Select(3, 2);
        var changes = 0;
        editor.ContentChanged += (_, _) => changes++;
        for (var i = 0; i < 5; i++) { editor.SetPreviewMode(true); editor.SetPreviewMode(false); }
        Check(ReferenceEquals(document, native.Document), "Mode switches retain the document instance");
        Check(changes == 0 && editor.GetMarkdown() == original + "输入内容", "Switching modes does not rewrite or publish text");
        Check(native.SelectionStart == 3 && native.SelectionLength == 2, "Source selection survives a reading round trip");
        native.Undo();
        Check(editor.GetMarkdown() == original, "Undo survives reading mode switches");
        native.Redo();
        Check(editor.GetMarkdown() == original + "输入内容", "Redo survives reading mode switches");
        editor.SetPreviewMode(true);
        editor.UpdateMarkdown("**new**\r\n\r\n");
        Check(ReferenceEquals(document, native.Document) && editor.GetMarkdown() == "**new**\r\n\r\n", "Live preview updates preserve the editor and exact source");
        editor.SetPreviewMode(false);
        editor.DiscardDraft("saved");
        Check(!native.Document.UndoStack.CanUndo && editor.GetMarkdown() == "saved", "Discarded drafts cannot return through undo");
    });

    public static void LiveEditing() => InSta(() =>
    {
        const string source = "# 标题 **粗体**\r\n正文 **加粗** 与 `code`\r\n- [ ] **任务**\r\n---\r\n```md\r\n- [ ] code\r\n```\r\n![alt](assets/test.png)\r\n最后一行";
        foreach (var dark in new[] { false, true })
        {
            var editor = Create(dark);
            var native = (TextEditor)editor.Child!;
            var view = native.TextArea.TextView;
            using var host = new HwndSource(new HwndSourceParameters("MarkdownLiveEditing") { Width = 420, Height = 700 });
            var root = new Border { Background = dark ? Brushes.Black : Brushes.White, Padding = new Thickness(12), Child = editor };
            host.RootVisual = root;
            editor.SetMarkdown(source);
            var document = native.Document;
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;
            void Render() => Layout(root, native, 420, 700);
            ICSharpCode.AvalonEdit.Rendering.VisualLine Line(int number) => view.VisualLines.Single(line => line.FirstDocumentLine.LineNumber == number);
            bool HasHidden(int number) => Line(number).Elements.Any(element => element.GetType().Name == "HiddenSource");
            void CheckLiteral(int number)
            {
                var line = Line(number);
                Check(line.Elements.All(element => element is not ICSharpCode.AvalonEdit.Rendering.InlineObjectElement && element.GetType().Name != "HiddenSource"), "Revealed lines contain actual editable text");
                for (var offset = 0; offset <= line.FirstDocumentLine.Length; offset++)
                    Check(line.GetRelativeOffset(line.GetVisualColumn(offset)) == offset, "Every source character remains addressable by the caret");
            }
            Render();
            Check(!editor.IsSourceMode && !editor.IsPreviewMode, "Editing defaults to live presentation");
            CheckLiteral(1);
            Check(HasHidden(2) && Visuals<CheckBox>(editor).Count() == 1, "Other text and task lines retain Markdown presentation");
            Check(Line(1).Elements.Any(element => element.TextRunProperties.FontRenderingEmSize == 24), "Revealing a heading retains its heading size");
            var headingHeight = Line(1).Height;
            CheckLiteral(5); CheckLiteral(6); CheckLiteral(7); CheckLiteral(8);
            Check(!Visuals<Image>(editor).Any(), "Images and code blocks remain source while editing in this step");
            Visuals<CheckBox>(editor).Single().IsChecked = true;
            Check(editor.GetMarkdown() == source, "Rendered task controls do not mutate an editing draft");
            native.CaretOffset = document.GetLineByNumber(3).Offset + 3;
            Render();
            CheckLiteral(3);
            Check(HasHidden(1) && !Visuals<CheckBox>(editor).Any(), "Moving the caret renders the old line and reveals the task markers");
            Check(Math.Abs(headingHeight - Line(1).Height) < .1, "Heading height stays stable after leaving the line");
            if (Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR") is { Length: > 0 } qa)
                SaveImage(root, qa, dark ? "live-task-dark.png" : "live-task-light.png", 420, 700);
            native.CaretOffset = document.GetLineByNumber(4).Offset + 1;
            Render();
            CheckLiteral(4);
            Check(Line(3).Elements.Any(element => element is ICSharpCode.AvalonEdit.Rendering.InlineObjectElement), "Leaving a task restores its checkbox");
            native.Select(2, document.GetLineByNumber(3).EndOffset - 2);
            Render();
            CheckLiteral(1); CheckLiteral(2); CheckLiteral(3);
            var selection = (native.SelectionStart, native.SelectionLength);
            editor.SetSourceMode(true); Render();
            foreach (var line in document.Lines) CheckLiteral(line.LineNumber);
            editor.SetSourceMode(false); Render();
            editor.SetPreviewMode(true); Render();
            Check(Visuals<Image>(editor).Any() && Visuals<CheckBox>(editor).Count() == 1, "Reading still renders complex blocks and tasks");
            editor.SetPreviewMode(false); Render();
            Check((native.SelectionStart, native.SelectionLength) == selection && changes == 0 && editor.GetMarkdown() == source,
                "Revealing, source fallback and reading preserve the selection and never publish text changes");
            native.Select(0, 0);
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            native.CaretOffset = document.GetLineByNumber(3).Offset + 2;
            Render();
            Check(Line(3).Elements.Any(element => element is ICSharpCode.AvalonEdit.Rendering.InlineObjectElement), "Mouse selection keeps the previous geometry until release");
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Handled = true });
            Render(); CheckLiteral(3);
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            native.CaretOffset = document.GetLineByNumber(4).Offset + 1;
            Render();
            native.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            Render(); CheckLiteral(4);
            native.CaretOffset = document.GetLineByNumber(3).Offset + 2;
            Render(); CheckLiteral(3);
            native.Document.Insert(native.CaretOffset, "输入");
            Render(); CheckLiteral(3);
            native.Undo(); Render();
            Check(ReferenceEquals(document, native.Document) && editor.GetMarkdown() == source, "Live editing retains native undo and the same document");
            native.Select(0, document.TextLength);
            var originalClipboard = ClipboardTestAccess.Run(Clipboard.GetDataObject);
            try { native.Copy(); Check(ClipboardTestAccess.Run(Clipboard.GetText) == source, "Live editing copies exact Markdown source"); }
            finally
            {
                if (originalClipboard is not null) ClipboardTestAccess.Run(() => Clipboard.SetDataObject(originalClipboard));
            }
            native.SelectedText = "replacement\n---";
            Render();
            Check(editor.GetMarkdown() == "replacement\n---", "Select all and replacement work across rendered blocks");
            native.Undo(); Render();
            Check(editor.GetMarkdown() == source, "Replacing a document remains one native undo step");
            editor.SetMarkdown(""); Render();
            Check(editor.GetMarkdown() == "", "Empty live documents remain editable");
        }
    });

    public static void ListContinuation() => InSta(() =>
    {
        var editor = Create();
        var native = (TextEditor)editor.Child!;
        foreach (var example in new[]
        {
            ("- item", "- item\r\n- "),
            ("  - [x] done", "  - [x] done\r\n  - [ ] "),
            ("> 3. item", "> 3. item\r\n> 4. "),
            ("- [ ] ", ""),
            ("```\n- item", "```\n- item")
        })
        {
            editor.SetMarkdown(example.Item1);
            native.CaretOffset = native.Document.TextLength;
            editor.ContinueListOnEnter();
            Check(editor.GetMarkdown() == example.Item2, "Context-aware list continuation: " + example.Item1);
        }
        editor.SetMarkdown("- first\n- second");
        native.CaretOffset = "- first".Length;
        Check(editor.ContinueListOnEnter() && editor.GetMarkdown() == "- first\n- \n- second", "List continuation retains LF delimiters");
        native.Undo();
        Check(editor.GetMarkdown() == "- first\n- second", "List continuation is one undo operation");
    });

    public static void BoldFormatting() => InSta(() =>
    {
        var editor = Create();
        var native = (TextEditor)editor.Child!;
        editor.SetMarkdown("before 文字 after");
        native.Select(7, 2);
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "before **文字** after", "Bold tool wraps the selected text");
        native.Undo();
        Check(editor.GetMarkdown() == "before 文字 after", "Applying bold is one undo step");
        native.Redo();
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "before 文字 after", "Repeating bold removes the selected delimiters");
        editor.SetMarkdown("**文字**");
        native.Select(2, 2);
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "文字", "Selecting only bold content also removes the surrounding markers");
        editor.SetMarkdown("");
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "****" && native.CaretOffset == 2, "An empty selection puts the caret between bold markers");
        native.SelectedText = "new";
        Check(editor.GetMarkdown() == "**new**", "Typing into the empty bold markers creates valid Markdown");
        editor.SetMarkdown("  first  \r\n\r\n\tsecond\n");
        native.Select(0, native.Document.TextLength);
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "  **first**  \r\n\r\n\t**second**\n", "Multiline bold retains blank lines, indentation and delimiters");
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "  first  \r\n\r\n\tsecond\n", "Multiline bold can be toggled off without damaging markers");
        editor.SetMarkdown("**bold**");
        editor.SetPreviewMode(true);
        editor.ToggleBold();
        Check(editor.GetMarkdown() == "**bold**", "The bold command does not mutate a read-only preview");
        using var host = new HwndSource(new HwndSourceParameters("MarkdownBoldRegression") { Width = 320, Height = 120 });
        host.RootVisual = editor;
        Layout(editor, native, 320, 120);
        Check(native.TextArea.TextView.VisualLines.SelectMany(line => line.Elements).Any(element => element.TextRunProperties.Typeface.Weight == FontWeights.Bold), "Reading displays strong emphasis with actual bold font weight");
    });

    public static void TaskAlignment() => InSta(() =>
    {
        const string source = "- [ ] 普通任务 English\n- [x] 已完成任务 Completed\n- [ ] **加粗任务 Bold**\n  - [ ] 多行中文任务 mixed English text that wraps onto a second and third line in a narrow reader\n普通正文";
        foreach (var dark in new[] { false, true })
        {
            var editor = Create(dark);
            var native = (TextEditor)editor.Child!;
            using var host = new HwndSource(new HwndSourceParameters("MarkdownTaskAlignment") { Width = 380, Height = 400 });
            var root = new Border
            {
                Background = dark ? new SolidColorBrush(Color.FromRgb(30, 34, 40)) : Brushes.White,
                Padding = new Thickness(16),
                Child = editor
            };
            host.RootVisual = root;
            editor.SetMarkdown(source);
            editor.SetPreviewMode(true);
            foreach (var fontSize in new[] { 14d, 18d, 22d })
            {
                native.FontSize = fontSize;
                native.TextArea.TextView.Redraw();
                Layout(root, native, 320, 380);
                var boxes = Visuals<CheckBox>(editor).ToArray();
                Check(boxes.Length == 4, "Every task has a checkbox in the alignment fixture");
                var taskLines = native.TextArea.TextView.VisualLines.Where(line => editor.Snapshot.Lines[line.FirstDocumentLine.LineNumber - 1].ListMarker?.IsTask == true).ToArray();
                Check(taskLines[^1].TextLines.Count > 1, "The alignment fixture includes a wrapped task");
                Check(taskLines[0].Height >= native.TextArea.TextView.DefaultLineHeight + 4 - .1,
                    "Task rows retain two pixels of spacing above and below the content");
                for (var i = 0; i < boxes.Length; i++)
                {
                    var visual = taskLines[i];
                    var baseline = visual.VisualTop - native.VerticalOffset + visual.TextLines[0].Baseline;
                    var textCenter = baseline - native.TextArea.TextView.DefaultBaseline + native.TextArea.TextView.DefaultLineHeight / 2;
                    var checkboxCenter = boxes[i].TranslatePoint(new Point(0, boxes[i].ActualHeight / 2), native.TextArea.TextView).Y;
                    Check(Math.Abs(checkboxCenter - textCenter) <= 1,
                        $"Task checkbox centers on its first text line at {fontSize}px: checkbox={checkboxCenter:F2}, text={textCenter:F2}, boxBaseline={TextBlock.GetBaselineOffset(boxes[i]):F2}, defaultBaseline={native.TextArea.TextView.DefaultBaseline:F2}, defaultHeight={native.TextArea.TextView.DefaultLineHeight:F2}, lineBaseline={visual.TextLines[0].Baseline:F2}");
                }
                Check(editor.GetMarkdown() == source, "Task baseline changes never alter Markdown source");
                if (fontSize == 14 && Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR") is { Length: > 0 } qa)
                    SaveImage(root, qa, dark ? "tasks-aligned-dark.png" : "tasks-aligned-light.png", 320, 380);
            }
        }
    });

    public static void ReadingLayout() => InSta(() =>
    {
        foreach (var dark in new[] { false, true })
        {
            var editor = Create(dark);
            var native = (TextEditor)editor.Child!;
            using var host = new HwndSource(new HwndSourceParameters("MarkdownReadingRegression") { Width = 720, Height = 1500 });
            var background = new Border { Background = dark ? new SolidColorBrush(Color.FromRgb(30, 34, 40)) : Brushes.White, Padding = new Thickness(20), Child = editor };
            host.RootVisual = background;
            editor.SetMarkdown(Sample);
            editor.SetPreviewMode(true);
            Layout(background, native, 700, 1400);
            Check(Visuals<CheckBox>(editor).Count() == 2, "Code fence task syntax never produces a checkbox");
            Check(Visuals<Image>(editor).Any(), "Reading renders local images");
            var document = native.Document;
            var checkbox = Visuals<CheckBox>(editor).First();
            checkbox.IsChecked = true;
            Check(editor.GetMarkdown().Contains("- [x] 尚未完成", StringComparison.Ordinal), "Native checkbox writes back its exact source task");
            Check(!editor.GetMarkdown().Contains("- [x] 这里是代码", StringComparison.Ordinal), "Native checkbox does not touch code blocks");
            var before = editor.GetMarkdown();
            var originalClipboard = ClipboardTestAccess.Run(Clipboard.GetDataObject);
            try
            {
                native.Select(0, native.Document.TextLength);
                native.Copy();
                var copied = ClipboardTestAccess.Run(Clipboard.GetText);
                Check(copied.Contains("Markdown 阅读与源码", StringComparison.Ordinal) && !copied.Contains("# Markdown", StringComparison.Ordinal), "Reading copy exports displayed text");
                Check(!((string?)ClipboardTestAccess.Run(() => Clipboard.GetData(DataFormats.Html)) ?? "").Contains("# Markdown", StringComparison.Ordinal), "Rich-text paste also receives the reading text instead of raw Markdown");
            }
            finally { if (originalClipboard is not null) ClipboardTestAccess.Run(() => Clipboard.SetDataObject(originalClipboard)); }
            var qa = Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR");
            if (!string.IsNullOrEmpty(qa)) SaveImage(background, qa, dark ? "reading-dark.png" : "reading-light.png", 700, 1400);
            editor.SetPreviewMode(false);
            editor.SetSourceMode(true);
            Layout(background, native, 700, 1400);
            Check(!Visuals<Image>(editor).Any() && !Visuals<CheckBox>(editor).Any(), "Source mode exposes all syntax including images and tasks");
            if (!string.IsNullOrEmpty(qa)) SaveImage(background, qa, dark ? "source-dark.png" : "source-light.png", 700, 1400);
            for (var i = 0; i < 4; i++)
            {
                editor.SetPreviewMode(true);
                Layout(background, native, 280 + i * 90, 1400);
                editor.SetPreviewMode(false);
                Layout(background, native, 280 + i * 90, 1400);
            }
            Check(editor.GetMarkdown() == before && ReferenceEquals(document, native.Document), "Resizing and repeated switches preserve all source");
            editor.SetPreviewMode(true);
            Layout(background, native, 280, 1400);
            if (!string.IsNullOrEmpty(qa)) SaveImage(background, qa, dark ? "narrow-dark.png" : "narrow-light.png", 280, 1400);
            editor.SetMarkdown("Long escaped paragraph: \\*" + string.Concat(Enumerable.Repeat("中文换行 test ", 30)) + "\\*");
            Layout(background, native, 280, 1400);
            Check(native.TextArea.TextView.VisualLines[0].TextLines.Count > 3, "Escaped long paragraphs still wrap in narrow readers");
            editor.SetMarkdown("Click to edit");
            native.Select(0, 0);
            Layout(background, native, 280, 1400);
            var clicks = 0;
            editor.EditRequested += _ => clicks++;
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            native.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Handled = true });
            Check(clicks == 1, "Reading clicks reach the host even when AvalonEdit consumes mouse-up");
        }
    });

    private static MarkdownBlockEditor Create(bool dark = false)
    {
        var bitmap = BitmapSource.Create(240, 80, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Range(0, 240 * 80).SelectMany(index => new byte[] { 190, (byte)(110 + index % 80), 45, 255 }).ToArray(), 240 * 4);
        bitmap.Freeze();
        return new MarkdownBlockEditor(dark ? Brushes.WhiteSmoke : Brushes.Black, Brushes.Gray,
            dark ? new SolidColorBrush(Color.FromRgb(42, 47, 54)) : new SolidColorBrush(Color.FromRgb(243, 246, 249)),
            dark ? Brushes.DimGray : Brushes.LightGray, Brushes.DodgerBlue, new FontFamily("Segoe UI"), _ => bitmap);
    }

    public static void LinkHitTesting() => InSta(() =>
    {
        const string url = "https://act.steamchina.com/2026showcase";
        const string link = "[2026 鉴赏家新品节](https://act.steamchina.com/2026showcase)";
        var editor = Create();
        var native = (TextEditor)editor.Child!;
        var view = native.TextArea.TextView;
        using var host = new HwndSource(new HwndSourceParameters("MarkdownLinkHitTesting") { Width = 520, Height = 340 });
        host.RootVisual = editor;
        foreach (var source in new[]
        {
            link, link + " ", link + " 正文", "正文：" + link, "# " + link, "- [ ] " + link,
            "[2026 **鉴赏家**新品节](" + url + ")", "[2026 鉴赏家新品节](" + url + " \"节日\")",
            "[2026 鉴赏家新品节][event]\n\n[event]: " + url,
            "[另一个链接](https://example.com) " + link,
            "<" + url + ">",
            "[2026 鉴赏家新品节 " + string.Concat(Enumerable.Repeat("中文与 English wrapped link text ", 5)) + "结束](" + url + ")"
        })
        {
            editor.SetMarkdown(source);
            editor.SetPreviewMode(true);
            foreach (var width in new[] { 520, 220 })
            {
                Layout(editor, native, width, 340);
                var visual = view.VisualLines[0];
                // Sample real glyphs, including the right half of the last character;
                // the ordinary caret hit test rounds that half to the next position.
                var offsets = source.Contains('鉴') ? new[] { source.IndexOf("2026", StringComparison.Ordinal), source.IndexOf('鉴'), source.IndexOf('节') } : new[] { 1, source.IndexOf(')') < 0 ? source.Length - 2 : 2 };
                foreach (var offset in offsets)
                {
                    var column = visual.GetVisualColumn(offset);
                    var textLine = visual.GetTextLine(column);
                    var bounds = textLine.GetTextBounds(column, 1).First(bound => bound.Rectangle.Width > 0).Rectangle;
                    var y = visual.GetTextLineVisualYPosition(textLine, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset.Y;
                    foreach (var fraction in new[] { .1, .5, .9 })
                        Check(editor.HitTestLink(view.TranslatePoint(new Point(bounds.Left + bounds.Width * fraction, y), native))?.Url == url,
                            "Visible link text is clickable across its entire glyph: " + source);
                }
                foreach (var textLine in visual.TextLines)
                {
                    var y = visual.GetTextLineVisualYPosition(textLine, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset.Y;
                    var right = textLine.WidthIncludingTrailingWhitespace + 2;
                    if (right < view.ActualWidth)
                        Check(editor.HitTestLink(view.TranslatePoint(new Point(right, y), native)) is null, "Blank space to the right of each wrapped text row is not a link: " + source);
                }
                Check(editor.HitTestLink(view.TranslatePoint(new Point(20, visual.Height + 8), native)) is null, "Blank space below the link is not clickable");
                Check(editor.HitTestLink(new Point(0, 0)) is null, "Editor padding is not a link");
                Check(editor.GetMarkdown() == source, "Link hit testing never rewrites the source");
            }
        }
        editor.SetMarkdown(string.Concat(Enumerable.Repeat("普通正文\n", 20)) + link);
        Layout(editor, native, 520, 140);
        native.ScrollToEnd();
        Layout(editor, native, 520, 140);
        var scrolled = view.VisualLines.Last();
        var lastColumn = scrolled.GetVisualColumn(link.IndexOf('节'));
        var lastLine = scrolled.GetTextLine(lastColumn);
        var lastBounds = lastLine.GetTextBounds(lastColumn, 1).Single().Rectangle;
        var lastY = scrolled.GetTextLineVisualYPosition(lastLine, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset.Y;
        Check(editor.HitTestLink(view.TranslatePoint(new Point(lastBounds.Left + lastBounds.Width / 2, lastY), native))?.Url == url, "Scrolled links use viewport coordinates correctly");
        Check(editor.HitTestLink(view.TranslatePoint(new Point(view.ActualWidth - 8, lastY), native)) is null, "Scrolled row whitespace is not clickable");
        editor.SetPreviewMode(false);
        Check(editor.HitTestLink(view.TranslatePoint(new Point(lastBounds.Left + lastBounds.Width / 2, lastY), native)) is null, "Editing keeps link text editable instead of opening a URL");
    });

    private static void Layout(FrameworkElement root, TextEditor native, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        native.TextArea.TextView.EnsureVisualLines();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void SaveImage(FrameworkElement root, string folder, string name, int width, int height)
    {
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private static System.Collections.Generic.IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    private static void InSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Markdown WPF regression timed out");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
