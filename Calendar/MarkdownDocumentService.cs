using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Markdig;

namespace ScrewCalendar;

public enum MarkdownLineKind { Paragraph, Heading1, Heading2, Heading3, Bullet, Task, Image }

public sealed record MarkdownLine(MarkdownLineKind Kind, string Text, bool IsChecked = false, int Indent = 0, string? ImagePath = null);

public static partial class MarkdownDocumentService
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MarkdownLine> ParseLines(string? markdown)
    {
        markdown ??= string.Empty;
        _ = Markdown.Parse(markdown, Pipeline);
        var result = new List<MarkdownLine>();
        foreach (var sourceLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = Math.Min(4, line.TakeWhile(char.IsWhiteSpace).Count() / 2);
            var content = line.TrimStart();
            var image = ImageLine().Match(content);
            if (image.Success)
            {
                result.Add(new MarkdownLine(MarkdownLineKind.Image, image.Groups[1].Value, ImagePath: image.Groups[2].Value));
                continue;
            }
            var task = TaskLine().Match(content);
            if (task.Success)
            {
                result.Add(new MarkdownLine(MarkdownLineKind.Task, task.Groups[2].Value, task.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase), indent));
                continue;
            }
            var bullet = BulletLine().Match(content);
            if (bullet.Success)
            {
                result.Add(new MarkdownLine(MarkdownLineKind.Bullet, bullet.Groups[1].Value, Indent: indent));
                continue;
            }
            var heading = HeadingLine().Match(content);
            if (heading.Success)
            {
                var kind = heading.Groups[1].Value.Length switch
                {
                    1 => MarkdownLineKind.Heading1,
                    2 => MarkdownLineKind.Heading2,
                    _ => MarkdownLineKind.Heading3
                };
                result.Add(new MarkdownLine(kind, heading.Groups[2].Value));
                continue;
            }
            result.Add(new MarkdownLine(MarkdownLineKind.Paragraph, content));
        }
        return result;
    }

    public static string SerializeLines(IEnumerable<MarkdownLine> lines) => string.Join(Environment.NewLine, lines.Select(SerializeLine)).Trim();

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
        var seen = 0;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = TaskLine().Match(lines[index].TrimStart());
            if (!match.Success) continue;
            if (seen++ != taskIndex) continue;
            var markerIndex = lines[index].IndexOf('[', StringComparison.Ordinal);
            if (markerIndex >= 0 && markerIndex + 2 < lines[index].Length)
                lines[index] = lines[index].Remove(markerIndex + 1, 1).Insert(markerIndex + 1, isChecked ? "x" : " ");
            break;
        }
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s*(.*)$", RegexOptions.Compiled)]
    private static partial Regex TaskLine();

    [GeneratedRegex(@"^[-*+]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^!\[(.*?)\]\((.*?)\)\s*$", RegexOptions.Compiled)]
    private static partial Regex ImageLine();
}
