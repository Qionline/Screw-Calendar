using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ScrewCalendar;

[Flags]
public enum MarkdownTextStyle { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8, Link = 16 }
public enum MarkdownReplacementKind { Hidden, Text, Bullet, Task, Image, Rule, Quote }
public sealed record MarkdownReplacement(int Start, int Length, MarkdownReplacementKind Kind, string Text = "", int TaskIndex = -1, string Label = "");
public sealed record MarkdownStyleSpan(int Start, int Length, MarkdownTextStyle Style);
public sealed record MarkdownLink(int Start, int Length, string Url);
public sealed record MarkdownTask(int MarkerOffset, bool IsChecked);
public sealed record MarkdownListMarker(int Start, int Length, int ContentStart, string Continuation, bool IsTask);

public sealed class MarkdownSourceLine
{
    public int Offset { get; init; }
    public int Length { get; init; }
    public int DelimiterLength { get; init; }
    public int HeadingLevel { get; internal set; }
    public int QuoteDepth { get; internal set; }
    public bool IsCode { get; internal set; }
    public bool IsSyntaxLine { get; internal set; }
    public MarkdownListMarker? ListMarker { get; internal set; }
    public IReadOnlyList<MarkdownReplacement> Replacements { get; internal set; } = Array.Empty<MarkdownReplacement>();
    public IReadOnlyList<MarkdownStyleSpan> Styles { get; internal set; } = Array.Empty<MarkdownStyleSpan>();
}

/// <summary>
/// UI-independent, source-positioned presentation data from one full Markdig parse.
/// Reading never serializes an AST back into the user's source. Unknown syntax stays visible.
/// </summary>
public sealed class MarkdownSnapshot
{
    private readonly List<MarkdownReplacement> _replacements = new();
    private readonly List<MarkdownStyleSpan> _styles = new();
    private readonly List<MarkdownTask> _tasks = new();
    private readonly List<MarkdownLink> _links = new();

    internal MarkdownSnapshot(string source)
    {
        Source = source;
        var lines = new List<MarkdownSourceLine>();
        var offset = 0;
        while (offset < source.Length)
        {
            var end = offset;
            while (end < source.Length && source[end] is not '\r' and not '\n') end++;
            var delimiter = end == source.Length ? 0 : source[end] == '\r' && end + 1 < source.Length && source[end + 1] == '\n' ? 2 : 1;
            lines.Add(new MarkdownSourceLine { Offset = offset, Length = end - offset, DelimiterLength = delimiter });
            offset = end + delimiter;
        }
        if (lines.Count == 0 || lines[^1].DelimiterLength > 0)
            lines.Add(new MarkdownSourceLine { Offset = source.Length });
        Lines = lines;

        VisitBlocks(Markdig.Markdown.Parse(source, MarkdownDocumentService.Pipeline));
        AddQuoteMarkers();
        IndexPresentation();
    }

    public string Source { get; }
    public IReadOnlyList<MarkdownSourceLine> Lines { get; }
    public IReadOnlyList<MarkdownTask> Tasks => _tasks;
    public IReadOnlyList<MarkdownLink> Links => _links;

    public int LineIndexAt(int offset)
    {
        offset = Math.Clamp(offset, 0, Source.Length);
        var low = 0;
        var high = Lines.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Lines[mid].Offset <= offset) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    public IReadOnlyList<string> SummaryLines(int maximum = 3)
    {
        var result = new List<string>();
        foreach (var line in Lines)
        {
            if (line.IsSyntaxLine) continue;
            var text = new StringBuilder();
            var position = line.Offset;
            foreach (var replacement in line.Replacements)
            {
                text.Append(Source, position, replacement.Start - position);
                text.Append(replacement.Kind switch
                {
                    MarkdownReplacementKind.Image => $"[{replacement.Label}]",
                    MarkdownReplacementKind.Rule => "────────",
                    MarkdownReplacementKind.Task => _tasks[replacement.TaskIndex].IsChecked ? "☑ " : "☐ ",
                    _ => replacement.Text
                });
                position = replacement.Start + replacement.Length;
            }
            text.Append(Source, position, line.Offset + line.Length - position);
            var value = text.ToString().Trim();
            if (value.Length == 0) continue;
            result.Add(value);
            if (result.Count >= maximum) break;
        }
        return result;
    }

    public string PlainText(int start, int length)
    {
        var end = Math.Clamp(start + length, 0, Source.Length);
        start = Math.Clamp(start, 0, end);
        var result = new StringBuilder();
        var position = start;
        for (var i = LineIndexAt(start); i < Lines.Count && Lines[i].Offset < end; i++)
        {
            foreach (var replacement in Lines[i].Replacements)
            {
                var replacementEnd = replacement.Start + replacement.Length;
                if (replacementEnd <= start || replacement.Start >= end) continue;
                if (replacement.Start > position) result.Append(Source, position, replacement.Start - position);
                result.Append(replacement.Kind switch
                {
                    MarkdownReplacementKind.Task => _tasks[replacement.TaskIndex].IsChecked ? "☑" : "☐",
                    MarkdownReplacementKind.Image => replacement.Label,
                    MarkdownReplacementKind.Rule => "────────",
                    _ => replacement.Text
                });
                position = Math.Min(end, replacementEnd);
            }
        }
        if (position < end) result.Append(Source, position, end - position);
        return result.ToString();
    }

    private void VisitBlocks(ContainerBlock container)
    {
        foreach (var block in container)
        {
            var first = LineIndexAt(block.Span.Start);
            var last = LineIndexAt(block.Span.End);
            switch (block)
            {
                case HeadingBlock heading:
                    // Desktop notes treat a dash-only line as a separator even immediately
                    // after text. Markdig classifies that pair as a Setext H2; retain its
                    // inline content, but present ordinary text followed by a horizontal rule.
                    if (heading.IsSetext && heading.Level == 2)
                    {
                        var marker = Source.IndexOf('-', Lines[last].Offset, Lines[last].Length);
                        if (marker >= 0 && marker + 3 <= Lines[last].Offset + Lines[last].Length && Source.AsSpan(marker, 3).SequenceEqual("---"))
                        {
                            Add(marker, Lines[last].Offset + Lines[last].Length - marker, MarkdownReplacementKind.Rule);
                            break;
                        }
                    }
                    for (var i = first; i <= last; i++) Lines[i].HeadingLevel = heading.Level;
                    if (heading.IsSetext)
                    {
                        Lines[last].IsSyntaxLine = true;
                        Hide(Lines[last].Offset, Lines[last].Length);
                    }
                    else if (heading.Inline is { FirstChild: not null } inline)
                    {
                        Hide(heading.Span.Start, inline.FirstChild.Span.Start - heading.Span.Start);
                        Hide(inline.LastChild!.Span.End + 1, heading.Span.End - inline.LastChild.Span.End);
                    }
                    break;
                case ThematicBreakBlock:
                    Add(block.Span.Start, block.Span.Length, MarkdownReplacementKind.Rule);
                    break;
                case CodeBlock code:
                    for (var i = first; i <= last; i++) Lines[i].IsCode = true;
                    if (code is FencedCodeBlock fence)
                    {
                        HideFence(first, block.Span.Start);
                        if (fence.ClosingFencedCharCount > 0 && last > first)
                            HideFence(last, Lines[last].Offset);
                    }
                    break;
                case QuoteBlock:
                    for (var i = first; i <= last; i++) Lines[i].QuoteDepth++;
                    break;
                case ListItemBlock item:
                    AddListMarker(item, first);
                    break;
            }
            if (block is LeafBlock { Inline: not null } leaf) VisitInlines(leaf.Inline);
            if (block is ContainerBlock child) VisitBlocks(child);
        }
    }

    private void HideFence(int index, int start)
    {
        var line = Lines[index];
        line.IsSyntaxLine = true;
        Hide(start, line.Offset + line.Length - start);
    }

    private void AddListMarker(ListItemBlock item, int lineIndex)
    {
        var line = Lines[lineIndex];
        var start = item.Span.Start;
        var end = start;
        while (end < line.Offset + line.Length && !char.IsWhiteSpace(Source[end])) end++;
        while (end < line.Offset + line.Length && Source[end] is ' ' or '\t') end++;
        var marker = Source[start..end];
        var task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList taskList } ? taskList : null;
        var continuation = marker;
        if (item.Parent is ListBlock { IsOrdered: true } && int.TryParse(marker.TrimEnd().TrimEnd('.', ')'), out var order))
            continuation = (order + 1) + marker[marker.IndexOfAny(['.', ')'])..];
        if (task is not null)
        {
            var taskIndex = _tasks.Count;
            _tasks.Add(new MarkdownTask(task.Span.Start + 1, task.Checked));
            Add(start, task.Span.End + 1 - start, MarkdownReplacementKind.Task, taskIndex: taskIndex);
            end = task.Span.End + 1;
            while (end < line.Offset + line.Length && Source[end] is ' ' or '\t') end++;
            continuation += "[ ] ";
        }
        else
        {
            Add(start, end - start, MarkdownReplacementKind.Bullet,
                item.Parent is ListBlock { IsOrdered: true } ? marker : "• ");
        }
        line.ListMarker = new MarkdownListMarker(start, end - start, end, continuation, task is not null);
    }

    private void VisitInlines(ContainerInline container)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            var start = inline.Span.Start;
            var length = inline.Span.Length;
            switch (inline)
            {
                case EmphasisInline emphasis:
                    Hide(start, emphasis.DelimiterCount);
                    Hide(inline.Span.End + 1 - emphasis.DelimiterCount, emphasis.DelimiterCount);
                    var style = emphasis.DelimiterChar == '~' ? MarkdownTextStyle.Strike
                        : emphasis.DelimiterCount >= 2 ? MarkdownTextStyle.Bold : MarkdownTextStyle.Italic;
                    _styles.Add(new MarkdownStyleSpan(start, length, style));
                    break;
                case CodeInline code:
                    Hide(start, code.DelimiterCount);
                    Hide(inline.Span.End + 1 - code.DelimiterCount, code.DelimiterCount);
                    _styles.Add(new MarkdownStyleSpan(start, length, MarkdownTextStyle.Code));
                    var contentStart = start + code.DelimiterCount;
                    var contentLength = length - code.DelimiterCount * 2;
                    if (contentLength > 0 && LineIndexAt(contentStart) == LineIndexAt(contentStart + contentLength - 1) &&
                        Source.Substring(contentStart, contentLength) != code.Content)
                        Add(contentStart, contentLength, MarkdownReplacementKind.Text, code.Content);
                    break;
                case LinkInline link when link.IsImage:
                    if (LineIndexAt(start) == LineIndexAt(inline.Span.End))
                        Add(start, length, MarkdownReplacementKind.Image, link.Url ?? "", label: InlineLabel(link));
                    else
                        continue; // Preserve unsupported multiline image source verbatim.
                    continue;
                case LinkInline link:
                    if (link.FirstChild is not null)
                    {
                        Hide(start, link.FirstChild.Span.Start - start);
                        Hide(link.LastChild!.Span.End + 1, inline.Span.End - link.LastChild.Span.End);
                    }
                    _styles.Add(new MarkdownStyleSpan(start, length, MarkdownTextStyle.Link));
                    _links.Add(new MarkdownLink(start, length, link.Url ?? ""));
                    break;
                case AutolinkInline auto:
                    Hide(start, 1);
                    Hide(inline.Span.End, 1);
                    _styles.Add(new MarkdownStyleSpan(start, length, MarkdownTextStyle.Link));
                    _links.Add(new MarkdownLink(start, length, auto.IsEmail ? "mailto:" + auto.Url : auto.Url));
                    break;
                case LiteralInline literal when length > 0:
                    var content = literal.Content.ToString();
                    if (LineIndexAt(start) == LineIndexAt(inline.Span.End) && Source.Substring(start, length) != content)
                        AddLiteral(start, length, content);
                    break;
                case HtmlEntityInline entity:
                    Add(start, length, MarkdownReplacementKind.Text, entity.Transcoded.ToString());
                    break;
                case LineBreakInline lineBreak when lineBreak.IsHard:
                    Hide(start, length);
                    break;
            }
            if (inline is ContainerInline nested) VisitInlines(nested);
        }
    }

    private void AddQuoteMarkers()
    {
        foreach (var line in Lines)
        {
            var remaining = line.QuoteDepth;
            var index = line.Offset;
            var end = line.Offset + line.Length;
            while (remaining > 0 && index < end)
            {
                if (Source[index] is ' ' or '\t') { index++; continue; }
                if (Source[index] == '>')
                {
                    var length = index + 1 < end && Source[index + 1] == ' ' ? 2 : 1;
                    Add(index, length, MarkdownReplacementKind.Quote, "│ ");
                    remaining--;
                    index += length;
                    continue;
                }
                // An outer list may precede a nested quote on the same physical line.
                if (Source[index] is '-' or '+' or '*' || char.IsDigit(Source[index]))
                {
                    while (index < end && Source[index] is not ' ' and not '\t') index++;
                    continue;
                }
                break; // Lazy quote continuation has no literal marker to replace.
            }
        }
    }

    private void Hide(int start, int length) => Add(start, length, MarkdownReplacementKind.Hidden);

    private void AddLiteral(int start, int length, string decoded)
    {
        // Hide just escape characters, keeping the actual text in AvalonEdit's normal
        // line breaker. Replacing a whole literal would make long escaped paragraphs atomic.
        var escapes = new List<int>();
        var sourceOffset = start;
        var decodedOffset = 0;
        var end = start + length;
        while (sourceOffset < end && decodedOffset < decoded.Length)
        {
            if (Source[sourceOffset] == '\\' && sourceOffset + 1 < end && Source[sourceOffset + 1] == decoded[decodedOffset])
            {
                escapes.Add(sourceOffset++);
            }
            if (Source[sourceOffset] != decoded[decodedOffset]) break;
            sourceOffset++;
            decodedOffset++;
        }
        if (sourceOffset == end && decodedOffset == decoded.Length)
        {
            foreach (var offset in escapes) Hide(offset, 1);
        }
        else Add(start, length, MarkdownReplacementKind.Text, decoded);
    }

    private static string InlineLabel(ContainerInline inline)
    {
        var text = new StringBuilder();
        for (var child = inline.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LiteralInline literal) text.Append(literal.Content);
            else if (child is ContainerInline nested) text.Append(InlineLabel(nested));
        }
        return text.ToString();
    }

    private void Add(int start, int length, MarkdownReplacementKind kind, string text = "", int taskIndex = -1, string label = "")
    {
        if (start < 0 || length <= 0 || start + length > Source.Length) return;
        var end = start + length;
        for (var i = LineIndexAt(start); i < Lines.Count && Lines[i].Offset < end; i++)
        {
            var line = Lines[i];
            var partStart = Math.Max(start, line.Offset);
            var partEnd = Math.Min(end, line.Offset + line.Length);
            if (partEnd > partStart) _replacements.Add(new MarkdownReplacement(partStart, partEnd - partStart, kind, text, taskIndex, label));
        }
    }

    private void IndexPresentation()
    {
        var replacements = _replacements.OrderBy(item => item.Start).ThenByDescending(item => item.Length).ToArray();
        var cursor = 0;
        var styleLines = new List<MarkdownStyleSpan>[Lines.Count];
        foreach (var style in _styles)
        {
            var end = style.Start + style.Length;
            for (var i = LineIndexAt(style.Start); i < Lines.Count && Lines[i].Offset < end; i++)
                (styleLines[i] ??= new List<MarkdownStyleSpan>()).Add(style);
        }
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var items = new List<MarkdownReplacement>();
            var end = line.Offset;
            while (cursor < replacements.Length && replacements[cursor].Start < line.Offset + line.Length)
            {
                var item = replacements[cursor++];
                if (item.Start < end) continue;
                items.Add(item);
                end = item.Start + item.Length;
            }
            line.Replacements = items;
            line.Styles = styleLines[i] is { } styles ? styles : Array.Empty<MarkdownStyleSpan>();
        }
    }
}
