using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ScrewCalendar;

public sealed partial class MarkdownBlockEditor
{
    private const double RepeatBodyPadding = 4;
    private IReadOnlyList<RepeatBlock>? _repeatPresentation;
    private IReadOnlyList<RepeatBlock> PresentedRepeats => _repeatPresentation ??= RepeatDocumentService.ValidBlocks(GetRepeatDraft());

    private void InvalidateRepeatPresentation()
    {
        _repeatPresentation = null;
        _editor.TextArea.TextView.Redraw();
    }

    private bool RendersRepeat(RepeatBlock block)
    {
        if (IsPreviewMode) return true;
        if (IsSourceMode) return false;
        var first = Snapshot.LineIndexAt(block.Start) + 1;
        var last = Snapshot.LineIndexAt(block.Start + block.Length - 1) + 1;
        return last < _firstSourceLine || first > _lastSourceLine;
    }

    private static Color RepeatColor(string? type) => type switch
    {
        "d" => Color.FromRgb(25, 145, 118),
        "m" => Color.FromRgb(147, 100, 209),
        _ => Color.FromRgb(61, 133, 218)
    };

    private static string RepeatTypeText(RepeatBlock block) => Localization.T("markdown.repeat." + block.Type);
    private static string RepeatEndText(RepeatBlock block) => block.EndDate.HasValue
        ? Localization.T("markdown.repeat.until", block.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        : Localization.T("markdown.repeat.forever");

    private UIElement CreateRepeatHeader(RepeatBlock block)
    {
        var color = RepeatColor(block.Type);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var type = new TextBlock
        {
            Text = RepeatTypeText(block),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        };
        var end = new TextBlock
        {
            Text = RepeatEndText(block),
            FontSize = 10,
            Foreground = _muted,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(end, 1);
        grid.Children.Add(type);
        grid.Children.Add(end);
        return new Border
        {
            Width = Math.Max(24, _editor.TextArea.TextView.ActualWidth - 2 + RepeatBodyPadding * 2),
            Height = 30,
            Margin = new Thickness(-RepeatBodyPadding, 0, -RepeatBodyPadding, RepeatBodyPadding),
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(75, color.R, color.G, color.B)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
            Tag = new RenderedSourceOffset(block.Start),
            ToolTip = RepeatTypeText(block) + " · " + RepeatEndText(block)
        };
    }

    internal string ReadingText(int start, int length)
    {
        var end = Math.Clamp(start + length, 0, _editor.Text.Length);
        start = Math.Clamp(start, 0, end);
        var text = new StringBuilder();
        var position = start;
        foreach (var block in PresentedRepeats)
        {
            var opening = Snapshot.Lines[Snapshot.LineIndexAt(block.Start)];
            var openingEnd = opening.Offset + opening.Length;
            foreach (var replacement in new[]
            {
                (Start: block.Start, End: openingEnd, Text: RepeatTypeText(block) + " · " + RepeatEndText(block)),
                (Start: block.ClosingStart, End: block.Start + block.Length, Text: string.Empty)
            })
            {
                if (replacement.End <= start || replacement.Start >= end) continue;
                if (replacement.Start > position) text.Append(Snapshot.PlainText(position, replacement.Start - position));
                text.Append(replacement.Text);
                position = Math.Min(end, replacement.End);
            }
        }
        if (position < end) text.Append(Snapshot.PlainText(position, end - position));
        return text.ToString();
    }

    private sealed class RepeatElements(MarkdownBlockEditor owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!owner.UsesMarkdownPresentation) return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            foreach (var block in owner.PresentedRepeats)
            {
                if (!owner.RendersRepeat(block)) continue;
                var closing = block.ClosingStart;
                if (block.Start >= startOffset && block.Start <= end) return block.Start;
                if (closing >= startOffset && closing <= end) return closing;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            foreach (var block in owner.PresentedRepeats)
            {
                if (!owner.RendersRepeat(block)) continue;
                if (block.Start == offset)
                    return new InlineObjectElement(owner.Snapshot.Lines[owner.Snapshot.LineIndexAt(offset)].Length, owner.CreateRepeatHeader(block));
                if (block.ClosingStart == offset) return new HiddenSource(RepeatBlockParser.ClosingTag.Length);
            }
            return null;
        }
    }

    private sealed class RepeatBackground(MarkdownBlockEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!owner.UsesMarkdownPresentation || !textView.VisualLinesValid) return;
            foreach (var block in owner.PresentedRepeats)
            {
                if (!owner.RendersRepeat(block)) continue;
                var lines = textView.VisualLines.Where(line => line.FirstDocumentLine.Offset < block.Start + block.Length &&
                    line.LastDocumentLine.EndOffset >= block.Start).ToArray();
                if (lines.Length == 0) continue;
                var color = RepeatColor(block.Type);
                var bounds = new Rect(-RepeatBodyPadding, lines[0].VisualTop - textView.VerticalOffset,
                    Math.Max(0, textView.ActualWidth - 1 + RepeatBodyPadding * 2), lines[^1].VisualTop + lines[^1].Height - lines[0].VisualTop);
                var alpha = (byte)Math.Round(23 * Math.Clamp(owner.SurfaceOpacity, 0, 1));
                drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), null, bounds, 5, 5);
            }
        }
    }

    private sealed class RepeatSourceStyles(MarkdownBlockEditor owner) : DocumentColorizingTransformer
    {
        // Tolerate a temporarily missing quote while the user is typing an attribute.
        private static readonly Regex Attributes = new("(?<name>\\b[a-zA-Z_][\\w-]*)\\s*=\\s*\"(?<value>[^\"\\r\\n\\]]*)", RegexOptions.CultureInvariant);
        private MarkdownSnapshot? _styledSnapshot;
        private readonly Dictionary<int, bool> _invalidTags = [];

        private void RefreshTagValidity()
        {
            if (ReferenceEquals(_styledSnapshot, owner.Snapshot)) return;
            _styledSnapshot = owner.Snapshot;
            _invalidTags.Clear();
            bool? invalid = null;
            foreach (var sourceLine in _styledSnapshot.Lines)
            {
                if (sourceLine.IsCode) continue;
                var text = owner._editor.Document.GetText(sourceLine.Offset, sourceLine.Length);
                if (RepeatBlockParser.IsOpening(text))
                {
                    invalid = !RepeatBlockParser.HasValidAttributes(text);
                    _invalidTags[sourceLine.Offset] = invalid.Value;
                }
                else if (text.StartsWith("[/repeat", StringComparison.Ordinal))
                {
                    _invalidTags[sourceLine.Offset] = invalid != false || text != RepeatBlockParser.ClosingTag;
                    invalid = null;
                }
            }
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner.IsPreviewMode || owner.Snapshot.Lines[line.LineNumber - 1].IsCode) return;
            RefreshTagValidity();
            if (!_invalidTags.TryGetValue(line.Offset, out var invalid)) return;
            if (owner.PresentedRepeats.Any(block => owner.RendersRepeat(block) &&
                (block.Start == line.Offset || block.ClosingStart == line.Offset))) return;
            var dark = owner.IsDarkSurface;
            var tagColor = invalid
                ? (dark ? Color.FromRgb(160, 128, 128) : Color.FromRgb(185, 151, 151))
                : (dark ? Color.FromRgb(137, 149, 163) : Color.FromRgb(150, 161, 173));
            var tagBrush = new SolidColorBrush(tagColor);
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(tagBrush);
                var font = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(font.FontFamily, font.Style, FontWeights.Normal, font.Stretch));
            });
            var text = owner._editor.Document.GetText(line);
            var attributes = Attributes.Matches(text).Cast<Match>().ToArray();
            var type = attributes.FirstOrDefault(attribute => attribute.Groups["name"].Value == "type")?.Groups["value"].Value;
            var foreground = new SolidColorBrush(invalid ? Color.FromRgb(239, 68, 68) : RepeatColor(type));
            foreach (var attribute in attributes)
            {
                var value = attribute.Groups["value"];
                if (value.Length == 0) continue;
                ChangeLinePart(line.Offset + value.Index, line.Offset + value.Index + value.Length, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                    var font = element.TextRunProperties.Typeface;
                    element.TextRunProperties.SetTypeface(new Typeface(font.FontFamily, font.Style, FontWeights.Bold, font.Stretch));
                });
            }
        }
    }
}
