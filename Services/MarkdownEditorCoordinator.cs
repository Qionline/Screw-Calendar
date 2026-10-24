using System;

namespace ScrewCalendar;

// Keeps one Markdown dialog open and saves its draft before switching documents.
internal sealed class MarkdownEditorCoordinator
{
    private string? _documentKey;
    private Action? _activate;
    private Func<bool>? _saveAndClose;

    public bool TryPrepareToOpen(string documentKey)
    {
        if (_documentKey == documentKey)
        {
            _activate?.Invoke();
            return false;
        }

        return _saveAndClose?.Invoke() ?? true;
    }

    public void Register(string documentKey, Action activate, Func<bool> saveAndClose)
    {
        _documentKey = documentKey;
        _activate = activate;
        _saveAndClose = saveAndClose;
    }

    public void Clear(string documentKey)
    {
        if (_documentKey != documentKey) return;
        _documentKey = null;
        _activate = null;
        _saveAndClose = null;
    }
}
