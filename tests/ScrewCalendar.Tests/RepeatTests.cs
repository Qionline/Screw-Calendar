using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;

namespace ScrewCalendar.Tests;

internal static class RepeatTests
{
    private const string Key = "2026-01-23";
    private const string Header = "[repeat type=\"w\" startdate=\"2026-01-23\" enddate=\"2026-02-19\"]";
    private const string Block = Header + "\n**共享正文**\n- [ ] shared\n[/repeat]";
    private static readonly DateTime Date = new(2026, 1, 23);

    public static void Parsing()
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var source = ("上方\n" + Block + "\n下方").Replace("\n", newline, StringComparison.Ordinal);
            var block = RepeatBlockParser.Parse(source).Single();
            Check(block.IsValid && block.Type == "w" && block.StartDate == new DateOnly(2026, 1, 23) &&
                block.EndDate == new DateOnly(2026, 2, 19), "Weekly rule and inclusive default interval parse");
            Check(source.Substring(block.BodyStart, block.BodyLength) == "**共享正文**" + newline + "- [ ] shared", "Only body text is selected, preserving line endings");
            Check(source.Substring(block.Start, block.Length) == Block.Replace("\n", newline, StringComparison.Ordinal), "Exact source span excludes local surrounding lines");
        }
        foreach (var type in new[] { "d", "w", "m" })
            Check(RepeatBlockParser.Parse(Block.Replace("type=\"w\"", $"type=\"{type}\"", StringComparison.Ordinal)).Single().IsValid, "All three repeat types parse");
        foreach (var end in new[] { "", " enddate=\"\"" })
            Check(RepeatBlockParser.Parse(Block.Replace(" enddate=\"2026-02-19\"", end, StringComparison.Ordinal)).Single() is { IsValid: true, EndDate: null }, "Absent or empty enddate means forever");
        foreach (var bad in new[]
        {
            Block.Replace("type=\"w\"", "type=\"x\"", StringComparison.Ordinal),
            Block.Replace("2026-01-23", "2026-02-30", StringComparison.Ordinal),
            Block.Replace("2026-02-19", "2026-01-22", StringComparison.Ordinal),
            Block.Replace("2026-02-19\"", "2026-02-19", StringComparison.Ordinal),
            Block.Replace("type=\"w\"", "type=\"w\" type=\"d\"", StringComparison.Ordinal)
        }) Check(!RepeatBlockParser.Parse(bad).Single().IsValid, "Invalid attributes suspend the rule without losing the block");
        Check(RepeatBlockParser.Parse(Block.Replace("[/repeat]", "[/repea", StringComparison.Ordinal)).Count == 0, "Half-deleted tag is structurally invalid");
        Check(RepeatBlockParser.Parse("```md\n" + Block + "\n```\n\n" + string.Join("\n", Block.Split('\n').Select(line => "    " + line))).Count == 0, "Fenced and indented code never registers");
        var recover = RepeatBlockParser.Parse("[repeat type=\"w\"\nbroken\n" + Block).Single();
        Check(recover.Start > 0, "Incomplete opening cannot swallow the next complete block");
        Check(RepeatBlockParser.Parse(Header + "\n" + Block + "\n[/repeat]").All(block => block.Start > 0), "Nested tags invalidate the enclosing block");
        Check(!RepeatBlockParser.CanInsert(Block, Header.Length + 1, 0) && !RepeatBlockParser.CanInsert("```\ncode\n```", 5, 0), "Button rejects nested and code insertion");
    }

    public static void Storage()
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new CalendarState();
        var source = "上方\r\n\n" + Block + "\n\r\n下方\n";
        var offset = source.IndexOf(Header, StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Key, new(source, [new(id, offset, Block.Length)]));
        Check(state.DateMarkdown[Key] == source.Remove(offset, Block.Length) && state.RepeatBlocks[id] == Block, "Local text and shared source are stored separately");
        Check(RepeatDocumentService.Load(state, Key).Markdown == source && CalendarDataStore.Validate(state), "Composed source preserves all whitespace exactly");
        var imported = CalendarDataStore.Deserialize(CalendarDataStore.Serialize(state))!;
        Check(CalendarDataStore.Validate(imported) && RepeatDocumentService.Load(imported, Key).Bindings.Single().Id == id, "JSON round-trip retains hidden identity");
        RepeatDocumentService.Apply(state, "2026-01-30", new("独立上方\n" + Block + "\n独立下方", [new(id, 5, Block.Length)]));
        var updated = Block.Replace("共享正文", "修改正文", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Key, new(source.Replace(Block, updated, StringComparison.Ordinal), [new(id, offset, updated.Length)]));
        Check(RepeatDocumentService.Load(state, "2026-01-30").Markdown == "独立上方\n" + updated + "\n独立下方", "Shared body changes keep each date's outside text independent");
        var inactive = updated.Replace("type=\"w\"", "type=\"invalid\"", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Key, new("上\n" + inactive + "\n下", [new(id, 2, inactive.Length)]));
        Check(state.RepeatBlocks.ContainsKey(id) && CalendarDataStore.Validate(state), "Semantically invalid rule keeps its identity in persisted data");
        var broken = inactive.Replace("[/repeat]", "[/repeat", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Key, new(broken, [new(id, 0, broken.Length)]));
        Check(state.RepeatBlocks.Count == 0 && !state.DateRepeatReferences.ContainsKey(Key) &&
            !state.DateRepeatReferences.ContainsKey("2026-01-30") && state.DateMarkdown[Key] == broken,
            "Structural damage stops the series, clears all references and preserves residual text");
        RepeatDocumentService.Apply(state, Key, new(Block, []));
        Check(state.RepeatBlocks.Count == 0 && state.DateMarkdown[Key] == Block, "Handwritten tags remain ordinary local text");
        var old = CalendarDataStore.Deserialize("{\"Version\":3,\"DateMarkdown\":{\"2026-01-23\":\"old text\"}}")!;
        Check(CalendarDataStore.Validate(old) && old.RepeatBlocks.Count == 0 && RepeatDocumentService.Load(old, Key).Markdown == "old text", "Existing files without repeat fields load unchanged");
        imported.DateRepeatReferences[Key][0] = new(id, 999999);
        Check(!CalendarDataStore.Validate(imported), "Import rejects out-of-range references");
    }

    public static void AtomicSave()
    {
        var id = Guid.NewGuid().ToString("N");
        var state = new CalendarState();
        RepeatDocumentService.Apply(state, Key, new(Block, [new(id, 0, Block.Length)]));
        var before = CalendarDataStore.Serialize(state);
        var badPath = Path.Combine(Path.GetTempPath(), "ScrewRepeatSave-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(badPath, "This file prevents creation of the data directory.");
        try
        {
            var store = new CalendarDataStore(badPath, (_, _) => { });
            Check(!RepeatDocumentService.TrySave(state, Key, new("broken remaining text", []), store.Save), "Real file-system save failure is reported");
            Check(CalendarDataStore.Serialize(state) == before, "Failed save restores text, shared bodies and placements together");
        }
        finally { File.Delete(badPath); }
    }

    public static void EditorLifecycle() => InSta(() =>
    {
        Localization.SetLanguage(CalendarLanguage.ChineseSimplified);
        var editor = new MarkdownBlockEditor(Brushes.Black, Brushes.Gray, Brushes.White, Brushes.Silver, Brushes.Blue, new FontFamily("Segoe UI"), _ => null);
        var native = (TextEditor)editor.Child!;
        Check(!editor.InsertRepeat(), "Permanent/unconfigured editor never registers repeat blocks");
        editor.LoadRepeatDocument(Date, new("local before\nlocal after", []));
        native.Select("local before".Length, 0);
        var registeredAtChange = 0;
        editor.ContentChanged += (_, _) => registeredAtChange = editor.GetRepeatDraft().Bindings.Count;
        Check(editor.InsertRepeat(), "Date editor inserts from the toolbar action");
        var inserted = editor.GetRepeatDraft();
        var id = inserted.Bindings.Single().Id;
        var parsed = RepeatBlockParser.Parse(inserted.Markdown).Single();
        Check(parsed.IsValid && parsed.StartDate == new DateOnly(2026, 1, 23) && parsed.EndDate == new DateOnly(2026, 2, 19), "Default dates follow the edited date and span four weeks inclusive");
        Check(native.SelectionStart == parsed.BodyStart && native.SelectedText == Localization.T("markdown.repeatBody") && registeredAtChange == 1, "Insertion selects only the explanatory body and publishes a complete draft");
        native.Undo();
        Check(editor.GetMarkdown() == "local before\nlocal after" && editor.GetRepeatDraft().Bindings.Count == 0, "One native undo removes text and registration");
        native.Redo();
        Check(editor.GetMarkdown() == inserted.Markdown && editor.GetRepeatDraft().Bindings.Single().Id == id, "Redo restores the same hidden identity");
        parsed = RepeatBlockParser.Parse(editor.GetMarkdown()).Single();
        native.Select(parsed.BodyStart, parsed.BodyLength);
        native.SelectedText = "**正文**\n- [ ] checkbox\n[链接](https://example.com)";
        native.Document.Insert(0, "仅本日上方\n");
        native.Document.Insert(native.Document.TextLength, "\n仅本日下方");
        var edited = editor.GetRepeatDraft();
        var state = new CalendarState();
        RepeatDocumentService.Apply(state, Key, edited);
        Check(state.RepeatBlocks[id].Contains("**正文**", StringComparison.Ordinal) && state.DateMarkdown[Key].Contains("仅本日下方", StringComparison.Ordinal) &&
            !state.RepeatBlocks[id].Contains("仅本日", StringComparison.Ordinal), "Multiline replacement tracks identity while surrounding additions stay local");
        native.Undo(); native.Redo();
        Check(editor.GetRepeatDraft().Bindings.SequenceEqual(edited.Bindings), "Outside-text undo/redo restores exact ranges");
        var closing = editor.GetMarkdown().IndexOf("[/repeat]", StringComparison.Ordinal) + "[/repeat]".Length - 1;
        native.Document.Remove(closing, 1);
        native.Document.Insert(closing, "]");
        RepeatDocumentService.Apply(state, Key, editor.GetRepeatDraft());
        Check(state.RepeatBlocks.ContainsKey(id), "Repairing a half-deleted closing tag before saving retains identity");
        var opening = editor.GetRepeatDraft().Bindings.Single().Start;
        native.Document.Remove(opening, 1);
        native.Document.Insert(opening, "[");
        RepeatDocumentService.Apply(state, Key, editor.GetRepeatDraft());
        Check(state.RepeatBlocks.ContainsKey(id), "Repairing the first opening character before saving retains identity");
        native.Select(opening, Header.Length);
        native.SelectedText = Header.Replace("type=\"w\"", "type=\"m\"", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Key, editor.GetRepeatDraft());
        Check(state.RepeatBlocks[id].StartsWith("[repeat type=\"m\"", StringComparison.Ordinal), "Replacing an intact header can edit the existing rule");
        editor.SetSourceMode(true); editor.SetSourceMode(false);
        Check(editor.GetRepeatDraft().Bindings.Single().Id == id, "MD mode changes preserve registration");
        native.Document.Insert(native.Document.TextLength, "\n" + Block);
        Check(editor.GetRepeatDraft().Bindings.Count == 1, "Pasting valid syntax never creates another identity");
        closing = editor.GetMarkdown().IndexOf("[/repeat]", StringComparison.Ordinal);
        native.Document.Remove(closing, 1);
        var broken = editor.GetRepeatDraft();
        RepeatDocumentService.Apply(state, Key, broken);
        Check(state.RepeatBlocks.Count == 0 && state.DateMarkdown[Key] == broken.Markdown, "Committed broken tags drop the mapping without dropping text");
        editor.AcceptRepeatSave([]);
        native.Undo();
        Check(editor.GetRepeatDraft().Bindings.Count == 0, "Undo after committed cleanup cannot silently reactivate a retired identity");
        editor.DiscardDraft("saved local text");
        editor.LoadRepeatDocument(Date, new("saved local text", []));
        Check(editor.GetRepeatDraft().Bindings.Count == 0 && !native.CanUndo, "Cancel restores saved document and discards draft registrations/history");
        editor.LoadRepeatDocument(Date, new("", []));
        Check(editor.InsertRepeat(), "Create the first of multiple blocks");
        native.Select(native.Document.TextLength, 0);
        Check(editor.InsertRepeat(), "Caret after a closing tag can append another standalone block");
        var multiple = editor.GetRepeatDraft();
        Check(multiple.Bindings.Select(binding => binding.Id).Distinct().Count() == 2 && RepeatBlockParser.Parse(multiple.Markdown).Count == 2,
            "Multiple blocks keep separate identities");
        native.Undo();
        Check(editor.GetRepeatDraft().Bindings.Count == 1, "Undo removes only the second block registration");
        native.Redo();
        Check(editor.GetRepeatDraft().Bindings.SequenceEqual(multiple.Bindings), "Redo restores both original identities and ranges");
        native.Document.Insert(0, Block + "\n");
        RepeatDocumentService.Apply(state, Key, editor.GetRepeatDraft());
        Check(state.RepeatBlocks.Count == 2 && RepeatBlockParser.Parse(RepeatDocumentService.Load(state, Key).Markdown).Count == 3 && state.DateMarkdown[Key].StartsWith(Block, StringComparison.Ordinal),
            "Pasting a valid block above existing blocks leaves only the originals registered");
    });

    public static void Presentation() => InSta(() =>
    {
        Localization.SetLanguage(CalendarLanguage.ChineseSimplified);
        foreach (var dark in new[] { false, true })
        {
            var foreground = dark ? Brushes.WhiteSmoke : Brushes.Black;
            var surface = dark ? new SolidColorBrush(Color.FromRgb(32, 37, 44)) : Brushes.White;
            var editor = new MarkdownBlockEditor(foreground, dark ? Brushes.LightGray : Brushes.DimGray, surface, Brushes.Gray, Brushes.DodgerBlue, new FontFamily("Segoe UI"), _ => null);
            var native = (TextEditor)editor.Child!;
            var source = new StringBuilder("当天上方正文\n\n");
            var bindings = new List<RepeatBlockBinding>();
            foreach (var type in new[] { "d", "w", "m" })
            {
                var block = Block.Replace("type=\"w\"", $"type=\"{type}\"", StringComparison.Ordinal)
                    .Replace("**共享正文**", "**共享正文**，长文字自动换行并保留整个区块的颜色。\n[2026 鉴赏家新品节](https://example.com)", StringComparison.Ordinal);
                if (type == "m") block = block.Replace(" enddate=\"2026-02-19\"", "", StringComparison.Ordinal);
                bindings.Add(new(Guid.NewGuid().ToString("N"), source.Length, block.Length));
                source.Append(block).Append("\n\n");
            }
            source.Append("当天下方正文");
            var document = new RepeatDocument(source.ToString(), bindings);
            var root = new Border { Background = surface, Padding = new Thickness(12), Child = editor };
            using var host = new HwndSource(new HwndSourceParameters("RepeatPresentation") { Width = 560, Height = 800 });
            host.RootVisual = root;
            editor.LoadRepeatDocument(Date, document);
            var view = native.TextArea.TextView;
            editor.SetPreviewMode(true);
            Layout(560, 800);
            Check(Headers().Length == 3, "Reading renders three registered repeat headers");
            Check(Headers().Select(label => ((SolidColorBrush)label.Foreground).Color).Distinct().Count() == 3, "Daily, weekly and monthly headers have distinct colors");
            Check(Visuals<TextBlock>(editor).Count(label => label.Text == "截止 2026-02-19") == 2 && Visuals<TextBlock>(editor).Any(label => label.Text == "长期"), "Header shows end date or forever");
            Check(Visuals<CheckBox>(editor).Count() == 3, "Repeat tasks retain native checkbox objects");
            var copied = editor.ReadingText(0, document.Markdown.Length);
            Check(copied.Contains("周提醒", StringComparison.Ordinal) && copied.Contains("共享正文", StringComparison.Ordinal) &&
                !copied.Contains("[repeat", StringComparison.Ordinal) && !copied.Contains("[/repeat]", StringComparison.Ordinal), "Reading copy includes visible metadata and body without reserved tags");
            Check(!RepeatDocumentService.VisibleSource(document).Contains("[repeat", StringComparison.Ordinal), "Calendar summaries hide only registered repeat tag lines");
            var link = editor.Snapshot.Links.First();
            var visual = view.GetVisualLine(native.Document.GetLineByOffset(link.Start).LineNumber)!;
            var column = visual.GetVisualColumn(document.Markdown.IndexOf("2026 鉴赏", StringComparison.Ordinal) - visual.FirstDocumentLine.Offset);
            var textLine = visual.GetTextLine(column);
            var bounds = textLine.GetTextBounds(column, 1).First(bound => bound.Rectangle.Width > 0).Rectangle;
            var y = visual.GetTextLineVisualYPosition(textLine, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle) - view.VerticalOffset;
            Check(editor.HitTestLink(view.TranslatePoint(new Point(bounds.Left + bounds.Width / 2, y), native))?.Url == "https://example.com", "Link glyphs inside repeat blocks stay clickable");
            Check(editor.HitTestLink(view.TranslatePoint(new Point(view.ActualWidth - 2, y), native)) is null, "Repeat row whitespace is not a link");
            Visuals<CheckBox>(editor).First().IsChecked = true;
            Layout(560, 800);
            Check(editor.GetRepeatDraft().Bindings.Count == 3 && editor.GetMarkdown().Contains("[x] shared", StringComparison.Ordinal), "Reading task changes keep all hidden registrations");
            var textDocument = native.Document;
            var changedSource = editor.GetMarkdown();
            var active = RepeatBlockParser.Parse(changedSource).First();
            editor.SetPreviewMode(false, active.BodyStart);
            Layout(560, 800);
            Check(Headers().Length == 2 && !view.GetVisualLine(native.Document.GetLineByOffset(active.Start).LineNumber)!.Elements.Any(element => element is ICSharpCode.AvalonEdit.Rendering.InlineObjectElement), "Editing a repeat block reveals its complete source");
            editor.SetSourceMode(true);
            Layout(560, 800);
            Check(Headers().Length == 0 && ReferenceEquals(textDocument, native.Document) && editor.GetMarkdown() == changedSource, "MD mode reveals all tags without replacing source or document");
            if (Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR") is { Length: > 0 } sourceQa)
                Save(sourceQa, dark ? "repeat-source-colors-dark.png" : "repeat-source-colors-light.png", 560, 800);
            var normalTagColor = dark ? Color.FromRgb(137, 149, 163) : Color.FromRgb(150, 161, 173);
            Check(StyleAt(active.Start).Color == normalTagColor, "Source tags use subdued theme-specific gray");
            foreach (var rule in RepeatBlockParser.Parse(changedSource))
            {
                var valueOffset = rule.Start + "[repeat type=\"".Length;
                var dateOffset = changedSource.IndexOf("2026-01-23", rule.Start, StringComparison.Ordinal);
                Check(StyleAt(valueOffset).Color == StyleAt(dateOffset).Color && StyleAt(dateOffset).Weight == FontWeights.Bold,
                    "Every attribute value keeps its rule's type color and bold weight");
            }
            var originalHeader = native.Document.GetText(active.Start, Header.Length);
            foreach (var badHeader in new[]
            {
                originalHeader.Replace("type=\"d\"", "type=\"x\"", StringComparison.Ordinal),
                originalHeader.Replace("2026-01-23", "2026-02-30", StringComparison.Ordinal),
                originalHeader.Replace("2026-02-19", "2026-01-22", StringComparison.Ordinal),
                originalHeader.Replace("type=\"d\"", "type=\"d\" extra=\"bad\"", StringComparison.Ordinal),
                originalHeader.Replace("type=\"d\"", "type=\"d\" type=\"w\"", StringComparison.Ordinal),
                originalHeader.Replace("2026-02-19\"", "2026-02-19", StringComparison.Ordinal)
            })
            {
                native.Document.Replace(active.Start, originalHeader.Length, badHeader);
                Layout(560, 800);
                var errorTagColor = dark ? Color.FromRgb(160, 128, 128) : Color.FromRgb(185, 151, 151);
                var closingOffset = native.Text.IndexOf("[/repeat]", active.Start, StringComparison.Ordinal);
                Check(StyleAt(active.Start).Color == errorTagColor && StyleAt(closingOffset).Color == errorTagColor,
                    "Invalid attributes tint both tags gray-red");
                var valueStyle = StyleAt(active.Start + "[repeat type=\"".Length);
                Check(valueStyle.Color == Color.FromRgb(239, 68, 68) && valueStyle.Weight == FontWeights.Bold,
                    "Invalid rules use bold red attribute values");
                if (badHeader.Contains("type=\"x\"", StringComparison.Ordinal) &&
                    Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR") is { Length: > 0 } errorQa)
                    Save(errorQa, dark ? "repeat-source-errors-dark.png" : "repeat-source-errors-light.png", 560, 800);
                native.Undo();
                Layout(560, 800);
                Check(editor.GetMarkdown() == changedSource && StyleAt(active.Start).Color == normalTagColor &&
                    StyleAt(active.Start + "[repeat type=\"".Length).Color == Color.FromRgb(25, 145, 118),
                    "Undoing attribute errors restores source and normal colors without stale validation");
            }
            editor.SetSourceMode(false);
            editor.SetPreviewMode(true);
            Layout(560, 800);
            Check(Headers().Length == 3, "Leaving editing restores all repeat cards");
            var typeOffset = active.Start + "[repeat type=\"".Length;
            native.Document.Replace(typeOffset, 1, "x");
            Layout(560, 800);
            Check(Headers().Length == 2 && editor.GetRepeatDraft().Bindings.Count == 3, "Invalid rule falls back to editable text while preserving identity");
            native.Undo();
            Layout(560, 800);
            Check(Headers().Length == 3, "Repairing a rule restores its card");
            native.Document.Remove(active.Start + active.Length - 1, 1);
            Layout(560, 800);
            Check(Headers().Length == 2, "Broken closing tag falls back to source");
            native.Undo();
            Layout(560, 800);
            Check(Headers().Length == 3, "Undo restores the card and its original range");
            if (Environment.GetEnvironmentVariable("SCREW_MARKDOWN_QA_DIR") is { Length: > 0 } qa)
            {
                Save(qa, dark ? "repeat-cards-dark.png" : "repeat-cards-light.png", 560, 800);
                Layout(145, 800);
                Check(Headers().Length == 3, "Narrow repeat headers retain all three types");
                foreach (var label in Headers())
                {
                    var ending = ((Grid)label.Parent).Children.OfType<TextBlock>().Last();
                    Check(ending.TranslatePoint(new Point(0, 0), root).X >= label.TranslatePoint(new Point(label.ActualWidth, 0), root).X,
                        "Narrow end dates never overlap the repeat type");
                }
                Save(qa, dark ? "repeat-cards-narrow-dark.png" : "repeat-cards-narrow-light.png", 145, 800);
            }
            editor.LoadRepeatDocument(Date, new(changedSource, []));
            Layout(560, 800);
            Check(Headers().Length == 0 && editor.ReadingText(0, changedSource.Length).Contains("[repeat", StringComparison.Ordinal), "Identical handwritten syntax stays literal without registrations");

            TextBlock[] Headers() => Visuals<TextBlock>(editor).Where(label => label.Text is "每日提醒" or "周提醒" or "每月提醒").ToArray();
            (Color Color, FontWeight Weight) StyleAt(int offset)
            {
                var visualLine = view.GetVisualLine(native.Document.GetLineByOffset(offset).LineNumber)!;
                var visualColumn = visualLine.GetVisualColumn(offset - visualLine.FirstDocumentLine.Offset);
                var properties = visualLine.Elements.First(element => element.VisualColumn <= visualColumn &&
                    visualColumn < element.VisualColumn + element.VisualLength).TextRunProperties;
                return (((SolidColorBrush)properties.ForegroundBrush).Color, properties.Typeface.Weight);
            }
            void Layout(int width, int height)
            {
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                view.EnsureVisualLines();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            }
            void Save(string folder, string name, int width, int height)
            {
                Directory.CreateDirectory(folder);
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(Path.Combine(folder, name)); encoder.Save(file);
            }
        }
    });

    public static void ProjectedEditing() => InSta(() =>
    {
        var state = RepeatRecurrenceTests.Create(out var id);
        const string key = RepeatRecurrenceTests.Next;
        state.DateMarkdown[key] = "original local text";
        var editor = new MarkdownBlockEditor(Brushes.Black, Brushes.Gray, Brushes.White, Brushes.Gray, Brushes.Blue, new FontFamily("Segoe UI"), _ => null);
        var native = (TextEditor)editor.Child!;
        editor.LoadRepeatDocument(new DateTime(2026, 1, 30), RepeatDocumentService.Load(state, key));
        var closingCharacter = editor.GetRepeatDraft().Bindings.Single().Length - 1;
        native.Document.Remove(closingCharacter, 1);
        native.Document.Insert(closingCharacter, "]");
        Check(editor.GetRepeatDraft().Bindings.Single().GeneratedSeparatorLength == 2, "Repairing a closing tag keeps its untouched generated separator");
        var block = RepeatBlockParser.Parse(editor.GetMarkdown()).Single();
        native.Select(block.BodyStart, block.BodyLength);
        native.SelectedText = "**shared changed**\n- [x] task changed";
        var bodyOnly = editor.GetRepeatDraft();
        Check(bodyOnly.Bindings.Single().GeneratedSeparatorLength == 2, "Body editing preserves generated boundary metadata");
        RepeatDocumentService.Apply(state, key, bodyOnly);
        Check(!state.DateRepeatReferences.ContainsKey(key) && state.DateMarkdown[key] == "original local text", "Native projected body editing stores no date reference or synthetic whitespace");
        editor.LoadRepeatDocument(new DateTime(2026, 1, 30), RepeatDocumentService.Load(state, key));
        native.Document.Insert(0, "local above\n");
        native.Document.Insert(native.Document.TextLength, "\nlocal below");
        var localDraft = editor.GetRepeatDraft();
        native.Undo(); native.Redo();
        Check(editor.GetRepeatDraft().Bindings.SequenceEqual(localDraft.Bindings), "Native outside-text undo preserves projection metadata");
        RepeatDocumentService.Apply(state, key, localDraft);
        Check(state.DateRepeatReferences[key].Single().Id == id && RepeatDocumentService.Load(state, key).Markdown == localDraft.Markdown,
            "Outside edits pin the block's placement and restore the exact composed source");
        Check(!state.RepeatBlocks[id].Contains("local above", StringComparison.Ordinal) && !RepeatDocumentService.Load(state, "2026-02-06").Markdown.Contains("local above", StringComparison.Ordinal),
            "Outside text never leaks to shared content or another date");
        var updated = state.RepeatBlocks[id].Replace("shared changed", "shared synchronized", StringComparison.Ordinal);
        var other = RepeatDocumentService.Load(state, "2026-02-06");
        RepeatDocumentService.Apply(state, "2026-02-06", new(other.Markdown.Replace(state.RepeatBlocks[id], updated, StringComparison.Ordinal),
            other.Bindings.Select(binding => binding with { Length = updated.Length }).ToArray()));
        Check(RepeatDocumentService.Load(state, key).Markdown == localDraft.Markdown.Replace("shared changed", "shared synchronized", StringComparison.Ordinal) && CalendarDataStore.Validate(state),
            "Subsequent shared edits retain pinned local text and placement");
        // A user editing a generated separator turns it into ordinary date text.
        editor.LoadRepeatDocument(new DateTime(2026, 2, 13), RepeatDocumentService.Load(state, "2026-02-13"));
        var end = editor.GetRepeatDraft().Bindings.Single().Length;
        native.Document.Insert(end + 1, "user separator note");
        Check(editor.GetRepeatDraft().Bindings.Single().GeneratedSeparatorLength == 0, "Edited separators are never stripped as generated text");
        RepeatDocumentService.Apply(state, "2026-02-13", editor.GetRepeatDraft());
        Check(state.DateMarkdown["2026-02-13"].Contains("user separator note", StringComparison.Ordinal), "Saving keeps text typed between a block and its generated separator");
    });

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Repeat editor test timed out");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
