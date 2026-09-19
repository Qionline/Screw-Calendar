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
    }

    public event EventHandler? ContentChanged;

    public void SetMarkdown(string markdown)
    {
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
        block.Editor.GotKeyboardFocus += (_, _) => _activeBlock = block;
        block.Editor.TextChanged += (_, _) => RaiseChanged();
        block.Editor.PreviewKeyDown += (_, eventArgs) =>
        {
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
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        block.Marker = CreateMarker(block);
        row.Children.Add(block.Marker);
        Grid.SetColumn(block.Editor, 1);
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
                HorizontalAlignment = HorizontalAlignment.Center
            };
            checkbox.Checked += (_, _) => { block.IsChecked = true; ApplyEditorTypography(block); RaiseChanged(); };
            checkbox.Unchecked += (_, _) => { block.IsChecked = false; ApplyEditorTypography(block); RaiseChanged(); };
            return checkbox;
        }
        var label = block.Kind switch
        {
            MarkdownLineKind.Heading1 => "H1",
            MarkdownLineKind.Heading2 => "H2",
            MarkdownLineKind.Heading3 => "H3",
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
            Marker = null!;
        }

        public MarkdownLineKind Kind { get; set; }
        public bool IsChecked { get; set; }
        public int Indent { get; }
        public string? ImagePath { get; }
        public TextBox Editor { get; set; }
        public UIElement Host { get; set; }
        public UIElement Marker { get; set; }

        public MarkdownLine ToMarkdownLine() => new(Kind, Editor.Text, IsChecked, Indent, ImagePath);
    }
}
