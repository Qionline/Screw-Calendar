using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace ScrewCalendar;

public sealed class MarkdownBlockEditor : Border
{
    private readonly StackPanel _panel = new();
    private readonly List<EditorBlock> _blocks = [];
    private readonly Brush _foreground;
    private readonly Brush _muted;
    private readonly Brush _surface;
    private readonly Brush _line;
    private readonly Brush _accent;
    private readonly FontFamily _fontFamily;
    private readonly Func<string, BitmapSource?> _imageLoader;
    private EditorBlock? _activeBlock;
    // Blocks keep independent typography, while these fields bridge selection across their TextBoxes.
    private TextBox? _selectionAnchorEditor;
    private int _selectionAnchorOffset;
    private bool _dragSelectingAcrossBlocks;
    private bool _hasCrossBlockSelection;
    private int _selectionStartBlock;
    private int _selectionStartOffset;
    private int _selectionEndBlock;
    private int _selectionEndOffset;
    private bool _loading;

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
        if (Application.Current?.TryFindResource("ToolkitMinimalScrollBarStyle") is Style scrollBarStyle)
            Resources.Add(typeof(ScrollBar), scrollBarStyle);
        Child = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        PreviewMouseLeftButtonDown += FocusEditorFromBlankArea;
        PreviewMouseLeftButtonDown += TrackSelectionAnchor;
        PreviewMouseMove += ExtendSelectionAcrossBlocks;
        PreviewMouseLeftButtonUp += FinishSelectionAcrossBlocks;
        DataObject.AddPastingHandler(this, HandleCrossBlockPaste);
    }

    public event EventHandler? ContentChanged;

    public void SetMarkdown(string markdown)
    {
        ClearCrossBlockSelection();
        _selectionAnchorEditor = null;
        _loading = true;
        _panel.Children.Clear();
        _blocks.Clear();
        foreach (var line in MarkdownDocumentService.ParseLines(markdown)) AddBlock(line);
        if (_blocks.Count == 0) AddBlock(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty));
        _loading = false;
    }

    public string GetMarkdown() => MarkdownDocumentService.SerializeLines(_blocks.Select(block => block.ToMarkdownLine()));

    public void ApplyKind(MarkdownLineKind kind)
    {
        if (_hasCrossBlockSelection)
        {
            var start = _selectionStartBlock;
            var end = _selectionEndBlock;
            ClearCrossBlockSelection();
            _loading = true;
            for (var index = start; index <= end; index++)
            {
                var selected = _blocks[index];
                if (selected.Kind == MarkdownLineKind.Image) continue;
                selected.Kind = kind;
                if (kind != MarkdownLineKind.Task) selected.IsChecked = false;
                RefreshBlock(selected);
            }
            _loading = false;
            RaiseChanged();
            _blocks[Math.Clamp(start, 0, _blocks.Count - 1)].Editor.Focus();
            return;
        }

        var block = _activeBlock ?? _blocks.LastOrDefault();
        if (block is null || block.Kind == MarkdownLineKind.Image)
        {
            AddBlock(new MarkdownLine(kind, string.Empty));
            return;
        }
        block.Kind = kind;
        RefreshBlock(block);
        RaiseChanged();
        block.Editor.Focus();
    }

    public void AddImage(string relativePath, string altText = "image")
    {
        var activeIndex = _activeBlock is null ? _blocks.Count - 1 : _blocks.IndexOf(_activeBlock);
        var insertAt = Math.Clamp(activeIndex + 1, 0, _blocks.Count);
        AddBlock(new MarkdownLine(MarkdownLineKind.Image, altText, ImagePath: relativePath), insertAt);
        var imageHost = _blocks[insertAt].Host;
        if (imageHost is FrameworkElement element)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(element.BringIntoView));
    }

    public void FocusEditor()
    {
        var block = _blocks.LastOrDefault(item => item.Kind != MarkdownLineKind.Image);
        if (block is null)
        {
            AddBlock(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty));
            block = _blocks[^1];
        }
        _activeBlock = block;
        block.Editor.Focus();
        block.Editor.CaretIndex = block.Editor.Text.Length;
    }

    private void FocusEditorFromBlankArea(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left || IsInteractiveSource(eventArgs.OriginalSource as DependencyObject)) return;
        FocusEditor();
        eventArgs.Handled = true;
    }

    private void TrackSelectionAnchor(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left) return;
        var editor = FindEditor(eventArgs.OriginalSource as DependencyObject);
        if (editor is null)
        {
            _selectionAnchorEditor = null;
            return;
        }

        ClearCrossBlockSelection();
        _selectionAnchorEditor = editor;
        _selectionAnchorOffset = CharacterIndex(editor, eventArgs.GetPosition(editor));
        _dragSelectingAcrossBlocks = false;
    }

    private void ExtendSelectionAcrossBlocks(object sender, MouseEventArgs eventArgs)
    {
        if (_selectionAnchorEditor is null || Mouse.LeftButton != MouseButtonState.Pressed) return;
        var target = FindEditor(eventArgs.OriginalSource as DependencyObject) ?? FindEditor(Mouse.DirectlyOver as DependencyObject);
        if (target is null || (ReferenceEquals(target, _selectionAnchorEditor) && !_dragSelectingAcrossBlocks)) return;

        if (!_dragSelectingAcrossBlocks)
        {
            _dragSelectingAcrossBlocks = true;
            CaptureMouse();
        }

        ApplyBlockSelection(_selectionAnchorEditor, _selectionAnchorOffset, target, CharacterIndex(target, eventArgs.GetPosition(target)));
        eventArgs.Handled = true;
    }

    private void FinishSelectionAcrossBlocks(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!_dragSelectingAcrossBlocks || eventArgs.ChangedButton != MouseButton.Left) return;
        var target = FindEditor(eventArgs.OriginalSource as DependencyObject) ?? FindEditor(Mouse.DirectlyOver as DependencyObject);
        if (target is not null)
            ApplyBlockSelection(_selectionAnchorEditor!, _selectionAnchorOffset, target, CharacterIndex(target, eventArgs.GetPosition(target)));
        ReleaseMouseCapture();
        _selectionAnchorEditor = null;
        _dragSelectingAcrossBlocks = false;
        eventArgs.Handled = true;
    }

    private bool HandleSelectionCommand(KeyEventArgs eventArgs)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && eventArgs.Key == Key.A)
        {
            SelectAllBlocks();
            eventArgs.Handled = true;
            return true;
        }

        if (_hasCrossBlockSelection && control && eventArgs.Key == Key.C)
        {
            CopyCrossBlockSelection();
            eventArgs.Handled = true;
            return true;
        }

        if (_hasCrossBlockSelection && control && eventArgs.Key == Key.X)
        {
            CopyCrossBlockSelection();
            ReplaceCrossBlockSelection(string.Empty);
            eventArgs.Handled = true;
            return true;
        }

        if (_hasCrossBlockSelection && eventArgs.Key is Key.Back or Key.Delete)
        {
            ReplaceCrossBlockSelection(string.Empty);
            eventArgs.Handled = true;
            return true;
        }

        if (_hasCrossBlockSelection && eventArgs.Key == Key.Enter)
        {
            var startBlock = _blocks[_selectionStartBlock];
            var nextKind = startBlock.Kind is MarkdownLineKind.Bullet or MarkdownLineKind.Task ? startBlock.Kind : MarkdownLineKind.Paragraph;
            ReplaceCrossBlockSelection(string.Empty);
            var index = _blocks.IndexOf(startBlock) + 1;
            AddBlock(new MarkdownLine(nextKind, string.Empty, Indent: startBlock.Indent), index);
            _blocks[index].Editor.Focus();
            eventArgs.Handled = true;
            return true;
        }

        if (_hasCrossBlockSelection && eventArgs.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            ClearCrossBlockSelection();
        return false;
    }

    private void HandleCrossBlockPaste(object sender, DataObjectPastingEventArgs eventArgs)
    {
        if (!_hasCrossBlockSelection || !eventArgs.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        var text = eventArgs.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (text is null) return;
        ReplaceCrossBlockSelection(text);
        eventArgs.CancelCommand();
    }

    private void ApplyBlockSelection(TextBox anchor, int anchorOffset, TextBox target, int targetOffset)
    {
        var anchorIndex = FindBlockIndex(anchor);
        var targetIndex = FindBlockIndex(target);
        if (anchorIndex < 0 || targetIndex < 0) return;

        foreach (var block in _blocks)
            if (block.Kind != MarkdownLineKind.Image)
                block.Editor.Select(0, 0);

        var forward = anchorIndex < targetIndex || anchorIndex == targetIndex && anchorOffset <= targetOffset;
        var startIndex = forward ? anchorIndex : targetIndex;
        var endIndex = forward ? targetIndex : anchorIndex;
        var startOffset = forward ? anchorOffset : targetOffset;
        var endOffset = forward ? targetOffset : anchorOffset;

        if (startIndex == endIndex)
        {
            _blocks[startIndex].Editor.Select(startOffset, Math.Max(0, endOffset - startOffset));
            _hasCrossBlockSelection = false;
            _selectionStartBlock = startIndex;
            _selectionStartOffset = startOffset;
            _selectionEndBlock = endIndex;
            _selectionEndOffset = endOffset;
            return;
        }

        var startEditor = _blocks[startIndex].Editor;
        var endEditor = _blocks[endIndex].Editor;
        startEditor.Select(Math.Clamp(startOffset, 0, startEditor.Text.Length), Math.Max(0, startEditor.Text.Length - startOffset));
        for (var index = startIndex + 1; index < endIndex; index++)
            if (_blocks[index].Kind != MarkdownLineKind.Image)
                _blocks[index].Editor.SelectAll();
        endEditor.Select(0, Math.Clamp(endOffset, 0, endEditor.Text.Length));

        _hasCrossBlockSelection = true;
        _selectionStartBlock = startIndex;
        _selectionStartOffset = Math.Clamp(startOffset, 0, startEditor.Text.Length);
        _selectionEndBlock = endIndex;
        _selectionEndOffset = Math.Clamp(endOffset, 0, endEditor.Text.Length);
    }

    private void SelectAllBlocks()
    {
        ClearCrossBlockSelection();
        var textBlocks = _blocks
            .Select((block, index) => (block, index))
            .Where(item => item.block.Kind != MarkdownLineKind.Image)
            .ToList();
        if (textBlocks.Count == 0) return;

        foreach (var item in textBlocks)
            item.block.Editor.SelectAll();
        _selectionStartBlock = textBlocks[0].index;
        _selectionStartOffset = 0;
        _selectionEndBlock = textBlocks[^1].index;
        _selectionEndOffset = textBlocks[^1].block.Editor.Text.Length;
        _hasCrossBlockSelection = textBlocks.Count > 1;
    }

    private void CopyCrossBlockSelection()
    {
        if (!_hasCrossBlockSelection) return;
        var lines = new List<string>();
        for (var index = _selectionStartBlock; index <= _selectionEndBlock; index++)
        {
            var block = _blocks[index];
            if (block.Editor is null) continue;
            var start = index == _selectionStartBlock ? _selectionStartOffset : 0;
            var end = index == _selectionEndBlock ? _selectionEndOffset : block.Editor.Text.Length;
            if (end <= start) continue;
            var selected = block.ToMarkdownLine() with { Text = block.Editor.Text[start..end] };
            lines.Add(MarkdownDocumentService.SerializeLines(new[] { selected }));
        }
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void ReplaceCrossBlockSelection(string replacement)
    {
        if (!_hasCrossBlockSelection) return;
        var startBlock = _blocks[_selectionStartBlock];
        var endBlock = _blocks[_selectionEndBlock];
        var prefix = startBlock.Editor.Text[.._selectionStartOffset];
        var suffix = endBlock.Editor.Text[_selectionEndOffset..];
        var replacementLines = replacement.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        _loading = true;
        for (var index = _selectionEndBlock; index > _selectionStartBlock; index--)
        {
            _blocks.RemoveAt(index);
            _panel.Children.RemoveAt(index);
        }

        startBlock.Editor.Text = prefix + replacementLines[0] + (replacementLines.Length == 1 ? suffix : string.Empty);
        var caretBlock = startBlock;
        var caret = prefix.Length + replacementLines[0].Length;
        var insertIndex = _selectionStartBlock + 1;
        for (var lineIndex = 1; lineIndex < replacementLines.Length; lineIndex++)
        {
            var line = replacementLines[lineIndex] + (lineIndex == replacementLines.Length - 1 ? suffix : string.Empty);
            var newBlock = new MarkdownLine(startBlock.Kind, line, Indent: startBlock.Indent);
            AddBlock(newBlock, insertIndex++);
            caretBlock = _blocks[insertIndex - 1];
            caret = replacementLines[lineIndex].Length;
        }
        _loading = false;
        _hasCrossBlockSelection = false;
        _selectionAnchorEditor = null;
        caretBlock.Editor.Focus();
        caretBlock.Editor.CaretIndex = Math.Clamp(caret, 0, caretBlock.Editor.Text.Length);
        RaiseChanged();
    }

    private void ClearCrossBlockSelection()
    {
        if (!_hasCrossBlockSelection) return;
        foreach (var block in _blocks)
            if (block.Kind != MarkdownLineKind.Image)
                block.Editor.Select(0, 0);
        _hasCrossBlockSelection = false;
    }

    private int FindBlockIndex(TextBox editor) => _blocks.FindIndex(block => ReferenceEquals(block.Editor, editor));

    private static TextBox? FindEditor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox editor) return editor;
            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private static int CharacterIndex(TextBox editor, Point point)
    {
        var index = editor.GetCharacterIndexFromPoint(point, false);
        return Math.Clamp(index < 0 ? editor.Text.Length : index, 0, editor.Text.Length);
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox or ButtonBase or ScrollBar) return true;
            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void AddBlock(MarkdownLine line, int? insertAt = null)
    {
        var block = new EditorBlock(line);
        block.Editor = new TextBox
        {
            Text = line.Text,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = _foreground,
            FontFamily = _fontFamily,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false
        };
        block.Editor.IsInactiveSelectionHighlightEnabled = true;
        block.Editor.SelectionBrush = _accent;
        block.Editor.SelectionOpacity = .35;
        block.Editor.GotKeyboardFocus += (_, _) => _activeBlock = block;
        block.Editor.TextChanged += (_, _) => RaiseChanged();
        block.Editor.PreviewTextInput += (_, eventArgs) =>
        {
            if (!_hasCrossBlockSelection) return;
            ReplaceCrossBlockSelection(eventArgs.Text);
            eventArgs.Handled = true;
        };
        block.Editor.PreviewKeyDown += (_, eventArgs) =>
        {
            if (HandleSelectionCommand(eventArgs)) return;
            if (eventArgs.Key == Key.Enter)
            {
                eventArgs.Handled = true;
                var index = _blocks.IndexOf(block) + 1;
                var nextKind = block.Kind is MarkdownLineKind.Bullet or MarkdownLineKind.Task ? block.Kind : MarkdownLineKind.Paragraph;
                AddBlock(new MarkdownLine(nextKind, string.Empty, Indent: block.Indent), index);
                _blocks[index].Editor.Focus();
                return;
            }
            if (eventArgs.Key != Key.Back || block.Editor.Text.Length != 0 || _blocks.Count <= 1) return;
            eventArgs.Handled = true;
            var currentIndex = _blocks.IndexOf(block);
            _blocks.RemoveAt(currentIndex);
            _panel.Children.RemoveAt(currentIndex);
            var previous = _blocks[Math.Max(0, currentIndex - 1)].Editor;
            previous.Focus();
            previous.CaretIndex = previous.Text.Length;
            RaiseChanged();
        };
        block.Host = CreateHost(block);
        var targetIndex = insertAt ?? _blocks.Count;
        _blocks.Insert(targetIndex, block);
        _panel.Children.Insert(targetIndex, block.Host);
        if (!_loading) RaiseChanged();
    }

    private UIElement CreateHost(EditorBlock block)
    {
        if (block.Kind == MarkdownLineKind.Image) return CreateImageHost(block);
        var row = new Grid { Margin = new Thickness(block.Indent * 18, 1, 0, 1), MinHeight = 32 };
        var hasMarker = block.Kind is MarkdownLineKind.Bullet or MarkdownLineKind.Task;
        if (hasMarker)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.Marker = CreateMarker(block);
            row.Children.Add(block.Marker);
            Grid.SetColumn(block.Editor, 1);
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            block.Marker = null;
            Grid.SetColumn(block.Editor, 0);
        }
        row.Children.Add(block.Editor);
        ApplyEditorTypography(block);
        return row;
    }

    private UIElement CreateMarker(EditorBlock block)
    {
        if (block.Kind == MarkdownLineKind.Task)
        {
            var checkbox = new CheckBox
            {
                IsChecked = block.IsChecked,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = Application.Current?.TryFindResource("ToolkitRoundedCheckBoxStyle") as Style
            };
            checkbox.Checked += (_, _) => { block.IsChecked = true; ApplyEditorTypography(block); RaiseChanged(); };
            checkbox.Unchecked += (_, _) => { block.IsChecked = false; ApplyEditorTypography(block); RaiseChanged(); };
            return checkbox;
        }
        var label = block.Kind switch
        {
            MarkdownLineKind.Bullet => "•",
            _ => string.Empty
        };
        return new TextBlock
        {
            Text = label,
            Foreground = block.Kind == MarkdownLineKind.Bullet ? _accent : _muted,
            FontSize = block.Kind == MarkdownLineKind.Bullet ? 17 : 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private UIElement CreateImageHost(EditorBlock block)
    {
        var host = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        var image = new Image
        {
            Source = block.ImagePath is null ? null : _imageLoader(block.ImagePath),
            MaxHeight = 260,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch
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
            Cursor = Cursors.Hand
        };
        remove.Click += (_, _) =>
        {
            var index = _blocks.IndexOf(block);
            if (index < 0) return;
            _blocks.RemoveAt(index);
            _panel.Children.RemoveAt(index);
            if (_blocks.Count == 0) AddBlock(new MarkdownLine(MarkdownLineKind.Paragraph, string.Empty));
            RaiseChanged();
        };
        host.Children.Add(remove);
        return host;
    }

    private void RefreshBlock(EditorBlock block)
    {
        var index = _blocks.IndexOf(block);
        if (index < 0) return;
        if (block.Editor.Parent is Panel previousParent)
            previousParent.Children.Remove(block.Editor);
        _panel.Children.RemoveAt(index);
        block.Host = CreateHost(block);
        _panel.Children.Insert(index, block.Host);
    }

    private void ApplyEditorTypography(EditorBlock block)
    {
        block.Editor.FontSize = block.Kind switch
        {
            MarkdownLineKind.Heading1 => 24,
            MarkdownLineKind.Heading2 => 20,
            MarkdownLineKind.Heading3 => 17,
            _ => 14
        };
        block.Editor.FontWeight = block.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? FontWeights.SemiBold : FontWeights.Normal;
        block.Editor.Foreground = block.Kind == MarkdownLineKind.Task && block.IsChecked ? _muted : _foreground;
        block.Editor.TextDecorations = block.Kind == MarkdownLineKind.Task && block.IsChecked ? TextDecorations.Strikethrough : null;
    }

    private void RaiseChanged()
    {
        if (!_loading) ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class EditorBlock
    {
        public EditorBlock(MarkdownLine line)
        {
            Kind = line.Kind;
            IsChecked = line.IsChecked;
            Indent = line.Indent;
            ImagePath = line.ImagePath;
            Editor = null!;
            Host = null!;
            Marker = null;
        }

        public MarkdownLineKind Kind { get; set; }
        public bool IsChecked { get; set; }
        public int Indent { get; }
        public string? ImagePath { get; }
        public TextBox Editor { get; set; }
        public UIElement Host { get; set; }
        public UIElement? Marker { get; set; }

        public MarkdownLine ToMarkdownLine() => new(Kind, Editor.Text, IsChecked, Indent, ImagePath);
    }
}
