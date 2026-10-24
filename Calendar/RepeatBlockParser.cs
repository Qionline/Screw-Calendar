using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScrewCalendar;

public sealed record RepeatBlock(int Start, int Length, int BodyStart, int BodyLength,
    string? Type, DateOnly? StartDate, DateOnly? EndDate, string? Error)
{
    // Valid syntax alone never activates a block; activation also requires a saved binding.
    public bool IsValid => Error is null;
    internal int ClosingStart => Start + Length - RepeatBlockParser.ClosingTag.Length;
}

/// <summary>Recognizes standalone tags, preserving source positions and invalid rule identities.</summary>
public static class RepeatBlockParser
{
    internal const string ClosingTag = "[/repeat]";
    private static readonly Regex Attribute = new("\\G\\s+(?<name>[a-z]+)=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant);

    public static IReadOnlyList<RepeatBlock> Parse(string source)
    {
        var result = new List<RepeatBlock>();
        MarkdownSourceLine? opening = null;
        foreach (var line in MarkdownDocumentService.Parse(source).Lines)
        {
            if (line.IsCode) continue;
            var text = source.Substring(line.Offset, line.Length);
            // A new opening abandons an incomplete preceding block. Never bind across it.
            if (IsOpening(text)) { opening = line; continue; }
            if (text != ClosingTag || opening is null) continue;
            var header = source.Substring(opening.Offset, opening.Length);
            if (header.EndsWith(']'))
            {
                var bodyStart = opening.Offset + opening.Length + opening.DelimiterLength;
                var bodyEnd = line.Offset;
                if (bodyEnd > bodyStart && source[bodyEnd - 1] == '\n') bodyEnd--;
                if (bodyEnd > bodyStart && source[bodyEnd - 1] == '\r') bodyEnd--;
                result.Add(ParseRule(header, opening.Offset, line.Offset + line.Length - opening.Offset,
                    bodyStart, Math.Max(0, bodyEnd - bodyStart)));
            }
            opening = null;
        }
        return result;
    }

    internal static bool IsOpening(string line) => line.StartsWith("[repeat", StringComparison.Ordinal) &&
        (line.Length == 7 || line[7] == ']' || char.IsWhiteSpace(line[7]));

    internal static bool HasValidAttributes(string header) => header.EndsWith(']') &&
        ParseRule(header, 0, header.Length, 0, 0).IsValid;

    public static bool CanInsert(string source, int start, int length)
    {
        var end = start + length;
        var inside = false;
        foreach (var line in MarkdownDocumentService.Parse(source).Lines)
        {
            var lineEnd = line.Offset + line.Length;
            var intersects = (length == 0 ? line.Offset <= end : line.Offset < end) && lineEnd >= start;
            if (line.IsCode) { if (intersects) return false; continue; }
            var text = source.Substring(line.Offset, line.Length);
            if (IsOpening(text))
            {
                if (intersects && (length > 0 || start > line.Offset)) return false;
                inside = true;
            }
            else if (text == ClosingTag)
            {
                if (intersects && inside && (length > 0 || start < lineEnd)) return false;
                inside = false;
            }
            else if (intersects && inside) return false;
            if (line.Offset > end) break;
        }
        return true;
    }

    private static RepeatBlock ParseRule(string header, int start, int length, int bodyStart, int bodyLength)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;
        var position = 7;
        while (position < header.Length - 1)
        {
            if (string.IsNullOrWhiteSpace(header.Substring(position, header.Length - 1 - position))) break;
            var match = Attribute.Match(header, position);
            if (!match.Success || match.Index + match.Length > header.Length - 1) { error = "attributes"; break; }
            var name = match.Groups["name"].Value;
            if (name is not ("type" or "startdate" or "enddate") || !attributes.TryAdd(name, match.Groups["value"].Value))
                error = "attributes";
            position += match.Length;
        }
        attributes.TryGetValue("type", out var type);
        if (type is not ("d" or "w" or "m")) error ??= "type";
        DateOnly? startDate = null;
        DateOnly? endDate = null;
        if (attributes.TryGetValue("startdate", out var first) && TryDate(first, out var parsedStart)) startDate = parsedStart;
        else error ??= "startdate";
        if (attributes.TryGetValue("enddate", out var last) && last.Length > 0)
        {
            if (TryDate(last, out var parsedEnd)) endDate = parsedEnd;
            else error ??= "enddate";
        }
        if (endDate < startDate) error ??= "range";
        return new(start, length, bodyStart, bodyLength, type, startDate, endDate, error);
    }

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
