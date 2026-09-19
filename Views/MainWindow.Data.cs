using System;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace ScrewCalendar;

// Persistence and user-driven JSON import/export for the main window.
public sealed partial class MainWindow
{
    private void SaveState()
    {
        if (!_dataStore.Save(_state)) AppLogger.Error("The current calendar state could not be saved.");
    }

    private void SaveAndRender() { SaveState(); Render(); }

    private void ExportJson()
    {
        using var dialog = new Forms.SaveFileDialog { Filter = Localization.T("file.json") + "|*.json", FileName = $"calendar-{DateTime.Today:yyyyMMdd}.json", InitialDirectory = _dataStore.DataDirectory };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try { _dataStore.Export(dialog.FileName, _state); }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to export calendar data.", exception);
            MessageBox.Show(this, Localization.T("file.exportError"), Localization.T("dialog.calendar"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportJson()
    {
        using var dialog = new Forms.OpenFileDialog { Filter = Localization.T("file.json") + "|*.json" };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var imported = _dataStore.Import(dialog.FileName);
            _state.Rows = CalendarLayout.ClampRows(imported.Rows);
            _state.Style = imported.Style;
            _state.Theme = imported.Theme;
            _state.ThemeColor = imported.ThemeColor;
            _state.Language = imported.Language;
            _state.Opacity = imported.Opacity;
            _state.Locked = imported.Locked;
            _state.Topmost = imported.Topmost;
            _state.ShowTodoPanel = imported.ShowTodoPanel;
            _state.TodoPanelHeight = imported.TodoPanelHeight;
            _state.PermanentMarkdown = imported.PermanentMarkdown;
            _state.DateMarkdown = imported.DateMarkdown;
            Localization.SetLanguage(_state.Language);
            SaveState();
            ApplyVisuals();
            RefreshLocalizedUi();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to import calendar data.", exception);
            MessageBox.Show(this, Localization.T("file.importError"), Localization.T("dialog.calendar"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
