using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrewCalendar;

/// <summary>
/// Markdown editor backed by one native WPF RichTextBox.
///
/// A single editing surface owns the caret and selection. This deliberately
/// avoids stitching together independent text boxes, so native WPF handles
/// drag selection, Ctrl+A, copy, paste, deletion, and replacement across
/// every paragraph.
/// </summary>
public sealed class MarkdownBlockEditor : Border
{
    private readonly RichTextBox _editor;
    private readonly Brush _foreground;
    private readonly Brush _muted;
    private readonly Brush _surface;
    private readonly Brush _line;
    private readonly Brush _accent;
    private readonly FontFamily _fontFamily;
    private readonly Func<string, BitmapSource?> _imageLoader;
    private readonly Dictionary<Paragraph, ParagraphMetadata> _paragraphMetadata = [];
    private readonly Dictionary<BlockUIContainer, ImageMetadata> _imageMetadata = [];
    private bool _loading;
    private bool _enterNormalizationScheduled;
    private Paragraph? _pendingEnterSource;
    private MarkdownLineKind? _pendingEnterKind;
    private int _pendingEnterIndent;

    public MarkdownBlockEditor(Brush foreground, Brush muted, Brush surface, Brush line, Brush accent, FontFamily fontFamily, Func<string, BitmapSource?> imageLoader)
    {
        _foreground = foreground;
        _muted = muted;
        _surface = surface;
        _line = line;
        _accent = accent;
        _fontFamily = fontFamily;
        _imageLoader = imageLoader;

        Background = surface;
        BorderBrush = line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(10, 8, 10, 8);

        _editor = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = foreground,
            FontFamily = fontFamily,
            FontSize = 14,
            Padding = new Thickness(4, 3, 4, 3),
            AcceptsTab = true,
            IsDocumentEnabled = true,
            AllowDrop = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = accent,
            SelectionOpacity = .35,
            IsInactiveSelectionHighlightEnabled = true
        };
        _editor.Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = fontFamily,
            FontSize = 14,
            Foreground = foreground
        };
        if (Application.Current?.TryFindResource("ToolkitMinimalScrollBarStyle") is Style scrollBarStyle)
            _editor.Resources.Add(typeof(ScrollBar), scrollBarStyle);

        _editor.TextChanged += (_, _) =>
        {
            PruneMetadata();
            ScheduleEnterNormalization();
            RaiseChanged();
        };
        _editor.PreviewKeyDown += TrackEnterInheritance;
        _editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, HandleCopyCommand));
        _editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, HandleCutCommand));
        _editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, HandlePasteCommand));
        Child = _editor;
    }

    public event EventHandler? ContentChanged;

    public void SetMarkdown(string markdown)
    {
        ClearPendingEnterNormalization();
        _loading = true;
        _editor.Document.Blocks.Clear();
        _paragraphMetadata.Clear();
        _imageMetadata.Clear();

        foreach (var line in MarkdownDocumentService.ParseLines(markdown))
        {
            if (line.Kind == MarkdownLineKind.Image)
                _editor.Document.Blocks.Add(CreateImageBlock(line.ImagePath ?? string.Empty, line.Text));
            else
                _editor.Document.Blocks.Add(CreateParagraph(line));
        }

        if (_editor.Document.Blocks.Count == 0)
            _editor.Document.Blocks.Add(CreateParagraph(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty)));
        _loading = false;
    }

    public string GetMarkdown()
    {
        var snapshot = BuildSourceDocument();
        return MarkdownDocumentService.SerializeLines(snapshot.Lines.Select(item => item.Line));
    }

    public void ApplyKind(MarkdownLineKind kind)
    {
        if (kind == MarkdownLineKind.Image) return;

        var selected = GetSelectedParagraphs();
        if (selected.Count == 0)
        {
            var paragraph = GetCurrentParagraph() ?? CreateParagraph(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty));
            if (!_editor.Document.Blocks.Contains(paragraph)) _editor.Document.Blocks.Add(paragraph);
            selected = [paragraph];
        }

        var selectionStart = selected[0].ContentStart;
        var selectionEnd = selected[^1].ContentEnd;
        _loading = true;
        foreach (var paragraph in selected)
        {
            var metadata = GetParagraphMetadata(paragraph);
            var text = ExtractParagraphText(paragraph, metadata.Kind);
            metadata.Kind = kind;
            if (kind != MarkdownLineKind.Task) metadata.IsChecked = false;
            ReplaceParagraphContent(paragraph, metadata, text);
        }
        _loading = false;
        RestoreSelection(selectionStart, selectionEnd);
        RaiseChanged();
    }

    public void AddImage(string relativePath, string altText = "image")
    {
        var current = GetCurrentBlock();
        var block = CreateImageBlock(relativePath, altText);
        if (current is null)
        {
            _editor.Document.Blocks.Add(block);
        }
        else
        {
            var blocks = _editor.Document.Blocks.ToList();
            var currentIndex = blocks.IndexOf(current);
            if (currentIndex < 0 || currentIndex == blocks.Count - 1)
                _editor.Document.Blocks.Add(block);
            else
                _editor.Document.Blocks.InsertBefore(blocks[currentIndex + 1], block);
        }
        block.BringIntoView();
        RaiseChanged();
    }

    public void FocusEditor()
    {
        var paragraph = _editor.Document.Blocks.OfType<Paragraph>().LastOrDefault();
        if (paragraph is null)
        {
            paragraph = CreateParagraph(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty));
            _editor.Document.Blocks.Add(paragraph);
        }
        _editor.Focus();
        _editor.CaretPosition = paragraph.ContentEnd;
    }

    private void TrackEnterInheritance(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (_editor.Selection.Start.CompareTo(_editor.Selection.End) != 0) return;

        var paragraph = GetCurrentParagraph();
        if (paragraph is null) return;
        var metadata = GetParagraphMetadata(paragraph);
        if (metadata.Kind is not (MarkdownLineKind.Bullet or MarkdownLineKind.Task)) return;

        _pendingEnterSource = paragraph;
        _pendingEnterKind = metadata.Kind;
        _pendingEnterIndent = metadata.Indent;
        _enterNormalizationScheduled = false;
    }

    private void ScheduleEnterNormalization()
    {
        if (_loading || _pendingEnterKind is null || _enterNormalizationScheduled) return;
        _enterNormalizationScheduled = true;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(NormalizeInheritedParagraph));
    }

    private void NormalizeInheritedParagraph()
    {
        _enterNormalizationScheduled = false;
        var kind = _pendingEnterKind;
        var source = _pendingEnterSource;
        var indent = _pendingEnterIndent;
        ClearPendingEnterNormalization();
        if (kind is null || source is null || _loading) return;

        var target = GetCurrentParagraph();
        if (target is null || ReferenceEquals(target, source)) return;

        var metadata = GetParagraphMetadata(target);
        var text = ExtractParagraphText(target, metadata.Kind);
        metadata.Kind = kind.Value;
        metadata.Indent = indent;
        metadata.IsChecked = false;
        _loading = true;
        ReplaceParagraphContent(target, metadata, text);
        _loading = false;
        RaiseChanged();
    }

    private void ClearPendingEnterNormalization()
    {
        _pendingEnterSource = null;
        _pendingEnterKind = null;
        _pendingEnterIndent = 0;
        _enterNormalizationScheduled = false;
    }

    private void HandleCopyCommand(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        if (_editor.Selection.Start.CompareTo(_editor.Selection.End) == 0) return;
        var markdown = GetSelectedMarkdown();
        if (markdown is null) return;

        try
        {
            Clipboard.SetText(markdown);
            eventArgs.Handled = true;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to copy Markdown selection.", exception);
        }
    }

    private void HandleCutCommand(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        if (_editor.Selection.Start.CompareTo(_editor.Selection.End) == 0) return;
        var markdown = GetSelectedMarkdown();
        if (markdown is null) return;

        try
        {
            Clipboard.SetText(markdown);
            ReplaceSelectedMarkdown(string.Empty);
            eventArgs.Handled = true;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to cut Markdown selection.", exception);
        }
    }

    private void HandlePasteCommand(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList() || !Clipboard.ContainsText()) return;

        try
        {
            ReplaceSelectedMarkdown(Clipboard.GetText());
            eventArgs.Handled = true;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to paste Markdown text.", exception);
        }
    }

    private string? GetSelectedMarkdown()
    {
        var snapshot = BuildSourceDocument();
        if (snapshot.Lines.Count == 0) return null;

        var start = GetSourceOffset(snapshot, _editor.Selection.Start);
        var end = GetSourceOffset(snapshot, _editor.Selection.End);
        if (start > end) (start, end) = (end, start);
        return snapshot.Text[Math.Clamp(start, 0, snapshot.Text.Length)..Math.Clamp(end, 0, snapshot.Text.Length)];
    }

    private void ReplaceSelectedMarkdown(string replacement)
    {
        var snapshot = BuildSourceDocument();
        if (snapshot.Lines.Count == 0) return;

        var start = GetSourceOffset(snapshot, _editor.Selection.Start);
        var end = GetSourceOffset(snapshot, _editor.Selection.End);
        if (start > end) (start, end) = (end, start);
        start = Math.Clamp(start, 0, snapshot.Text.Length);
        end = Math.Clamp(end, start, snapshot.Text.Length);
        var normalized = replacement
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        var updated = snapshot.Text[..start] + normalized + snapshot.Text[end..];

        _loading = true;
        SetMarkdown(updated);
        _loading = false;
        RestoreCaretAtSourceOffset(start + normalized.Length);
        RaiseChanged();
    }

    private Paragraph CreateParagraph(MarkdownLine line)
    {
        var paragraph = new Paragraph();
        var metadata = new ParagraphMetadata(line.Kind, line.IsChecked, line.Indent);
        _paragraphMetadata[paragraph] = metadata;
        ReplaceParagraphContent(paragraph, metadata, line.Text);
        return paragraph;
    }

    private void ReplaceParagraphContent(Paragraph paragraph, ParagraphMetadata metadata, string text)
    {
        paragraph.Inlines.Clear();
        ApplyParagraphTypography(paragraph, metadata);

        if (metadata.Kind == MarkdownLineKind.Bullet)
        {
            paragraph.Inlines.Add(new Run("• ")
            {
                Foreground = _accent,
                FontWeight = FontWeights.SemiBold
            });
        }
        else if (metadata.Kind == MarkdownLineKind.Task)
        {
            var checkbox = CreateTaskCheckBox(paragraph, metadata);
            paragraph.Inlines.Add(new InlineUIContainer(checkbox)
            {
                BaselineAlignment = BaselineAlignment.Center
            });
            paragraph.Inlines.Add(new Run(" "));
        }

        paragraph.Inlines.Add(new Run(text ?? string.Empty));
    }

    private CheckBox CreateTaskCheckBox(Paragraph paragraph, ParagraphMetadata metadata)
    {
        var checkbox = new CheckBox
        {
            IsChecked = metadata.IsChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true,
            ToolTip = "完成任务",
            Style = Application.Current?.TryFindResource("ToolkitRoundedCheckBoxStyle") as Style
        };
        checkbox.Checked += (_, _) => UpdateTaskState(paragraph, metadata, true);
        checkbox.Unchecked += (_, _) => UpdateTaskState(paragraph, metadata, false);
        return checkbox;
    }

    private void UpdateTaskState(Paragraph paragraph, ParagraphMetadata metadata, bool isChecked)
    {
        metadata.IsChecked = isChecked;
        ApplyParagraphTypography(paragraph, metadata);
        RaiseChanged();
    }

    private void ApplyParagraphTypography(Paragraph paragraph, ParagraphMetadata metadata)
    {
        paragraph.Margin = new Thickness(metadata.Indent * 18, metadata.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? 4 : 1, 0, metadata.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? 5 : 1);
        paragraph.FontFamily = _fontFamily;
        paragraph.FontSize = metadata.Kind switch
        {
            MarkdownLineKind.Heading1 => 24,
            MarkdownLineKind.Heading2 => 20,
            MarkdownLineKind.Heading3 => 17,
            _ => 14
        };
        paragraph.FontWeight = metadata.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        paragraph.Foreground = metadata.Kind == MarkdownLineKind.Task && metadata.IsChecked ? _muted : _foreground;
        paragraph.TextDecorations = metadata.Kind == MarkdownLineKind.Task && metadata.IsChecked ? TextDecorations.Strikethrough : null;
    }

    private BlockUIContainer CreateImageBlock(string relativePath, string altText)
    {
        var host = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        host.Children.Add(new Image
        {
            Source = _imageLoader(relativePath),
            MaxHeight = 260,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });

        var imageBlock = new BlockUIContainer(host) { Margin = new Thickness(0) };
        _imageMetadata[imageBlock] = new ImageMetadata(altText, relativePath);
        var remove = new Button
        {
            Content = "×",
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 4, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = _surface,
            Foreground = _muted,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        remove.Click += (_, _) =>
        {
            if (!_editor.Document.Blocks.Contains(imageBlock)) return;
            _editor.Document.Blocks.Remove(imageBlock);
            _imageMetadata.Remove(imageBlock);
            if (_editor.Document.Blocks.Count == 0)
                _editor.Document.Blocks.Add(CreateParagraph(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty)));
            RaiseChanged();
        };
        host.Children.Add(remove);
        return imageBlock;
    }

    private IReadOnlyList<Paragraph> GetSelectedParagraphs()
    {
        var start = _editor.Selection.Start;
        var end = _editor.Selection.End;
        if (start.CompareTo(end) > 0) (start, end) = (end, start);

        if (start.CompareTo(end) == 0)
        {
            var current = GetParagraphAt(start);
            return current is null ? [] : [current];
        }

        return _editor.Document.Blocks
            .OfType<Paragraph>()
            .Where(paragraph => paragraph.ContentStart.CompareTo(end) < 0 && paragraph.ContentEnd.CompareTo(start) > 0)
            .ToList();
    }

    private Paragraph? GetCurrentParagraph() => GetParagraphAt(_editor.Selection.Start);

    private Paragraph? GetParagraphAt(TextPointer pointer)
    {
        var direct = pointer.Paragraph;
        if (direct is not null && _editor.Document.Blocks.Contains(direct)) return direct;
        return _editor.Document.Blocks
            .OfType<Paragraph>()
            .FirstOrDefault(paragraph => paragraph.ContentStart.CompareTo(pointer) <= 0 && paragraph.ContentEnd.CompareTo(pointer) >= 0);
    }

    private Block? GetCurrentBlock()
    {
        var paragraph = GetCurrentParagraph();
        if (paragraph is not null) return paragraph;
        return _editor.Document.Blocks.LastOrDefault();
    }

    private SourceDocument BuildSourceDocument()
    {
        PruneMetadata();
        var lines = new List<SourceLine>();
        var start = 0;
        foreach (var block in _editor.Document.Blocks)
        {
            MarkdownLine? line = block switch
            {
                BlockUIContainer imageBlock when _imageMetadata.TryGetValue(imageBlock, out var image)
                    => new MarkdownLine(MarkdownLineKind.Image, image.AltText, ImagePath: image.RelativePath),
                Paragraph paragraph => CreateMarkdownLine(paragraph),
                _ => null
            };
            if (line is null) continue;

            var sourceText = MarkdownDocumentService.SerializeLine(line);
            lines.Add(new SourceLine(block, line, sourceText, start));
            start += sourceText.Length + Environment.NewLine.Length;
        }

        var documentText = string.Join(Environment.NewLine, lines.Select(item => item.SourceText));
        return new SourceDocument(lines, documentText);
    }

    private MarkdownLine CreateMarkdownLine(Paragraph paragraph)
    {
        var metadata = GetParagraphMetadata(paragraph);
        return new MarkdownLine(
            metadata.Kind,
            ExtractParagraphText(paragraph, metadata.Kind),
            metadata.IsChecked,
            metadata.Indent);
    }

    private int GetSourceOffset(SourceDocument snapshot, TextPointer pointer)
    {
        var sourceLine = FindSourceLine(snapshot, pointer);
        if (sourceLine is null) return pointer.CompareTo(_editor.Document.ContentStart) <= 0 ? 0 : snapshot.Text.Length;

        if (sourceLine.Block is not Paragraph paragraph)
            return sourceLine.Start;
        if (pointer.CompareTo(paragraph.ContentStart) <= 0)
            return sourceLine.Start;

        var prefixLength = MarkdownPrefixLength(sourceLine.Line);
        var textOffset = GetParagraphTextOffset(paragraph, pointer, sourceLine.Line.Kind);
        return Math.Clamp(sourceLine.Start + prefixLength + textOffset, sourceLine.Start, sourceLine.Start + sourceLine.SourceText.Length);
    }

    private static int MarkdownPrefixLength(MarkdownLine line) => line.Kind switch
    {
        MarkdownLineKind.Heading1 => 2,
        MarkdownLineKind.Heading2 => 3,
        MarkdownLineKind.Heading3 => 4,
        MarkdownLineKind.Bullet => line.Indent * 2 + 2,
        MarkdownLineKind.Task => line.Indent * 2 + 6,
        _ => 0
    };

    private SourceLine? FindSourceLine(SourceDocument snapshot, TextPointer pointer)
    {
        var paragraph = pointer.Paragraph;
        if (paragraph is not null)
            return snapshot.Lines.FirstOrDefault(line => ReferenceEquals(line.Block, paragraph));

        foreach (var line in snapshot.Lines)
        {
            if (pointer.CompareTo(line.Block.ContentStart) >= 0 && pointer.CompareTo(line.Block.ContentEnd) <= 0)
                return line;
        }
        return pointer.CompareTo(_editor.Document.ContentStart) <= 0 ? snapshot.Lines.FirstOrDefault() : snapshot.Lines.LastOrDefault();
    }

    private static int GetParagraphTextOffset(Paragraph paragraph, TextPointer pointer, MarkdownLineKind kind)
    {
        var offset = 0;
        foreach (var run in EnumerateRuns(paragraph.Inlines))
        {
            if (IsStructuralRun(paragraph, run, kind))
            {
                if (pointer.CompareTo(run.ContentEnd) < 0) return offset;
                continue;
            }

            if (pointer.CompareTo(run.ContentStart) <= 0) return offset;
            if (pointer.CompareTo(run.ContentEnd) < 0)
                return offset + GetRunTextOffset(run, pointer);
            offset += run.Text.Length;
        }
        return offset;
    }

    private static int GetRunTextOffset(Run run, TextPointer pointer)
    {
        if (pointer.CompareTo(run.ContentStart) <= 0) return 0;
        if (pointer.CompareTo(run.ContentEnd) >= 0) return run.Text.Length;
        var text = new TextRange(run.ContentStart, pointer).Text;
        return Math.Clamp(text.Length, 0, run.Text.Length);
    }

    private static IEnumerable<Run> EnumerateRuns(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return run;
                    break;
                case Span span:
                    foreach (var runInSpan in EnumerateRuns(span.Inlines)) yield return runInSpan;
                    break;
            }
        }
    }

    private static bool IsStructuralRun(Paragraph paragraph, Run run, MarkdownLineKind kind)
    {
        if (kind == MarkdownLineKind.Bullet)
            return ReferenceEquals(paragraph.Inlines.FirstInline, run) && run.Text.StartsWith("• ", StringComparison.Ordinal);
        if (kind != MarkdownLineKind.Task) return false;

        return paragraph.Inlines.FirstInline is InlineUIContainer first && ReferenceEquals(first.NextInline, run);
    }

    private void RestoreCaretAtSourceOffset(int offset)
    {
        var snapshot = BuildSourceDocument();
        var pointer = GetPointerAtSourceOffset(snapshot, offset);
        if (pointer is null)
        {
            FocusEditor();
            return;
        }
        _editor.Focus();
        _editor.CaretPosition = pointer;
    }

    private TextPointer? GetPointerAtSourceOffset(SourceDocument snapshot, int offset)
    {
        if (snapshot.Lines.Count == 0) return null;
        offset = Math.Clamp(offset, 0, snapshot.Text.Length);

        for (var index = 0; index < snapshot.Lines.Count; index++)
        {
            var line = snapshot.Lines[index];
            var lineEnd = line.Start + line.SourceText.Length;
            if (offset < line.Start)
                return GetParagraphEnd(snapshot.Lines[index - 1]);
            if (offset > lineEnd) continue;

            if (line.Block is Paragraph paragraph)
            {
                var relative = offset - line.Start;
                var prefixLength = MarkdownPrefixLength(line.Line);
                if (relative <= prefixLength) return GetFirstTextPointer(paragraph, line.Line.Kind);
                return GetParagraphTextPointer(paragraph, line.Line.Kind, relative - prefixLength);
            }
            return line.Block.ContentStart;
        }
        return GetParagraphEnd(snapshot.Lines[^1]);
    }

    private static TextPointer GetFirstTextPointer(Paragraph paragraph, MarkdownLineKind kind)
    {
        foreach (var run in EnumerateRuns(paragraph.Inlines))
            if (!IsStructuralRun(paragraph, run, kind)) return run.ContentStart;
        return paragraph.ContentEnd;
    }

    private static TextPointer GetParagraphTextPointer(Paragraph paragraph, MarkdownLineKind kind, int offset)
    {
        offset = Math.Max(0, offset);
        foreach (var run in EnumerateRuns(paragraph.Inlines))
        {
            if (IsStructuralRun(paragraph, run, kind)) continue;
            if (offset <= run.Text.Length)
                return run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
            offset -= run.Text.Length;
        }
        return paragraph.ContentEnd;
    }

    private static TextPointer GetParagraphEnd(SourceLine line) => line.Block is Paragraph paragraph ? paragraph.ContentEnd : line.Block.ContentEnd;

    private void RestoreSelection(TextPointer start, TextPointer end)
    {
        try
        {
            _editor.Selection.Select(start, end);
            _editor.Focus();
        }
        catch (ArgumentException)
        {
            FocusEditor();
        }
    }

    private ParagraphMetadata GetParagraphMetadata(Paragraph paragraph)
    {
        if (_paragraphMetadata.TryGetValue(paragraph, out var metadata)) return metadata;

        var firstInline = paragraph.Inlines.FirstInline;
        var kind = firstInline is InlineUIContainer { Child: CheckBox }
            ? MarkdownLineKind.Task
            : firstInline is Run { Text: string text } && text.StartsWith("• ", StringComparison.Ordinal)
                ? MarkdownLineKind.Bullet
                : paragraph.FontSize switch
                {
                    >= 22 => MarkdownLineKind.Heading1,
                    >= 18 => MarkdownLineKind.Heading2,
                    >= 16 => MarkdownLineKind.Heading3,
                    _ => MarkdownLineKind.Paragraph
                };
        var isChecked = firstInline is InlineUIContainer { Child: CheckBox checkbox } && checkbox.IsChecked == true;
        metadata = new ParagraphMetadata(kind, isChecked, Math.Max(0, (int)Math.Round(paragraph.Margin.Left / 18)));
        _paragraphMetadata[paragraph] = metadata;
        return metadata;
    }

    private static string ExtractParagraphText(Paragraph paragraph, MarkdownLineKind kind)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in paragraph.Inlines)
            AppendInlineText(inline, text);

        var value = text.ToString();
        if (kind == MarkdownLineKind.Bullet && value.StartsWith("• ", StringComparison.Ordinal))
            value = value[2..];
        else if (kind == MarkdownLineKind.Task && value.StartsWith(' '))
            value = value[1..];
        return value;
    }

    private static void AppendInlineText(Inline inline, System.Text.StringBuilder target)
    {
        switch (inline)
        {
            case Run run:
                target.Append(run.Text);
                break;
            case LineBreak:
                target.Append('\n');
                break;
            case InlineUIContainer:
                break;
            case Span span:
                foreach (var child in span.Inlines) AppendInlineText(child, target);
                break;
        }
    }

    private void PruneMetadata()
    {
        var paragraphs = _editor.Document.Blocks.OfType<Paragraph>().ToHashSet();
        foreach (var paragraph in _paragraphMetadata.Keys.Where(item => !paragraphs.Contains(item)).ToList())
            _paragraphMetadata.Remove(paragraph);

        var images = _editor.Document.Blocks.OfType<BlockUIContainer>().ToHashSet();
        foreach (var image in _imageMetadata.Keys.Where(item => !images.Contains(item)).ToList())
            _imageMetadata.Remove(image);
    }

    private void RaiseChanged()
    {
        if (!_loading) ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ParagraphMetadata
    {
        public ParagraphMetadata(MarkdownLineKind kind, bool isChecked, int indent)
        {
            Kind = kind;
            IsChecked = isChecked;
            Indent = indent;
        }

        public MarkdownLineKind Kind { get; set; }
        public bool IsChecked { get; set; }
        public int Indent { get; set; }
    }

    private sealed record ImageMetadata(string AltText, string RelativePath);

    private sealed record SourceLine(Block Block, MarkdownLine Line, string SourceText, int Start);

    private sealed record SourceDocument(IReadOnlyList<SourceLine> Lines, string Text);
}
