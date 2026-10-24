using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;

namespace ScrewCalendar;

/// <summary>Source editing and native reading share one AvalonEdit document, selection and undo stack.</summary>
public sealed partial class MarkdownBlockEditor : Border
{
    private readonly TextEditor _editor;
    private Brush _foreground;
    private Brush _muted;
    private Brush _surface;
    private Brush _line;
    private Brush _accent;
    private readonly FontFamily _fontFamily;
    private readonly Func<string, BitmapSource?> _imageLoader;
    private MarkdownSnapshot? _snapshot;
    private bool _loading;
    private int _savedSelectionStart;
    private int _savedSelectionLength;

    public MarkdownBlockEditor(Brush foreground, Brush muted, Brush surface, Brush line, Brush accent,
        FontFamily fontFamily, Func<string, BitmapSource?> imageLoader, Brush? selectionBrush = null)
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
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.IndentationSize = 2;
        _editor.TextArea.SelectionBrush = selectionBrush ?? CreateFallbackSelectionBrush(accent, surface);
        _editor.TextArea.SelectionForeground = foreground;
        var selectionBorder = new Pen(Brushes.Transparent, 0);
        selectionBorder.Freeze();
        _editor.TextArea.SelectionBorder = selectionBorder;
        _editor.TextArea.SelectionCornerRadius = 0;
        _editor.TextArea.Caret.CaretBrush = accent;
        if (Application.Current?.TryFindResource("ToolkitMinimalScrollBarStyle") is Style scrollBarStyle)
            _editor.Resources.Add(typeof(ScrollBar), scrollBarStyle);

        InitializePresentation();
        _editor.TextChanged += (_, _) =>
        {
            _snapshot = null;
            if (IsPreviewMode) _editor.TextArea.TextView.Redraw();
            if (!_loading) ContentChanged?.Invoke(this, EventArgs.Empty);
        };
        _editor.PreviewKeyDown += OnEditorKeyDown;
        Child = _editor;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || IsPreviewMode || _editor.IsKeyboardFocusWithin) return;
            FocusEditor();
            e.Handled = true;
        };
    }

    public event EventHandler? ContentChanged;
    public event Action<int>? EditRequested;
    public bool IsPreviewMode { get; private set; }
    public bool CanToggleTasks { get; set; } = true;
    public MarkdownSnapshot Snapshot => _snapshot ??= MarkdownDocumentService.Parse(_editor.Text);

    public void SetMarkdown(string markdown)
    {
        _loading = true;
        try
        {
            _editor.Text = markdown ?? string.Empty;
            _editor.Document.UndoStack.ClearAll();
            _editor.Select(0, 0);
            _savedSelectionStart = _savedSelectionLength = 0;
        }
        finally { _loading = false; }
    }

    /// <summary>Refresh an existing reader without replacing its document or resetting scroll/selection.</summary>
    public void UpdateMarkdown(string markdown)
    {
        var old = _editor.Text;
        if (old == markdown) return;
        var start = 0;
        while (start < old.Length && start < markdown.Length && old[start] == markdown[start]) start++;
        var oldEnd = old.Length;
        var newEnd = markdown.Length;
        while (oldEnd > start && newEnd > start && old[oldEnd - 1] == markdown[newEnd - 1]) { oldEnd--; newEnd--; }
        _loading = true;
        try { _editor.Document.Replace(start, oldEnd - start, markdown.Substring(start, newEnd - start)); }
        finally { _loading = false; }
    }

    public string GetMarkdown() => _editor.Text;

    public void UpdateAppearance(Brush foreground, Brush muted, Brush surface, Brush line, Brush accent, Brush selection)
    {
        _foreground = foreground;
        _muted = muted;
        _surface = surface;
        _line = line;
        _accent = accent;
        BorderBrush = line;
        Background = IsPreviewMode ? Brushes.Transparent : surface;
        _editor.Foreground = foreground;
        _editor.TextArea.SelectionForeground = foreground;
        _editor.TextArea.SelectionBrush = selection;
        _editor.TextArea.Caret.CaretBrush = IsPreviewMode ? Brushes.Transparent : accent;
        _editor.TextArea.TextView.Redraw();
    }

    public void DiscardDraft(string savedMarkdown)
    {
        UpdateMarkdown(savedMarkdown);
        _editor.Document.UndoStack.ClearAll();
    }

    public void SetPreviewMode(bool preview, int? caretOffset = null)
    {
        if (preview == IsPreviewMode) return;
        var top = _editor.TextArea.TextView.GetPosition(new Point(0, _editor.VerticalOffset));
        var topLine = top?.Line ?? Math.Max(1, _editor.TextArea.TextView.VisualLinesValid && _editor.TextArea.TextView.VisualLines.Count > 0
            ? _editor.TextArea.TextView.VisualLines[0].FirstDocumentLine.LineNumber : 1);
        var pixelOffset = _editor.VerticalOffset;
        if (preview)
        {
            _savedSelectionStart = _editor.SelectionStart;
            _savedSelectionLength = _editor.SelectionLength;
            _editor.Select(_editor.CaretOffset, 0);
        }
        IsPreviewMode = preview;
        _editor.IsReadOnly = preview;
        _editor.TextArea.Caret.CaretBrush = preview ? Brushes.Transparent : _accent;
        BorderThickness = preview ? new Thickness(0) : new Thickness(1);
        Padding = preview ? new Thickness(0) : new Thickness(10, 8, 10, 8);
        Background = preview ? Brushes.Transparent : _surface;
        _editor.TextArea.TextView.Redraw();
        _editor.UpdateLayout();
        if (_editor.TextArea.TextView.VisualLinesValid)
        {
            var line = _editor.Document.GetLineByNumber(Math.Clamp(topLine, 1, _editor.Document.LineCount));
            _editor.ScrollToVerticalOffset(_editor.TextArea.TextView.GetVisualTopByDocumentLine(line.LineNumber));
        }
        else _editor.ScrollToVerticalOffset(pixelOffset);
        if (!preview)
        {
            var start = Math.Clamp(caretOffset ?? _savedSelectionStart, 0, _editor.Document.TextLength);
            _editor.Select(start, caretOffset.HasValue ? 0 : Math.Min(_savedSelectionLength, _editor.Document.TextLength - start));
        }
    }

    public void FocusEditor()
    {
        _editor.Focus();
        _editor.TextArea.Focus();
    }

    public void ApplyKind(MarkdownLineKind kind)
    {
        if (IsPreviewMode || kind == MarkdownLineKind.Image) return;
        var snapshot = Snapshot;
        var start = _editor.SelectionStart;
        var last = _editor.SelectionLength == 0 ? start : start + _editor.SelectionLength - 1;
        var firstLine = snapshot.LineIndexAt(start);
        var lastLine = snapshot.LineIndexAt(last);
        _editor.Document.BeginUpdate();
        try
        {
            for (var index = lastLine; index >= firstLine; index--)
            {
                var line = snapshot.Lines[index];
                _editor.Document.Replace(line.Offset, line.Length, MarkdownDocumentService.FormatLine(snapshot, index, kind));
            }
        }
        finally { _editor.Document.EndUpdate(); }
        var first = _editor.Document.GetLineByNumber(firstLine + 1);
        var final = _editor.Document.GetLineByNumber(lastLine + 1);
        _editor.Select(first.Offset, final.EndOffset - first.Offset);
        FocusEditor();
    }

    public void AddImage(string relativePath, string altText = "image")
    {
        if (IsPreviewMode || string.IsNullOrWhiteSpace(relativePath)) return;
        var safeAlt = altText.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var path = relativePath.Replace("\\", "/", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
        var reference = $"![{safeAlt}]({path})";
        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        var prefix = "";
        var suffix = "";
        if (length == 0 && _editor.Document.TextLength > 0)
        {
            var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
            start = line.EndOffset;
            if (line.Length > 0) prefix = NewlineAt(line.LineNumber);
        }
        else if (length > 0)
        {
            if (start > 0 && _editor.Document.GetCharAt(start - 1) is not '\r' and not '\n') prefix = NewlineAt(1);
            if (start + length < _editor.Document.TextLength && _editor.Document.GetCharAt(start + length) is not '\r' and not '\n') suffix = NewlineAt(1);
        }
        _editor.Document.Replace(start, length, prefix + reference + suffix);
        _editor.Select(start + prefix.Length + reference.Length, 0);
        FocusEditor();
        _editor.TextArea.Caret.BringCaretToView();
    }

    private string NewlineAt(int lineNumber)
    {
        var line = _editor.Document.GetLineByNumber(lineNumber);
        if (line.DelimiterLength == 0) line = line.PreviousLine ?? line;
        return line.DelimiterLength > 0 ? _editor.Document.GetText(line.EndOffset, line.DelimiterLength) : Environment.NewLine;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs args)
    {
        if (!IsPreviewMode && !args.Handled && args.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleBold();
            args.Handled = true;
            return;
        }
        if (IsPreviewMode || args.Handled || args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || _editor.SelectionLength > 0) return;
        args.Handled = ContinueListOnEnter();
    }

    public void ToggleBold()
    {
        if (IsPreviewMode) return;
        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        var source = _editor.Text;
        var selected = source.Substring(start, length);
        string replacement;
        var selectionOffset = 0;
        if (start >= 2 && start + length + 2 <= source.Length &&
            source.AsSpan(start - 2, 2).SequenceEqual("**") && source.AsSpan(start + length, 2).SequenceEqual("**"))
        {
            start -= 2;
            length += 4;
            replacement = selected;
        }
        else if (selected.IndexOfAny(['\r', '\n']) < 0 && selected.Length >= 4 && selected.StartsWith("**", StringComparison.Ordinal) && selected.EndsWith("**", StringComparison.Ordinal))
        {
            replacement = selected[2..^2];
        }
        else if (length == 0)
        {
            replacement = "****";
            selectionOffset = 2;
        }
        else
        {
            // Format each physical line independently, preserving blank lines, indentation,
            // and LF/CRLF. Emphasis delimiters next to whitespace would not parse as bold.
            var lines = System.Text.RegularExpressions.Regex.Matches(selected, @"[^\r\n]+");
            var remove = lines.Select(match => match.Value.Trim()).Where(line => line.Length > 0)
                .All(line => line.Length >= 4 && line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal));
            replacement = System.Text.RegularExpressions.Regex.Replace(selected, @"[^\r\n]+", match =>
            {
                var line = match.Value;
                var content = line.Trim();
                if (content.Length == 0) return line;
                var leading = line.Length - line.TrimStart().Length;
                return line[..leading] + (remove ? content[2..^2] : "**" + content + "**") + line[(leading + content.Length)..];
            });
        }
        _editor.Document.Replace(start, length, replacement);
        _editor.Select(start + selectionOffset, length == 0 ? 0 : replacement.Length);
        FocusEditor();
    }

    internal bool ContinueListOnEnter()
    {
        var snapshot = Snapshot;
        var index = snapshot.LineIndexAt(_editor.CaretOffset);
        var line = snapshot.Lines[index];
        if (line.IsCode || line.ListMarker is not { } marker || _editor.CaretOffset < marker.ContentStart) return false;
        var outerPrefix = snapshot.Source.Substring(line.Offset, marker.Start - line.Offset);
        if (string.IsNullOrWhiteSpace(snapshot.Source.Substring(marker.ContentStart, line.Offset + line.Length - marker.ContentStart)))
        {
            _editor.Document.Remove(marker.Start, marker.ContentStart - marker.Start);
            _editor.CaretOffset = marker.Start;
            return true;
        }
        var insertion = NewlineAt(index + 1) + outerPrefix + marker.Continuation;
        var caret = _editor.CaretOffset;
        _editor.Document.Replace(caret, _editor.SelectionLength, insertion);
        _editor.CaretOffset = caret + insertion.Length;
        return true;
    }

    private static Brush CreateFallbackSelectionBrush(Brush accent, Brush surface)
    {
        if (accent is not SolidColorBrush accentBrush || surface is not SolidColorBrush surfaceBrush) return accent;
        var background = surfaceBrush.Color;
        if ((0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255 < .5) return accent;
        var color = accentBrush.Color;
        return new SolidColorBrush(Color.FromRgb(Blend(background.R, color.R), Blend(background.G, color.G), Blend(background.B, color.B)));
    }

    private static byte Blend(byte background, byte accent) => (byte)Math.Round(background + (accent - background) * .14);
}
