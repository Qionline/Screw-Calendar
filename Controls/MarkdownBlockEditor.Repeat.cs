using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;

namespace ScrewCalendar;

public sealed partial class MarkdownBlockEditor
{
    private DateTime? _repeatDate;
    private List<RepeatBlockBinding> _repeatBindings = [];
    private readonly HashSet<string> _knownRepeatIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredRepeatIds = new(StringComparer.Ordinal);
    public bool CanUseRepeat => _repeatDate.HasValue;

    public void LoadRepeatDocument(DateTime? date, RepeatDocument document)
    {
        var bindings = date.HasValue ? document.Bindings.ToList() : [];
        var externalChange = _repeatDate != date?.Date || GetMarkdown() != document.Markdown || !_repeatBindings.SequenceEqual(bindings);
        _repeatDate = date?.Date;
        UpdateMarkdown(document.Markdown);
        if (externalChange)
        {
            // An external save/import is a new baseline, not an editable undo operation.
            _editor.Document.UndoStack.ClearAll();
            ResetRepeatTracking();
        }
        _repeatBindings = bindings;
        foreach (var binding in _repeatBindings) _knownRepeatIds.Add(binding.Id);
        InvalidateRepeatPresentation();
    }

    private void ResetRepeatTracking()
    {
        _repeatBindings.Clear();
        _knownRepeatIds.Clear();
        _retiredRepeatIds.Clear();
        InvalidateRepeatPresentation();
    }

    public RepeatDocument GetRepeatDraft() => new(GetMarkdown(),
        _repeatBindings.Where(binding => !_retiredRepeatIds.Contains(binding.Id)).ToArray());

    public void AcceptRepeatSave(IReadOnlyList<RepeatBlockBinding> savedBindings)
    {
        var retained = savedBindings.Select(binding => binding.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _knownRepeatIds.Where(id => !retained.Contains(id))) _retiredRepeatIds.Add(id);
        _repeatBindings = savedBindings.ToList();
        InvalidateRepeatPresentation();
    }

    public bool InsertRepeat()
    {
        if (!CanUseRepeat || IsPreviewMode || !RepeatBlockParser.CanInsert(_editor.Text, _editor.SelectionStart, _editor.SelectionLength)) return false;
        var date = _repeatDate!.Value;
        var end = date > DateTime.MaxValue.Date.AddDays(-27) ? DateTime.MaxValue.Date : date.AddDays(27);
        var newline = _editor.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var body = Localization.T("markdown.repeatBody");
        var header = string.Format(CultureInfo.InvariantCulture, "[repeat type=\"w\" startdate=\"{0:yyyy-MM-dd}\" enddate=\"{1:yyyy-MM-dd}\"]", date, end);
        var block = header + newline + body + newline + RepeatBlockParser.ClosingTag;
        var offset = _editor.SelectionStart;
        var selectionEnd = offset + _editor.SelectionLength;
        var prefix = offset > 0 && _editor.Text[offset - 1] is not ('\n' or '\r') ? newline : string.Empty;
        var suffix = selectionEnd < _editor.Text.Length && _editor.Text[selectionEnd] is not ('\n' or '\r') ? newline : string.Empty;
        _editor.Document.BeginUpdate();
        try
        {
            _editor.Document.Replace(offset, _editor.SelectionLength, prefix + block + suffix);
            var before = _repeatBindings.ToArray();
            var binding = new RepeatBlockBinding(Guid.NewGuid().ToString("N"), offset + prefix.Length, block.Length);
            _repeatBindings.Add(binding);
            _knownRepeatIds.Add(binding.Id);
            _editor.Document.UndoStack.Push(new RepeatMappingChange(this, before, _repeatBindings.ToArray()));
            _editor.Select(binding.Start + header.Length + newline.Length, body.Length);
        }
        finally { _editor.Document.EndUpdate(); }
        FocusEditor();
        _editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    private void TrackRepeatChange(object? sender, DocumentChangeEventArgs change)
    {
        // Playback restores exact metadata snapshots. It must not transform ranges a second time.
        if (_loading || !_editor.Document.UndoStack.AcceptChanges || _repeatBindings.Count == 0) return;
        var before = _repeatBindings.ToArray();
        var separators = before.Where(binding => binding.GeneratedSeparatorLength > 0).ToDictionary(binding => binding.Id, binding =>
        {
            var end = binding.Start + binding.Length;
            return (Start: change.GetNewOffset(end, AnchorMovementType.AfterInsertion),
                End: change.GetNewOffset(end + binding.GeneratedSeparatorLength, AnchorMovementType.BeforeInsertion));
        });
        _repeatBindings = _repeatBindings.Where(binding =>
                !(change.RemovalLength > 0 && change.Offset <= binding.Start && change.Offset + change.RemovalLength >= binding.Start + binding.Length))
            .Select(binding =>
            {
                var movement = AnchorMovementType.AfterInsertion;
                if (change.Offset == binding.Start)
                {
                    var tailStart = change.Offset + change.InsertionLength;
                    var oldPrefix = change.RemovedText.Text + _editor.Document.GetText(tailStart, Math.Min(8, _editor.Document.TextLength - tailStart));
                    var newPrefix = _editor.Document.GetText(binding.Start, Math.Min(8, _editor.Document.TextLength - binding.Start));
                    if (change.RemovalLength > 0 || !RepeatBlockParser.IsOpening(oldPrefix) && RepeatBlockParser.IsOpening(newPrefix))
                        movement = AnchorMovementType.BeforeInsertion;
                }
                var start = change.GetNewOffset(binding.Start, movement);
                var end = change.GetNewOffset(binding.Start + binding.Length, AnchorMovementType.AfterInsertion);
                return binding with { Start = start, Length = Math.Max(0, end - start) };
            }).ToList();
        // Keep an incomplete closing tag repairable; trim complete blocks so text appended
        // below the closing line always belongs to the date, rather than the shared body.
        var parsed = RepeatBlockParser.Parse(_editor.Text).ToDictionary(block => block.Start);
        _repeatBindings = _repeatBindings.Select(binding =>
        {
            if (parsed.TryGetValue(binding.Start, out var block) && block.Length <= binding.Length)
                binding = binding with { Length = block.Length };
            if (separators.TryGetValue(binding.Id, out var separator) &&
                (separator.Start != binding.Start + binding.Length || separator.End - separator.Start != binding.GeneratedSeparatorLength))
                binding = binding with { GeneratedSeparatorLength = 0 };
            return binding;
        }).ToList();
        if (!before.SequenceEqual(_repeatBindings))
            _editor.Document.UndoStack.PushOptional(new RepeatMappingChange(this, before, _repeatBindings.ToArray()));
    }

    private sealed class RepeatMappingChange(MarkdownBlockEditor owner, RepeatBlockBinding[] before, RepeatBlockBinding[] after) : IUndoableOperation
    {
        public void Undo() => owner._repeatBindings = before.ToList();
        public void Redo() => owner._repeatBindings = after.ToList();
    }
}
