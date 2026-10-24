using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScrewCalendar.Tests;

internal static class RepeatRecurrenceTests
{
    internal const string Origin = "2026-01-23";
    internal const string Next = "2026-01-30";
    internal const string Source = "[repeat type=\"w\" startdate=\"2026-01-23\" enddate=\"2026-02-19\"]\nshared body\n- [ ] shared task\n[/repeat]";

    public static void Schedule()
    {
        var daily = Rule("d", "2026-01-23", "2026-01-25");
        Check(!Matches(daily, "2026-01-22") && Matches(daily, "2026-01-23") && Matches(daily, "2026-01-24") &&
            Matches(daily, "2026-01-25") && !Matches(daily, "2026-01-26"), "Daily bounds are inclusive");
        var weekly = Rule("w", "2026-01-23", "2026-02-13");
        foreach (var date in new[] { "2026-01-23", "2026-01-30", "2026-02-06", "2026-02-13" }) Check(Matches(weekly, date), "Weekday repeats across month boundaries");
        Check(!Matches(weekly, "2026-01-24") && !Matches(weekly, "2026-02-20"), "Weekly skips other weekdays and dates beyond the end");
        var monthly = Rule("m", "2026-01-31");
        foreach (var date in new[] { "2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30", "2026-12-31", "2027-01-31", "2028-02-29" })
            Check(Matches(monthly, date), "Monthly clamps to each month's last day and retains the original anchor");
        foreach (var date in new[] { "2026-01-30", "2026-03-28", "2026-03-30", "2028-02-28" }) Check(!Matches(monthly, date), "Short-month clamping never drifts subsequent months");
        var leapDay = Rule("m", "2024-02-29");
        Check(Matches(leapDay, "2024-03-29") && Matches(leapDay, "2025-02-28") && Matches(leapDay, "2028-02-29"), "Leap-day anchors stay on the 29th");
        Check(Matches(Rule("m", "2026-01-30", "2026-02-28"), "2026-02-28"), "Clamped month-end honors an inclusive end date");
        Check(Matches(Rule("d", "0001-01-01"), "9999-12-31") && Matches(Rule("m", "9999-12-31"), "9999-12-31"), "Unbounded rules support extreme dates without expanding a series");
        Check(!Matches(Rule("x", "2026-01-23"), Origin), "Invalid rules never produce occurrences");
    }

    public static void Projection()
    {
        var state = Create(out var id);
        var before = CalendarDataStore.Serialize(state);
        for (var index = 0; index < 100; index++) RepeatDocumentService.Load(state, DateTime.Parse(Origin).AddDays(index).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Check(CalendarDataStore.Serialize(state) == before, "Reading many dates never materializes or mutates stored dates");
        var origin = RepeatDocumentService.Load(state, Origin);
        Check(origin.Bindings.Count == 1 && !origin.Bindings[0].IsProjection && origin.Markdown == "origin above\n" + Source + "\norigin below", "Source date renders once in its original position");
        var next = RepeatDocumentService.Load(state, Next);
        Check(next.Bindings.Single() is { IsProjection: true, GeneratedSeparatorLength: 2 } && next.Bindings[0].Id == id && next.Markdown == Source + "\n\n", "Matching dates project the shared block without an explicit reference");
        Check(RepeatDocumentService.Load(state, "2026-01-24").Bindings.Count == 0 && RepeatDocumentService.Load(state, "2026-02-20").Bindings.Count == 0, "Nonmatching dates have no projected block");
        RepeatDocumentService.Apply(state, Next, next);
        Check(CalendarDataStore.Serialize(state) == before, "Opening and saving an unchanged projection creates no local text, separators or placements");
        var changed = Source.Replace("shared body", "updated\nmultiline body", StringComparison.Ordinal).Replace("[ ]", "[x]", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Next, Replace(next, changed));
        Check(!state.DateMarkdown.ContainsKey(Next) && !state.DateRepeatReferences.ContainsKey(Next) && state.RepeatBlocks[id] == changed,
            "Editing only projected body or checkbox updates the shared source without pinning the date");
        Check(RepeatDocumentService.Load(state, Origin).Markdown.Contains("multiline body", StringComparison.Ordinal) &&
            RepeatDocumentService.Load(state, "2026-02-06").Markdown.Contains("[x] shared task", StringComparison.Ordinal), "Other occurrence dates see text and task changes");
        var shifted = changed.Replace("2026-01-23", "2026-01-30", StringComparison.Ordinal);
        var original = RepeatDocumentService.Load(state, Origin);
        RepeatDocumentService.Apply(state, Origin, new(original.Markdown.Replace(changed, shifted, StringComparison.Ordinal), original.Bindings));
        Check(RepeatDocumentService.Load(state, Origin).Bindings.Count == 0 && RepeatDocumentService.Load(state, Origin).Markdown == "origin above\n\norigin below", "Changing start date hides the source occurrence outside the range");
        Check(!state.DateRepeatReferences.ContainsKey(Origin) && state.DateRepeatReferences[Next].Single().Id == id,
            "Changing start date moves the source reference to the new start without leaving an old editing anchor");
        var restored = CalendarDataStore.Deserialize(CalendarDataStore.Serialize(state))!;
        Check(CalendarDataStore.Validate(restored) && RepeatDocumentService.Load(restored, Next).Bindings.Single().Id == id, "Projected series survive persisted JSON round-trips");
        state.DateMarkdown[Origin] += "\n- [ ] local task";
        var reading = RepeatDocumentService.Load(state, Origin);
        RepeatDocumentService.Apply(state, Origin, new(reading.Markdown.Replace("[ ] local task", "[x] local task", StringComparison.Ordinal), reading.Bindings));
        Check(state.RepeatBlocks.ContainsKey(id) && !state.DateRepeatReferences.ContainsKey(Origin) &&
            RepeatDocumentService.Load(state, Next).Bindings.Single().Id == id && CalendarDataStore.Validate(state),
            "Saving an unrelated old-date checkbox neither restores its stale reference nor deletes the moved series");
        var sourceEditor = RepeatDocumentService.Load(state, Next);
        var sourceBinding = sourceEditor.Bindings.Single();
        RepeatDocumentService.Apply(state, Next, new(sourceEditor.Markdown.Remove(sourceBinding.Start, sourceBinding.Length), []));
        Check(state.RepeatBlocks.Count == 0 && RepeatDocumentService.Load(state, Next).Bindings.Count == 0, "Deleting the source from its new date stops the series");
    }

    public static void CleanupAndRollback()
    {
        var state = Create(out var id);
        var projected = RepeatDocumentService.Load(state, Next);
        var invalid = Source.Replace("type=\"w\"", "type=\"x\"", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Next, Replace(projected, invalid));
        Check(state.RepeatBlocks.ContainsKey(id) && state.DateRepeatReferences.ContainsKey(Next) && RepeatDocumentService.Load(state, "2026-02-06").Bindings.Count == 0,
            "Invalid projected rule is suspended and retains an editable reference on the date where it was changed");
        var fix = RepeatDocumentService.Load(state, Next);
        RepeatDocumentService.Apply(state, Next, new(fix.Markdown.Replace(invalid, Source, StringComparison.Ordinal), fix.Bindings));
        Check(RepeatDocumentService.Load(state, "2026-02-06").Bindings.Count == 1, "Repairing a suspended rule resumes projection");
        var snapshot = CalendarDataStore.Serialize(state);
        Check(!RepeatDocumentService.TrySave(state, Next, new("remaining local text", []), _ => false) && CalendarDataStore.Serialize(state) == snapshot,
            "Failed shared deletion rolls back all dates' references and text");
        var broken = RepeatDocumentService.Load(state, Next);
        var tag = broken.Markdown.IndexOf("[/repeat]", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Next, new(broken.Markdown.Remove(tag, 1), broken.Bindings.Select(binding => binding with { Length = binding.Length - 1 }).ToArray()));
        Check(state.RepeatBlocks.Count == 0 && state.DateRepeatReferences.Count == 0 && state.DateMarkdown[Next].Contains("/repeat]", StringComparison.Ordinal),
            "Saving damaged tags stops the whole series, clears every reference and preserves the edited date's residual text");
        Check(RepeatDocumentService.Load(state, "2026-02-06").Markdown.Length == 0 && state.DateMarkdown[Origin] == "origin above\n\norigin below" && CalendarDataStore.Validate(state),
            "Deleted series never regenerates and all unrelated date text survives");
    }

    public static void ReferenceCleanup()
    {
        const string first = "2026-01-24";
        const string earlier = "2026-01-25";
        const string later = "2026-01-31";
        const string newStart = "2026-01-30";
        var id = Guid.NewGuid().ToString("N");
        var state = new CalendarState();
        var daily = "[repeat type=\"d\" startdate=\"2026-01-24\" enddate=\"2026-03-31\"]\nshared\n[/repeat]";
        foreach (var key in new[] { first, earlier, later })
            RepeatDocumentService.Apply(state, key, new("above " + key + "\n" + daily + "\nbelow " + key,
                [new(id, ("above " + key + "\n").Length, daily.Length)]));
        var originalLocal = new Dictionary<string, string>(state.DateMarkdown);
        var laterReference = state.DateRepeatReferences[later].Single();
        var shifted = daily.Replace(first, newStart, StringComparison.Ordinal);
        var draft = Replace(RepeatDocumentService.Load(state, first), shifted);
        var snapshot = CalendarDataStore.Serialize(state);
        Check(!RepeatDocumentService.TrySave(state, first, draft, _ => false) && CalendarDataStore.Serialize(state) == snapshot,
            "Failed range-change save rolls back source migration, every reference and local text");
        RepeatDocumentService.Apply(state, first, draft);
        Check(!state.DateRepeatReferences.ContainsKey(first) && !state.DateRepeatReferences.ContainsKey(earlier) &&
            RepeatDocumentService.Load(state, first).Bindings.Count == 0, "Moving the start removes all earlier editing references");
        Check(state.RepeatBlocks[id] == shifted && state.DateRepeatReferences[newStart].Single() == new RepeatBlockReference(id, 0) &&
            state.DateRepeatReferences[later].Single() == laterReference, "New start gains an anchor and still-valid custom positions remain intact");
        Check(originalLocal.All(item => state.DateMarkdown[item.Key] == item.Value) && CalendarDataStore.Validate(state),
            "Reference migration preserves every date's exact local text and valid storage");
        RepeatDocumentService.Apply(state, later, Replace(RepeatDocumentService.Load(state, later), shifted.Replace("type=\"d\"", "type=\"w\"", StringComparison.Ordinal)));
        Check(!state.DateRepeatReferences.ContainsKey(later) && state.DateRepeatReferences[newStart].Single().Id == id,
            "Changing to weekly removes positions on the wrong weekday");
        var weekly = state.RepeatBlocks[id];
        var invalid = weekly.Replace(newStart, "bad-date", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, newStart, Replace(RepeatDocumentService.Load(state, newStart), invalid));
        Check(state.RepeatBlocks[id] == invalid && RepeatDocumentService.Load(state, newStart).Bindings.Single().Id == id &&
            state.DateRepeatReferences.Count == 1, "Invalid attributes keep the existing repair location without creating a date anchor");
        var repaired = weekly.Replace(newStart, "2026-02-06", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, newStart, Replace(RepeatDocumentService.Load(state, newStart), repaired));
        Check(!state.DateRepeatReferences.ContainsKey(newStart) && state.DateRepeatReferences["2026-02-06"].Single().Id == id,
            "Correcting invalid dates resumes reference migration");

        // The new start may already have a user-chosen position; never overwrite it.
        state = Create(out id);
        RepeatDocumentService.Apply(state, Next, new("prefix\n" + Source + "\nsuffix", [new(id, 7, Source.Length)]));
        var nextPosition = state.DateRepeatReferences[Next].Single();
        RepeatDocumentService.Apply(state, Origin, Replace(RepeatDocumentService.Load(state, Origin), Source.Replace(Origin, Next, StringComparison.Ordinal)));
        Check(state.DateRepeatReferences[Next].Single() == nextPosition && RepeatDocumentService.Load(state, Next).Bindings.Count == 1,
            "An existing position at the new start is retained without duplicating the block");
        var nextSource = state.RepeatBlocks[id];
        RepeatDocumentService.Apply(state, "2026-02-06", new("future above\n" + nextSource + "\nfuture below", [new(id, 13, nextSource.Length)]));
        RepeatDocumentService.Apply(state, Next, Replace(RepeatDocumentService.Load(state, Next), nextSource.Replace("2026-02-19", Next, StringComparison.Ordinal)));
        Check(!state.DateRepeatReferences.ContainsKey("2026-02-06") && state.DateMarkdown["2026-02-06"] == "future above\n\nfuture below",
            "Shortening the end removes later references while preserving their surrounding text");

        state = Create(out id);
        var monthly = Source.Replace("type=\"w\"", "type=\"m\"", StringComparison.Ordinal)
            .Replace(Origin, "2026-01-31", StringComparison.Ordinal).Replace("2026-02-19", "2026-04-30", StringComparison.Ordinal);
        RepeatDocumentService.Apply(state, Origin, Replace(RepeatDocumentService.Load(state, Origin), monthly));
        RepeatDocumentService.Apply(state, "2026-02-28", new("above\n" + monthly + "\nbelow", [new(id, 6, monthly.Length)]));
        RepeatDocumentService.Apply(state, "2026-01-31", Replace(RepeatDocumentService.Load(state, "2026-01-31"), monthly.Replace("2026-01-31", "2026-03-31", StringComparison.Ordinal)));
        Check(!state.DateRepeatReferences.ContainsKey("2026-02-28") && state.DateRepeatReferences["2026-03-31"].Single().Id == id &&
            RepeatDocumentService.Load(state, "2026-04-30").Bindings.Count == 1, "Monthly cleanup uses the same month-end matching rule as rendering");

        state = Create(out id);
        var otherId = Guid.NewGuid().ToString("N");
        var otherSource = Source.Replace(Origin, Next, StringComparison.Ordinal).Replace("shared body", "other reminder", StringComparison.Ordinal);
        var target = "target above\r\n" + otherSource + "\r\ntarget below";
        state.DateMarkdown[Next] = "target above\r\n\r\ntarget below";
        state.RepeatBlocks[otherId] = otherSource;
        state.DateRepeatReferences[Next] = [new(otherId, "target above\r\n".Length)];
        var oldTargetLocal = state.DateMarkdown[Next];
        var oldOtherOffset = state.DateRepeatReferences[Next].Single().Offset;
        RepeatDocumentService.Apply(state, Origin, Replace(RepeatDocumentService.Load(state, Origin), Source.Replace(Origin, Next, StringComparison.Ordinal)));
        var targetDocument = RepeatDocumentService.Load(state, Next);
        Check(state.DateMarkdown[Next] == "\r\n\r\n" + oldTargetLocal &&
            state.DateRepeatReferences[Next].Single(reference => reference.Id == otherId).Offset == oldOtherOffset + 4 &&
            RepeatDocumentService.ValidBlocks(targetDocument).Count == 2 && targetDocument.Markdown.EndsWith(target, StringComparison.Ordinal),
            "Moving into an occupied date preserves its content and other block's position with standalone source boundaries");
        var normalizedSnapshot = CalendarDataStore.Serialize(state);
        RepeatDocumentService.Apply(state, Next, targetDocument);
        Check(CalendarDataStore.Serialize(state) == normalizedSnapshot && CalendarDataStore.Validate(state),
            "Re-saving a migrated date adds no separators, references or duplicate reminders");

        // Existing on-disk data must be corrected on load and import, not only after another edit.
        state = Create(out id);
        state.RepeatBlocks[id] = Source.Replace(Origin, Next, StringComparison.Ordinal);
        var legacyJson = CalendarDataStore.Serialize(state);
        var folder = Path.Combine(Path.GetTempPath(), "ScrewRepeatReferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new CalendarDataStore(folder, (_, _) => { });
            File.WriteAllText(store.StatePath, legacyJson);
            foreach (var loaded in new[] { store.Load(), store.Import(store.StatePath) })
            {
                Check(!loaded.DateRepeatReferences.ContainsKey(Origin) && loaded.DateRepeatReferences[Next].Single().Id == id &&
                    loaded.DateMarkdown[Origin] == state.DateMarkdown[Origin] && CalendarDataStore.Validate(loaded),
                    "Loading or importing existing data cleans stale references without losing the shared source or local text");
                Check(store.Save(loaded), "Normalized references can be persisted");
            }
            Check(store.Load().DateRepeatReferences[Next].Count == 1, "Reloading normalized data is idempotent");

            state = Create(out id);
            var largeSource = Source.Replace(Origin, Next, StringComparison.Ordinal)
                .Replace("shared body", new string('x', CalendarLayout.MaximumMarkdownLength / 2), StringComparison.Ordinal);
            state.RepeatBlocks[id] = largeSource;
            otherId = Guid.NewGuid().ToString("N");
            state.RepeatBlocks[otherId] = largeSource;
            state.DateRepeatReferences[Next] = [new(otherId, 0)];
            Check(CalendarDataStore.Validate(state), "Each stored source fits the document limit before anchor migration");
            var oversizedJson = CalendarDataStore.Serialize(state);
            File.WriteAllText(store.StatePath, oversizedJson);
            var rejected = false;
            try { store.Import(store.StatePath); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && File.ReadAllText(store.StatePath) == oversizedJson,
                "Import revalidates the combined destination after migration and rejects overflow without modifying the file");
            Check(CalendarDataStore.Validate(store.Load()) && store.LastLoadStatus == CalendarLoadStatus.Backup,
                "Startup applies the same normalized validation and recovers the valid backup");
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static CalendarState Create(out string id)
    {
        id = Guid.NewGuid().ToString("N");
        var state = new CalendarState();
        RepeatDocumentService.Apply(state, Origin, new("origin above\n" + Source + "\norigin below", [new(id, "origin above\n".Length, Source.Length)]));
        return state;
    }

    private static RepeatDocument Replace(RepeatDocument document, string source)
    {
        var binding = document.Bindings.Single();
        return new(document.Markdown.Remove(binding.Start, binding.Length).Insert(binding.Start, source), [binding with { Length = source.Length }]);
    }

    private static RepeatBlock Rule(string type, string start, string? end = null) => RepeatBlockParser.Parse(
        $"[repeat type=\"{type}\" startdate=\"{start}\"" + (end is null ? "" : $" enddate=\"{end}\"") + "]\nbody\n[/repeat]").Single();
    private static bool Matches(RepeatBlock rule, string date) => RepeatSchedule.Matches(rule, DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
