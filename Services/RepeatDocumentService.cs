using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScrewCalendar;

public sealed record RepeatDocument(string Markdown, IReadOnlyList<RepeatBlockBinding> Bindings);

/// <summary>Composes shared blocks for editing; keeps all surrounding text local to its date.</summary>
public static class RepeatDocumentService
{
    public static IReadOnlyList<RepeatBlock> ValidBlocks(RepeatDocument document)
    {
        if (document.Bindings.Count == 0) return [];
        var bindings = document.Bindings.ToDictionary(binding => binding.Start, binding => binding.Length);
        return RepeatBlockParser.Parse(document.Markdown).Where(block => block.IsValid &&
            bindings.TryGetValue(block.Start, out var length) && length == block.Length).ToArray();
    }

    public static string VisibleSource(RepeatDocument document)
    {
        var text = new StringBuilder();
        var position = 0;
        foreach (var block in ValidBlocks(document))
        {
            text.Append(document.Markdown, position, block.Start - position);
            text.Append(document.Markdown, block.BodyStart, block.ClosingStart - block.BodyStart);
            position = block.Start + block.Length;
        }
        text.Append(document.Markdown, position, document.Markdown.Length - position);
        return text.ToString();
    }

    public static bool TrySave(CalendarState state, string? dateKey, RepeatDocument draft, Func<CalendarState, bool> save)
    {
        var permanent = state.PermanentMarkdown;
        var dates = state.DateMarkdown;
        var blocks = state.RepeatBlocks;
        var placements = state.DateRepeatReferences;
        var succeeded = false;
        try
        {
            if (dateKey is null) state.PermanentMarkdown = draft.Markdown;
            else Apply(state, dateKey, draft);
            succeeded = save(state);
            return succeeded;
        }
        finally
        {
            if (!succeeded)
            {
                state.PermanentMarkdown = permanent;
                state.DateMarkdown = dates;
                state.RepeatBlocks = blocks;
                state.DateRepeatReferences = placements;
            }
        }
    }

    public static RepeatDocument Load(CalendarState state, string dateKey)
    {
        var date = DateOnly.ParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var stored = LoadStored(state, dateKey, date);
        var placed = state.DateRepeatReferences.GetValueOrDefault(dateKey, []).Select(reference => reference.Id).ToHashSet(StringComparer.Ordinal);
        var text = new StringBuilder();
        var bindings = new List<RepeatBlockBinding>();
        foreach (var (id, source) in state.RepeatBlocks)
        {
            if (placed.Contains(id) || !RepeatSchedule.Matches(RepeatBlockParser.Parse(source).Single(), date)) continue;
            bindings.Add(new(id, text.Length, source.Length, IsProjection: true, GeneratedSeparatorLength: 2));
            text.Append(source).Append("\n\n");
        }
        var prefix = text.Length;
        text.Append(stored.Markdown);
        bindings.AddRange(stored.Bindings.Select(binding => binding with { Start = binding.Start + prefix }));
        return new(text.ToString(), bindings);
    }

    private static RepeatDocument LoadStored(CalendarState state, string dateKey, DateOnly date)
    {
        var local = state.DateMarkdown.GetValueOrDefault(dateKey, string.Empty);
        if (!state.DateRepeatReferences.TryGetValue(dateKey, out var references)) return new(local, []);
        var text = new StringBuilder();
        var bindings = new List<RepeatBlockBinding>();
        var position = 0;
        foreach (var reference in references)
        {
            text.Append(local, position, reference.Offset - position);
            var block = state.RepeatBlocks[reference.Id];
            var rule = RepeatBlockParser.Parse(block).Single();
            if (!rule.IsValid || RepeatSchedule.Matches(rule, date))
            {
                bindings.Add(new(reference.Id, text.Length, block.Length));
                text.Append(block);
            }
            position = reference.Offset;
        }
        text.Append(local, position, local.Length - position);
        return new(text.ToString(), bindings);
    }

    // The caller saves the resulting state atomically or restores all three original dictionaries.
    public static void Apply(CalendarState state, string dateKey, RepeatDocument draft)
    {
        var baseline = Load(state, dateKey);
        var baselineIds = baseline.Bindings.Select(binding => binding.Id).ToHashSet(StringComparer.Ordinal);
        baselineIds.UnionWith(draft.Bindings.Where(binding => state.RepeatBlocks.ContainsKey(binding.Id)).Select(binding => binding.Id));
        var parsed = RepeatBlockParser.Parse(draft.Markdown).ToDictionary(block => block.Start);
        var blocks = new Dictionary<string, string>(state.RepeatBlocks);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var retained = new List<RepeatBlockBinding>();
        var lastEnd = 0;
        foreach (var binding in draft.Bindings.OrderBy(binding => binding.Start))
        {
            if (binding.Start < lastEnd || !Guid.TryParseExact(binding.Id, "N", out _) || !seen.Add(binding.Id) ||
                !parsed.TryGetValue(binding.Start, out var block) || block.Length != binding.Length) continue;
            retained.Add(binding);
            blocks[binding.Id] = draft.Markdown.Substring(binding.Start, binding.Length);
            lastEnd = binding.Start + binding.Length;
        }
        // Removing a shared block stops the series; no per-day exception feature is implied.
        var retainedIds = retained.Select(binding => binding.Id).ToHashSet(StringComparer.Ordinal);
        var removed = baselineIds.Where(id => !retainedIds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var placements = new Dictionary<string, List<RepeatBlockReference>>();
        foreach (var (key, items) in state.DateRepeatReferences)
        {
            var remaining = items.Where(reference => !removed.Contains(reference.Id)).ToList();
            if (remaining.Count > 0) placements[key] = remaining;
        }
        foreach (var id in removed) blocks.Remove(id);

        var (local, references) = Split(stripGeneratedSeparators: true);
        var previous = placements.GetValueOrDefault(dateKey, []);
        var explicitIds = retained.Where(binding => !binding.IsProjection).Select(binding => binding.Id).ToHashSet(StringComparer.Ordinal);
        var explicitReferences = references.Where(reference => explicitIds.Contains(reference.Id)).ToList();
        var keepPositions = local != state.DateMarkdown.GetValueOrDefault(dateKey, string.Empty) || !explicitReferences.SequenceEqual(previous) ||
            retained.Any(binding => binding.IsProjection && !parsed[binding.Start].IsValid);
        if (keepPositions) (local, references) = Split(stripGeneratedSeparators: false);
        else references = explicitReferences;
        var dates = new Dictionary<string, string>(state.DateMarkdown);
        if (local.Length == 0) dates.Remove(dateKey);
        else dates[dateKey] = local;
        if (references.Count == 0) placements.Remove(dateKey);
        else placements[dateKey] = references;
        NormalizeReferences(blocks, dates, placements);
        var used = placements.Values.SelectMany(items => items).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in blocks.Keys.Where(id => !used.Contains(id)).ToArray()) blocks.Remove(id);
        state.DateMarkdown = dates;
        state.RepeatBlocks = blocks;
        state.DateRepeatReferences = placements;

        (string Local, List<RepeatBlockReference> References) Split(bool stripGeneratedSeparators)
        {
            var text = new StringBuilder();
            var references = new List<RepeatBlockReference>();
            var position = 0;
            foreach (var binding in retained)
            {
                text.Append(draft.Markdown, position, binding.Start - position);
                references.Add(new(binding.Id, text.Length));
                position = binding.Start + binding.Length;
                if (stripGeneratedSeparators && binding.IsProjection && binding.GeneratedSeparatorLength == 2 &&
                    position + 2 <= draft.Markdown.Length && draft.Markdown.AsSpan(position, 2).SequenceEqual("\n\n")) position += 2;
            }
            text.Append(draft.Markdown, position, draft.Markdown.Length - position);
            return (text.ToString(), references);
        }
    }

    // Normalize old saved positions too, without expanding the recurrence into daily records.
    internal static void NormalizeReferences(CalendarState state)
    {
        var dates = new Dictionary<string, string>(state.DateMarkdown);
        var placements = state.DateRepeatReferences.ToDictionary(item => item.Key, item => item.Value.ToList());
        NormalizeReferences(state.RepeatBlocks, dates, placements);
        state.DateMarkdown = dates;
        state.DateRepeatReferences = placements;
    }

    private static void NormalizeReferences(IReadOnlyDictionary<string, string> blocks,
        Dictionary<string, string> dates,
        Dictionary<string, List<RepeatBlockReference>> placements)
    {
        var rules = blocks.ToDictionary(item => item.Key, item => RepeatBlockParser.Parse(item.Value).Single());
        foreach (var key in placements.Keys.ToArray())
        {
            var date = DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            placements[key].RemoveAll(reference => rules[reference.Id].IsValid && !RepeatSchedule.Matches(rules[reference.Id], date));
            if (placements[key].Count == 0) placements.Remove(key);
        }
        foreach (var (id, rule) in rules)
        {
            // Invalid attributes retain their existing repair locations until corrected.
            if (!rule.IsValid) continue;
            var key = rule.StartDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!placements.TryGetValue(key, out var references)) placements[key] = references = [];
            if (references.Any(reference => reference.Id == id)) continue;
            var local = dates.GetValueOrDefault(key, string.Empty);
            if (local.Length > 0 || references.Count > 0)
            {
                // The fallback source must stay on standalone lines before existing content.
                var separator = local.Contains("\r\n", StringComparison.Ordinal) ? "\r\n\r\n" : "\n\n";
                dates[key] = separator + local;
                for (var index = 0; index < references.Count; index++)
                    references[index] = references[index] with { Offset = references[index].Offset + separator.Length };
            }
            references.Insert(0, new(id, 0));
        }
    }
}
