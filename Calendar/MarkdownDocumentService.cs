using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Markdig;

namespace ScrewCalendar;

public enum MarkdownLineKind { Paragraph, Heading1, Heading2, Heading3, Bullet, Task, Image }
public sealed record MarkdownLine(MarkdownLineKind Kind, string Text, bool IsChecked = false, int Indent = 0, string? ImagePath = null);

public static partial class MarkdownDocumentService
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .DisableHtml()
        .Build();

    // Shared by compact calendar summaries and full readers; never cache WPF elements.
    private static readonly Dictionary<string, MarkdownSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly Queue<string> SnapshotOrder = new();

    public static MarkdownSnapshot Parse(string? markdown)
    {
        markdown ??= string.Empty;
        lock (Snapshots)
        {
            if (Snapshots.TryGetValue(markdown, out var cached)) return cached;
            var snapshot = new MarkdownSnapshot(markdown);
            if (Snapshots.Count >= 32) Snapshots.Remove(SnapshotOrder.Dequeue());
            Snapshots.Add(markdown, snapshot);
            SnapshotOrder.Enqueue(markdown);
            return snapshot;
        }
    }

    public static string SerializeLine(MarkdownLine line)
    {
        var indent = new string(' ', line.Indent * 2);
        return line.Kind switch
        {
            MarkdownLineKind.Heading1 => $"# {line.Text}",
            MarkdownLineKind.Heading2 => $"## {line.Text}",
            MarkdownLineKind.Heading3 => $"### {line.Text}",
            MarkdownLineKind.Bullet => $"{indent}- {line.Text}",
            MarkdownLineKind.Task => $"{indent}- [{(line.IsChecked ? "x" : " ")}] {line.Text}",
            MarkdownLineKind.Image => $"![{line.Text}]({line.ImagePath})",
            _ => line.Text
        };
    }

    public static string ToggleTask(string markdown, int taskIndex, bool isChecked)
    {
        var snapshot = Parse(markdown);
        if (taskIndex < 0 || taskIndex >= snapshot.Tasks.Count) return markdown;
        var offset = snapshot.Tasks[taskIndex].MarkerOffset;
        return markdown.Remove(offset, 1).Insert(offset, isChecked ? "x" : " ");
    }

    internal static string FormatLine(MarkdownSnapshot snapshot, int index, MarkdownLineKind kind)
    {
        var line = snapshot.Lines[index];
        var source = snapshot.Source.Substring(line.Offset, line.Length);
        if (line.IsCode || line.Replacements.Count > 0 && line.Replacements[0].Kind == MarkdownReplacementKind.Image)
            return source;
        var prefix = 0;
        while (prefix < source.Length && (char.IsWhiteSpace(source[prefix]) || source[prefix] == '>')) prefix++;
        var content = source[prefix..];
        var isChecked = false;
        if (line.ListMarker is { } marker)
        {
            prefix = marker.Start - line.Offset;
            content = snapshot.Source.Substring(marker.ContentStart, line.Offset + line.Length - marker.ContentStart);
            isChecked = marker.IsTask && snapshot.Source[marker.Start..marker.ContentStart].Contains("[x]", StringComparison.OrdinalIgnoreCase);
        }
        else if (line.HeadingLevel > 0 && !line.IsSyntaxLine)
        {
            content = HeadingPrefix().Replace(content, string.Empty);
            content = Regex.Replace(content, @"\s+#+\s*$", string.Empty);
        }
        return source[..prefix] + SerializeLine(new MarkdownLine(kind, content, isChecked));
    }

    [GeneratedRegex(@"^#{1,6}(?:\s+|$)")]
    private static partial Regex HeadingPrefix();
}
