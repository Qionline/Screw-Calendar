using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ScrewCalendar;

public sealed partial class MarkdownBlockEditor
{
    private Point? _previewMouseDown;

    private void InitializePresentation()
    {
        var view = _editor.TextArea.TextView;
        view.ElementGenerators.Add(new ReadingElements(this));
        view.LineTransformers.Add(new ReadingStyles(this));
        view.BackgroundRenderers.Add(new CodeBackground(this));
        view.SizeChanged += (_, args) =>
        {
            if (IsPreviewMode && Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > .5) view.Redraw();
        };
        DataObject.AddCopyingHandler(_editor, (_, args) =>
        {
            if (!IsPreviewMode || args.IsDragDrop) return;
            var text = Snapshot.PlainText(_editor.SelectionStart, _editor.SelectionLength);
            args.DataObject.SetData(DataFormats.UnicodeText, text);
            args.DataObject.SetData(DataFormats.Text, text);
            args.DataObject.SetData(DataFormats.StringFormat, text);
            if (args.DataObject is DataObject data)
                ICSharpCode.AvalonEdit.Highlighting.HtmlClipboard.SetHtml(data, "<pre>" + System.Net.WebUtility.HtmlEncode(text) + "</pre>");
        });
        _editor.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _previewMouseDown = IsPreviewMode && !IsInteractive(args.OriginalSource) ? args.GetPosition(_editor) : null;
        };
        _editor.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (!IsPreviewMode || _previewMouseDown is not { } down) return;
            _previewMouseDown = null;
            var point = args.GetPosition(_editor);
            if ((point - down).Length > 4 || _editor.SelectionLength > 0 || IsInteractive(args.OriginalSource)) return;
            var position = _editor.GetPositionFromPoint(point);
            var offset = position.HasValue ? _editor.Document.GetOffset(position.Value.Location) : _editor.Document.TextLength;
            var imageOffset = ImageOffset(args.OriginalSource);
            if (imageOffset.HasValue) offset = imageOffset.Value;
            var link = Snapshot.Links.FirstOrDefault(item => offset >= item.Start && offset < item.Start + item.Length);
            if (link is not null && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception exception) { AppLogger.Error("Failed to open a Markdown link.", exception); }
                args.Handled = true;
                return;
            }
            EditRequested?.Invoke(offset);
        }), handledEventsToo: true);
        _editor.PreviewMouseMove += (_, args) =>
        {
            if (!IsPreviewMode) return;
            var position = _editor.GetPositionFromPoint(args.GetPosition(_editor));
            var offset = position.HasValue ? _editor.Document.GetOffset(position.Value.Location) : -1;
            var link = Snapshot.Links.FirstOrDefault(item => offset >= item.Start && offset < item.Start + item.Length);
            view.Cursor = link is null ? Cursors.IBeam : Cursors.Hand;
            view.ToolTip = link?.Url;
        };
    }

    private static bool IsInteractive(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = GetElementParent(current))
            if (current is ButtonBase or ScrollBar or Thumb) return true;
        return false;
    }

    private static int? ImageOffset(object source)
    {
        for (var current = source as DependencyObject; current is not null; current = GetElementParent(current))
            if (current is FrameworkElement { Tag: ImageSourceOffset offset }) return offset.Offset;
        return null;
    }

    private static DependencyObject? GetElementParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private sealed record ImageSourceOffset(int Offset);

    private double AvailableWidth(int offset)
    {
        var line = _editor.Document.GetLineByOffset(offset);
        return Math.Max(24, _editor.TextArea.TextView.ActualWidth - 8 - (offset - line.Offset) * 7);
    }

    private UIElement CreateTask(MarkdownReplacement replacement)
    {
        var snapshot = Snapshot;
        var task = snapshot.Tasks[replacement.TaskIndex];
        var checkbox = new CheckBox
        {
            IsChecked = task.IsChecked,
            IsEnabled = CanToggleTasks,
            Focusable = false,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Style = Application.Current?.TryFindResource("ToolkitRoundedCheckBoxStyle") as Style,
            ToolTip = Localization.T("todo.complete")
        };
        // AvalonEdit arranges inline objects at the text-line top and otherwise treats
        // their full height as the baseline. Use a font-height host to center the box,
        // and publish its text baseline so it doesn't push the adjacent text downward.
        var view = _editor.TextArea.TextView;
        const double verticalSpacing = 2;
        var contentHeight = Math.Max(checkbox.Height, view.DefaultLineHeight);
        var host = new Border
        {
            Height = contentHeight + verticalSpacing * 2,
            Padding = new Thickness(0, verticalSpacing, 0, verticalSpacing),
            Child = checkbox
        };
        TextBlock.SetBaselineOffset(host, verticalSpacing + view.DefaultBaseline + (contentHeight - view.DefaultLineHeight) / 2);
        void Toggle(bool completed)
        {
            if (!IsPreviewMode || !CanToggleTasks || Snapshot.Source != snapshot.Source) return;
            _editor.Document.Replace(task.MarkerOffset, 1, completed ? "x" : " ");
        }
        checkbox.Checked += (_, _) => Toggle(true);
        checkbox.Unchecked += (_, _) => Toggle(false);
        return host;
    }

    private UIElement CreateImage(MarkdownReplacement replacement)
    {
        var bitmap = _imageLoader(replacement.Text);
        var width = AvailableWidth(replacement.Start);
        if (bitmap is null)
            return new TextBlock { Text = Localization.T("markdown.imageMissing"), Foreground = _muted, ToolTip = replacement.Text, Tag = new ImageSourceOffset(replacement.Start) };
        return new Image
        {
            Source = bitmap,
            Width = Math.Min(width, bitmap.Width),
            MaxHeight = 320,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(0, 5, 0, 5),
            ToolTip = replacement.Label,
            Tag = new ImageSourceOffset(replacement.Start)
        };
    }

    private sealed class ReadingElements : VisualLineElementGenerator
    {
        private readonly MarkdownBlockEditor _owner;
        public ReadingElements(MarkdownBlockEditor owner) => _owner = owner;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!_owner.IsPreviewMode) return -1;
            var snapshot = _owner.Snapshot;
            var first = snapshot.LineIndexAt(startOffset);
            var last = Math.Min(snapshot.Lines.Count - 1, CurrentContext.VisualLine.LastDocumentLine.LineNumber - 1);
            for (var i = first; i <= last; i++)
                foreach (var replacement in snapshot.Lines[i].Replacements)
                    if (replacement.Start >= startOffset) return replacement.Start;
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (!_owner.IsPreviewMode) return null;
            var snapshot = _owner.Snapshot;
            var line = snapshot.Lines[snapshot.LineIndexAt(offset)];
            var replacement = line.Replacements.FirstOrDefault(item => item.Start == offset);
            if (replacement is null) return null;
            return replacement.Kind switch
            {
                MarkdownReplacementKind.Hidden => new HiddenSource(replacement.Length),
                MarkdownReplacementKind.Task => new InlineObjectElement(replacement.Length, _owner.CreateTask(replacement)),
                MarkdownReplacementKind.Image => new InlineObjectElement(replacement.Length, _owner.CreateImage(replacement)),
                MarkdownReplacementKind.Rule => new InlineObjectElement(replacement.Length, new Border
                {
                    Width = _owner.AvailableWidth(offset),
                    Height = 18,
                    Child = new Border { Height = 1, Background = _owner._line, VerticalAlignment = VerticalAlignment.Center }
                }),
                _ => new FormattedTextElement(replacement.Text, replacement.Length)
            };
        }
    }

    private sealed class HiddenSource : VisualLineElement
    {
        public HiddenSource(int sourceLength) : base(1, sourceLength) { }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new TextHidden(1);
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn + (relativeTextOffset > 0 ? 1 : 0);
        public override int GetRelativeOffset(int visualColumn) => RelativeTextOffset + (visualColumn > VisualColumn ? DocumentLength : 0);
    }

    private sealed class ReadingStyles : DocumentColorizingTransformer
    {
        private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
        private readonly MarkdownBlockEditor _owner;
        public ReadingStyles(MarkdownBlockEditor owner) => _owner = owner;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (!_owner.IsPreviewMode) return;
            var snapshot = _owner.Snapshot;
            var info = snapshot.Lines[line.LineNumber - 1];
            if (info.HeadingLevel > 0)
                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetFontRenderingEmSize(info.HeadingLevel switch { 1 => 24, 2 => 21, 3 => 18, 4 => 16, _ => 14 });
                    element.TextRunProperties.SetTypeface(new Typeface(_owner._fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
                });
            if (info.IsCode)
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetTypeface(new Typeface(CodeFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)));
            if (info.IsSyntaxLine)
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetFontRenderingEmSize(1));
            foreach (var task in info.Replacements.Where(item => item.Kind == MarkdownReplacementKind.Task && snapshot.Tasks[item.TaskIndex].IsChecked))
                ChangeLinePart(task.Start + task.Length, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(_owner._muted);
                    element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                });
            foreach (var span in info.Styles)
            {
                var start = Math.Max(line.Offset, span.Start);
                var end = Math.Min(line.EndOffset, span.Start + span.Length);
                if (end <= start) continue;
                ChangeLinePart(start, end, element =>
                {
                    var properties = element.TextRunProperties;
                    var typeface = properties.Typeface;
                    properties.SetTypeface(new Typeface(span.Style.HasFlag(MarkdownTextStyle.Code) ? CodeFont : typeface.FontFamily,
                        span.Style.HasFlag(MarkdownTextStyle.Italic) ? FontStyles.Italic : typeface.Style,
                        span.Style.HasFlag(MarkdownTextStyle.Bold) ? FontWeights.Bold : typeface.Weight, typeface.Stretch));
                    if (span.Style.HasFlag(MarkdownTextStyle.Code)) properties.SetBackgroundBrush(_owner._surface);
                    if (span.Style.HasFlag(MarkdownTextStyle.Strike)) properties.SetTextDecorations(TextDecorations.Strikethrough);
                    if (span.Style.HasFlag(MarkdownTextStyle.Link)) properties.SetForegroundBrush(_owner._accent);
                });
            }
        }
    }

    private sealed class CodeBackground : IBackgroundRenderer
    {
        private readonly MarkdownBlockEditor _owner;
        public CodeBackground(MarkdownBlockEditor owner) => _owner = owner;
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!_owner.IsPreviewMode || !textView.VisualLinesValid) return;
            var snapshot = _owner.Snapshot;
            foreach (var visual in textView.VisualLines)
            {
                if (!snapshot.Lines[visual.FirstDocumentLine.LineNumber - 1].IsCode) continue;
                drawingContext.DrawRectangle(_owner._surface, null,
                    new Rect(0, visual.VisualTop - textView.VerticalOffset, Math.Max(0, textView.ActualWidth), visual.Height));
            }
        }
    }
}
