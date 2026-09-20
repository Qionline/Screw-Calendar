using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ScrewCalendar;

/// <summary>
/// Markdown editor backed by one AvalonEdit TextDocument.
///
/// The document text is the single source of truth. Markdown decorations and
/// images are rendered by AvalonEdit visual layers, so normal selection,
/// multiline editing, clipboard operations and undo never depend on a WPF
/// FlowDocument tree.
/// </summary>
public sealed class MarkdownBlockEditor : Border
{
    private static readonly Regex ImageLine = new(@"^!\[(.*?)\]\((.*?)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex TaskLine = new(@"^(\s*)[-*+]\s+\[([ xX])\]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletLine = new(@"^(\s*)[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex HeadingLine = new(@"^(\s*)(#{1,3})\s+(.+)$", RegexOptions.Compiled);

    private readonly TextEditor _editor;
    private readonly Brush _foreground;
    private readonly Brush _muted;
    private readonly Brush _surface;
    private readonly Brush _line;
    private readonly Brush _accent;
    private readonly FontFamily _fontFamily;
    private readonly Func<string, BitmapSource?> _imageLoader;
    private readonly MarkdownVisualGenerator _visualGenerator;
    private readonly MarkdownColorizer _colorizer;
    private bool _loading;
    private bool _enterNormalizationScheduled;
    private MarkdownLineKind? _pendingEnterKind;
    private int _pendingEnterIndent;

    public MarkdownBlockEditor(
        Brush foreground,
        Brush muted,
        Brush surface,
        Brush line,
        Brush accent,
        FontFamily fontFamily,
        Func<string, BitmapSource?> imageLoader,
        Brush? selectionBrush = null)
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

        _editor = new TextEditor
        {
            Background = Brushes.Transparent,
            Foreground = foreground,
            FontFamily = fontFamily,
            FontSize = 14,
            Padding = new Thickness(4, 3, 4, 3),
            AllowDrop = true,
            ShowLineNumbers = false,
            WordWrap = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _editor.Options.EnableImeSupport = true;
        _editor.Options.EnableHyperlinks = true;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.Options.IndentationSize = 2;
        _editor.TextArea.SelectionBrush = selectionBrush ?? CreateFallbackSelectionBrush(accent, surface);
        _editor.TextArea.SelectionForeground = foreground;
        // AvalonEdit draws a separate selection outline by default. It uses
        // the system highlight pen, which is why a dark blue hairline remains
        // around an otherwise light selection fill. The fill is sufficient for
        // this editor, so make that outline fully transparent.
        var transparentSelectionBorder = new Pen(Brushes.Transparent, 0);
        transparentSelectionBorder.Freeze();
        _editor.TextArea.SelectionBorder = transparentSelectionBorder;
        _editor.TextArea.SelectionCornerRadius = 0;
        _editor.TextArea.Caret.CaretBrush = accent;
        if (Application.Current?.TryFindResource("ToolkitMinimalScrollBarStyle") is Style scrollBarStyle)
            _editor.Resources.Add(typeof(ScrollBar), scrollBarStyle);

        _visualGenerator = new MarkdownVisualGenerator(this);
        _colorizer = new MarkdownColorizer(_foreground, _muted, _accent, _fontFamily);
        _editor.TextArea.TextView.ElementGenerators.Add(_visualGenerator);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);

        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            ScheduleEnterNormalization();
            RaiseChanged();
        };
        _editor.PreviewKeyDown += TrackEnterInheritance;
        Child = _editor;
    }

    private static Brush CreateFallbackSelectionBrush(Brush accent, Brush surface)
    {
        if (accent is not SolidColorBrush accentBrush || surface is not SolidColorBrush surfaceBrush)
            return accent;

        // AvalonEdit expects an opaque selection fill. Keep the existing accent
        // on dark surfaces; on light surfaces, blend a small amount of the
        // current accent into the editor surface so selected text stays readable.
        var surfaceColor = surfaceBrush.Color;
        var surfaceLuminance = (0.2126 * surfaceColor.R + 0.7152 * surfaceColor.G + 0.0722 * surfaceColor.B) / 255.0;
        if (surfaceLuminance < 0.5)
            return new SolidColorBrush(accentBrush.Color);

        const double accentAmount = 0.14;
        var accentColor = accentBrush.Color;
        var blended = Color.FromRgb(
            BlendChannel(surfaceColor.R, accentColor.R, accentAmount),
            BlendChannel(surfaceColor.G, accentColor.G, accentAmount),
            BlendChannel(surfaceColor.B, accentColor.B, accentAmount));
        return new SolidColorBrush(blended);
    }

    private static byte BlendChannel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * amount);

    public event EventHandler? ContentChanged;

    public void SetMarkdown(string markdown)
    {
        ClearPendingEnterNormalization();
        _loading = true;
        try
        {
            var value = markdown ?? string.Empty;
            _editor.Document.BeginUpdate();
            try
            {
                _editor.Document.Replace(0, _editor.Document.TextLength, value);
            }
            finally
            {
                _editor.Document.EndUpdate();
            }

            _editor.Document.UndoStack.ClearAll();
            _editor.Select(0, 0);
        }
        finally
        {
            _loading = false;
        }
        _editor.TextArea.TextView.Redraw();
    }

    public string GetMarkdown() => _editor.Text.TrimEnd('\r', '\n');

    public void ApplyKind(MarkdownLineKind kind)
    {
        if (kind == MarkdownLineKind.Image || _editor.Document.TextLength == 0) return;

        var selectionStart = _editor.SelectionStart;
        var selectionEnd = selectionStart + _editor.SelectionLength;
        var firstLine = _editor.Document.GetLineByOffset(Math.Min(selectionStart, _editor.Document.TextLength));
        var lastOffset = selectionEnd > selectionStart
            ? Math.Max(selectionStart, Math.Min(selectionEnd - 1, _editor.Document.TextLength))
            : selectionStart;
        var lastLine = _editor.Document.GetLineByOffset(lastOffset);
        var replaceStart = firstLine.Offset;
        var replaceEnd = lastLine.EndOffset;
        var source = _editor.Document.GetText(replaceStart, replaceEnd - replaceStart);
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var formatted = string.Join(newline, lines.Select(line => FormatLine(line, kind)));

        _editor.Document.BeginUpdate();
        try
        {
            _editor.Document.Replace(replaceStart, replaceEnd - replaceStart, formatted);
        }
        finally
        {
            _editor.Document.EndUpdate();
        }
        _editor.Select(replaceStart, formatted.Length);
        RaiseChanged();
    }

    public void AddImage(string relativePath, string altText = "image")
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var reference = MarkdownDocumentService.SerializeLine(
            new MarkdownLine(MarkdownLineKind.Image, altText ?? "image", ImagePath: relativePath));
        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;

        _editor.Document.BeginUpdate();
        try
        {
            if (length > 0)
            {
                _editor.Document.Replace(start, length, reference);
            }
            else if (_editor.Document.TextLength == 0)
            {
                _editor.Document.Insert(0, reference);
                start = 0;
            }
            else
            {
                var line = _editor.Document.GetLineByOffset(Math.Min(_editor.CaretOffset, _editor.Document.TextLength));
                var newline = DetectNewline();
                var before = line.Length > 0 ? newline : string.Empty;
                var insertion = before + reference;
                var insertionOffset = line.EndOffset;
                _editor.Document.Insert(insertionOffset, insertion);
                start = insertionOffset + before.Length;
            }
        }
        finally
        {
            _editor.Document.EndUpdate();
        }

        _editor.Select(start + reference.Length, 0);
        _editor.TextArea.Caret.BringCaretToView();
        RaiseChanged();
    }

    public void FocusEditor()
    {
        _editor.Focus();
        _editor.Select(_editor.Document.TextLength, 0);
        _editor.TextArea.Caret.BringCaretToView();
    }

    private void TrackEnterInheritance(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Handled || eventArgs.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (_editor.SelectionLength != 0) return;

        var line = _editor.Document.GetLineByOffset(Math.Min(_editor.CaretOffset, _editor.Document.TextLength));
        var source = _editor.Document.GetText(line.Offset, line.Length);
        if (TryReadLine(source, out var parsed) && parsed.Kind is MarkdownLineKind.Bullet or MarkdownLineKind.Task)
        {
            _pendingEnterKind = parsed.Kind;
            _pendingEnterIndent = parsed.Indent;
            _enterNormalizationScheduled = false;
        }
        else
        {
            ClearPendingEnterNormalization();
        }
    }

    private void ScheduleEnterNormalization()
    {
        if (_loading || _pendingEnterKind is null || _enterNormalizationScheduled) return;
        _enterNormalizationScheduled = true;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(NormalizeInheritedLine));
    }

    private void NormalizeInheritedLine()
    {
        _enterNormalizationScheduled = false;
        var kind = _pendingEnterKind;
        var indent = _pendingEnterIndent;
        ClearPendingEnterNormalization();
        if (kind is null || _loading) return;

        var line = _editor.Document.GetLineByOffset(Math.Min(_editor.CaretOffset, _editor.Document.TextLength));
        var source = _editor.Document.GetText(line.Offset, line.Length);
        if (TryReadLine(source, out var parsed) && parsed.Kind is MarkdownLineKind.Bullet or MarkdownLineKind.Task) return;

        var content = source.TrimStart();
        var formatted = MarkdownDocumentService.SerializeLine(
            new MarkdownLine(kind.Value, content, IsChecked: false, Indent: indent));
        if (string.Equals(source, formatted, StringComparison.Ordinal)) return;

        _editor.Document.Replace(line.Offset, line.Length, formatted);
        _editor.CaretOffset = line.Offset + formatted.Length;
        RaiseChanged();
    }

    private void ClearPendingEnterNormalization()
    {
        _pendingEnterKind = null;
        _pendingEnterIndent = 0;
        _enterNormalizationScheduled = false;
    }

    private string DetectNewline() => _editor.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : Environment.NewLine;

    private static string FormatLine(string source, MarkdownLineKind kind)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        if (!TryReadLine(source, out var parsed))
            parsed = new MarkdownLine(MarkdownLineKind.Paragraph, source.Trim());
        if (parsed.Kind == MarkdownLineKind.Image) return source;
        return MarkdownDocumentService.SerializeLine(
            new MarkdownLine(kind, parsed.Text, kind == MarkdownLineKind.Task && parsed.IsChecked, parsed.Indent));
    }

    private static bool TryReadLine(string source, out MarkdownLine line)
    {
        var parsed = MarkdownDocumentService.ParseLines(source).FirstOrDefault();
        if (parsed is null)
        {
            line = new MarkdownLine(MarkdownLineKind.Paragraph, source.Trim());
            return false;
        }
        line = parsed;
        return true;
    }

    private void ToggleTask(TextAnchor anchor, bool isChecked)
    {
        if (anchor.IsDeleted) return;
        var line = _editor.Document.GetLineByOffset(Math.Min(anchor.Offset, _editor.Document.TextLength));
        var source = _editor.Document.GetText(line.Offset, line.Length);
        var match = TaskLine.Match(source);
        if (!match.Success) return;
        var markerStart = line.Offset + match.Groups[1].Length + 2;
        if (markerStart + 1 >= _editor.Document.TextLength) return;

        _editor.Document.BeginUpdate();
        try
        {
            _editor.Document.Replace(markerStart + 1, 1, isChecked ? "x" : " ");
        }
        finally
        {
            _editor.Document.EndUpdate();
        }
        RaiseChanged();
    }

    private void RemoveImage(TextAnchor anchor, int sourceLength)
    {
        if (anchor.IsDeleted) return;
        var line = _editor.Document.GetLineByOffset(Math.Min(anchor.Offset, _editor.Document.TextLength));
        if (line.Length != sourceLength || !ImageLine.IsMatch(_editor.Document.GetText(line.Offset, line.Length))) return;

        var start = line.Offset;
        var length = line.Length;
        if (line.NextLine is not null)
        {
            length += line.DelimiterLength;
        }
        else if (line.PreviousLine is not null)
        {
            start -= line.PreviousLine.DelimiterLength;
            length += line.PreviousLine.DelimiterLength;
        }

        _editor.Document.BeginUpdate();
        try
        {
            _editor.Document.Remove(start, length);
        }
        finally
        {
            _editor.Document.EndUpdate();
        }
        _editor.CaretOffset = Math.Min(start, _editor.Document.TextLength);
        _editor.Focus();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (!_loading) ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class MarkdownVisualGenerator : VisualLineElementGenerator
    {
        private readonly MarkdownBlockEditor _owner;

        public MarkdownVisualGenerator(MarkdownBlockEditor owner) => _owner = owner;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            for (var line = CurrentContext.VisualLine.FirstDocumentLine;
                 line is not null && line.LineNumber <= CurrentContext.VisualLine.LastDocumentLine.LineNumber;
                 line = line.NextLine)
            {
                var source = document.GetText(line.Offset, line.Length);
                var candidate = PrefixOffset(line, source, out _);
                if (candidate >= startOffset) return candidate;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var document = CurrentContext.Document;
            if (document.TextLength == 0 || offset < 0 || offset >= document.TextLength) return null;
            var line = document.GetLineByOffset(offset);
            var source = document.GetText(line.Offset, line.Length);
            var candidate = PrefixOffset(line, source, out var kind);
            if (candidate != offset) return null;

            if (kind == MarkdownLineKind.Image)
            {
                var match = ImageLine.Match(source);
                if (!match.Success) return null;
                var anchor = document.CreateAnchor(line.Offset);
                anchor.SurviveDeletion = true;
                return new InlineObjectElement(line.Length, _owner.CreateImageHost(anchor, line.Length, match.Groups[1].Value, match.Groups[2].Value));
            }

            if (kind == MarkdownLineKind.Task)
            {
                var match = TaskLine.Match(source);
                if (!match.Success) return null;
                var anchor = document.CreateAnchor(line.Offset);
                anchor.SurviveDeletion = true;
                var checkbox = _owner.CreateTaskCheckBox(anchor, match.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase));
                var markerLength = match.Groups[0].Length - match.Groups[3].Length;
                return new InlineObjectElement(markerLength, checkbox);
            }

            if (kind == MarkdownLineKind.Bullet)
            {
                var match = BulletLine.Match(source);
                if (!match.Success) return null;
                var markerLength = match.Groups[0].Length - match.Groups[2].Length;
                return new InlineObjectElement(markerLength, new TextBlock
                {
                    Text = "• ",
                    Foreground = _owner._foreground,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = _owner._fontFamily,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3)
            {
                var match = HeadingLine.Match(source);
                if (!match.Success) return null;
                var markerLength = match.Groups[0].Length - match.Groups[3].Length;
                return new InlineObjectElement(markerLength, new Border
                {
                    Width = 0,
                    Height = 0,
                    Background = Brushes.Transparent
                });
            }

            return null;
        }

        private static int PrefixOffset(DocumentLine line, string source, out MarkdownLineKind kind)
        {
            var image = ImageLine.Match(source);
            if (image.Success)
            {
                kind = MarkdownLineKind.Image;
                return line.Offset;
            }

            var task = TaskLine.Match(source);
            if (task.Success)
            {
                kind = MarkdownLineKind.Task;
                return line.Offset + task.Groups[1].Length;
            }

            var bullet = BulletLine.Match(source);
            if (bullet.Success)
            {
                kind = MarkdownLineKind.Bullet;
                return line.Offset + bullet.Groups[1].Length;
            }

            var heading = HeadingLine.Match(source);
            if (heading.Success)
            {
                kind = heading.Groups[2].Length switch
                {
                    1 => MarkdownLineKind.Heading1,
                    2 => MarkdownLineKind.Heading2,
                    _ => MarkdownLineKind.Heading3
                };
                return line.Offset + heading.Groups[1].Length;
            }

            kind = MarkdownLineKind.Paragraph;
            return -1;
        }
    }

    private CheckBox CreateTaskCheckBox(TextAnchor anchor, bool isChecked)
    {
        var checkbox = new CheckBox
        {
            IsChecked = isChecked,
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 7, -2),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true,
            ToolTip = "完成任务",
            Style = Application.Current?.TryFindResource("ToolkitRoundedCheckBoxStyle") as Style
        };
        checkbox.Checked += (_, _) => ToggleTask(anchor, true);
        checkbox.Unchecked += (_, _) => ToggleTask(anchor, false);
        return checkbox;
    }

    private UIElement CreateImageHost(TextAnchor anchor, int sourceLength, string altText, string relativePath)
    {
        var host = new Grid { Margin = new Thickness(0, 6, 0, 6), MinHeight = 30 };
        var image = new Image
        {
            Source = _imageLoader(relativePath),
            MaxHeight = 260,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = altText
        };
        host.Children.Add(image);
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
            Padding = new Thickness(0),
            ToolTip = "删除"
        };
        remove.Click += (_, args) =>
        {
            args.Handled = true;
            RemoveImage(anchor, sourceLength);
        };
        host.Children.Add(remove);
        return host;
    }

    private sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private readonly Brush _foreground;
        private readonly Brush _muted;
        private readonly Brush _accent;
        private readonly FontFamily _fontFamily;

        public MarkdownColorizer(Brush foreground, Brush muted, Brush accent, FontFamily fontFamily)
        {
            _foreground = foreground;
            _muted = muted;
            _accent = accent;
            _fontFamily = fontFamily;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var source = CurrentContext.Document.GetText(line.Offset, line.Length);
            var task = TaskLine.Match(source);
            if (task.Success)
            {
                var markerLength = task.Groups[0].Length - task.Groups[3].Length;
                ChangeLinePart(line.Offset + markerLength, line.EndOffset, element =>
                {
                    if (element.TextRunProperties is not VisualLineElementTextRunProperties properties) return;
                    properties.SetForegroundBrush(task.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase) ? _muted : _foreground);
                    if (task.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase)) properties.SetTextDecorations(TextDecorations.Strikethrough);
                });
                return;
            }

            var heading = HeadingLine.Match(source);
            if (heading.Success)
            {
                var size = heading.Groups[2].Length switch
                {
                    1 => 24d,
                    2 => 20d,
                    _ => 17d
                };
                ChangeLinePart(line.Offset + heading.Groups[1].Length + heading.Groups[2].Length + 1, line.EndOffset, element =>
                {
                    if (element.TextRunProperties is not VisualLineElementTextRunProperties properties) return;
                    properties.SetForegroundBrush(_foreground);
                    properties.SetFontRenderingEmSize(size);
                    properties.SetTypeface(new Typeface(_fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
                });
                return;
            }

            var bullet = BulletLine.Match(source);
            if (bullet.Success)
            {
                ChangeLinePart(line.Offset + bullet.Groups[1].Length + 2, line.EndOffset, element =>
                {
                    if (element.TextRunProperties is VisualLineElementTextRunProperties properties)
                        properties.SetForegroundBrush(_foreground);
                });
            }
        }
    }
}
