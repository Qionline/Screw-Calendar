using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ScrewCalendar;

// One Markdown document per date, plus one permanent Markdown document.
public sealed partial class MainWindow
{
    private void ShowMarkdownEditor(DateTime date, bool permanent, Point? ownerPosition = null)
    {
        if (_activeInlineSection is not null && !SaveInlineMarkdownEdit(_activeInlineSection)) return;
        var editorKey = permanent ? "permanent" : $"date:{CalendarMath.Key(date)}";
        if (!_markdownEditors.TryPrepareToOpen(editorKey)) return;

        var existing = GetMarkdownDocument(date, permanent);
        var dialog = NewEventDialog(string.Empty, ownerPosition);
        var previewsToday = !permanent && date.Date == _today.Date;
        var previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); RefreshTodayMarkdownPreview(); };
        dialog.Closed += (_, _) =>
        {
            previewTimer.Stop();
            _markdownEditors.Clear(editorKey);
            if (!previewsToday) return;
            _todayMarkdownPreview = null;
            _todayMarkdownPreviewSink = null;
            RefreshTodayMarkdownPreview();
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

        var normalEditor = CreateMarkdownEditor();
        normalEditor.SetMarkdown(existing);
        Grid.SetRow(normalEditor, 1); root.Children.Add(normalEditor);

        if (previewsToday)
        {
            _todayMarkdownPreview = existing;
            _todayMarkdownPreviewSink = ApplyExternalTodayDraft;
            RefreshTodayMarkdownPreview();
        }

        normalEditor.ContentChanged += (_, _) =>
        {
            PublishTodayPreview();
        };
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var formats = CreateMarkdownFormatTools(normalEditor);
        Grid.SetColumn(formats, 0); toolbar.Children.Add(formats);
        var close = DialogCloseButton(dialog, 28, 20, 0, InputBackground());
        var mode = ToolbarButton(Localization.T("markdown.preview"), Localization.T("markdown.preview"), 64);
        mode.Click += (_, _) => SetReadingMode(!normalEditor.IsPreviewMode);
        normalEditor.EditRequested += offset => SetReadingMode(false, offset);
        var viewActions = new StackPanel { Orientation = Orientation.Horizontal };
        viewActions.Children.Add(mode);
        viewActions.Children.Add(close);
        Grid.SetColumn(viewActions, 1); toolbar.Children.Add(viewActions);
        Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

        void SetReadingMode(bool reading, int? offset = null)
        {
            normalEditor.SetPreviewMode(reading, offset);
            formats.IsEnabled = !reading;
            mode.Content = Localization.T(reading ? "markdown.editSource" : "markdown.preview");
            mode.ToolTip = mode.Content;
            if (!reading) normalEditor.FocusEditor();
        }

        void PublishTodayPreview()
        {
            if (!previewsToday) return;
            _todayMarkdownPreview = normalEditor.GetMarkdown();
            if (!previewTimer.IsEnabled) previewTimer.Start();
        }

        void ApplyExternalTodayDraft(string markdown)
        {
            normalEditor.UpdateMarkdown(markdown);
        }

        bool SaveAndClose(string markdown)
        {
            var previous = GetMarkdownDocument(date, permanent);
            SetMarkdownDocument(date, permanent, markdown);
            if (!_dataStore.Save(_state))
            {
                SetMarkdownDocument(date, permanent, previous);
                System.Windows.MessageBox.Show(dialog, Localization.T("markdown.saveError"), Localization.T("dialog.calendar"), MessageBoxButton.OK, MessageBoxImage.Warning);
                dialog.Activate();
                return false;
            }

            dialog.Close();
            Render();
            return true;
        }

        bool SaveDraftAndClose() => SaveAndClose(normalEditor.GetMarkdown());

        var actions = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(existing))
        {
            var clear = Button(Localization.T("markdown.clear"), Localization.T("markdown.clearTooltip"), 95);
            clear.Height = 38; clear.Margin = new Thickness(0); clear.Padding = new Thickness(0);
            clear.HorizontalAlignment = HorizontalAlignment.Left; clear.HorizontalContentAlignment = HorizontalAlignment.Left;
            clear.Foreground = WorkBrush(); clear.Background = Brushes.Transparent; clear.BorderBrush = Brushes.Transparent;
            clear.BorderThickness = new Thickness(0); clear.Template = RoundedButtonTemplate(true);
            clear.Click += (_, _) => SaveAndClose(string.Empty);
            actions.Children.Add(clear);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button(Localization.T("common.cancel"), Localization.T("common.close"), 70);
        cancel.Height = 38; cancel.Background = InputBackground(); cancel.Click += (_, _) => dialog.Close();
        var save = Button(Localization.T("markdown.save"), Localization.T("markdown.saveTooltip"), 90);
        save.Height = 38; save.Margin = new Thickness(8, 2, 0, 0); save.Background = Accent(); save.Foreground = Brushes.White; save.BorderBrush = Accent();
        save.Click += (_, _) => SaveDraftAndClose();
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1); actions.Children.Add(buttons);
        Grid.SetRow(actions, 2); root.Children.Add(actions);

        dialog.Content = DialogSurface(root, DialogPadding());
        _markdownEditors.Register(editorKey, () => dialog.Activate(), SaveDraftAndClose);
        dialog.Show();
    }

    private string GetMarkdownDocument(DateTime date, bool permanent)
    {
        if (permanent) return _state.PermanentMarkdown;
        return _state.DateMarkdown.TryGetValue(CalendarMath.Key(date), out var markdown) ? markdown : string.Empty;
    }

    private void SetMarkdownDocument(DateTime date, bool permanent, string markdown)
    {
        if (permanent)
        {
            _state.PermanentMarkdown = markdown;
            return;
        }
        var key = CalendarMath.Key(date);
        if (markdown.Length == 0) _state.DateMarkdown.Remove(key);
        else _state.DateMarkdown[key] = markdown;
    }

    private Button ToolbarButton(string label, string toolTip, double width)
    {
        var button = Button(label, toolTip, width);
        button.Height = 30;
        button.Margin = new Thickness(0, 0, 5, 0);
        button.Padding = new Thickness(4, 0, 4, 0);
        button.FontSize = label.StartsWith('H') ? 11 : 13;
        button.Background = InputBackground();
        return button;
    }

    private WrapPanel CreateMarkdownFormatTools(MarkdownBlockEditor editor)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var item in new[] { ("T", "markdown.paragraph", MarkdownLineKind.Paragraph), ("H", "markdown.heading2", MarkdownLineKind.Heading2), ("•", "markdown.list", MarkdownLineKind.Bullet), ("☑", "markdown.task", MarkdownLineKind.Task) })
        {
            var button = FormatToolButton(item.Item1, Localization.T(item.Item2));
            button.Click += (_, _) => editor.ApplyKind(item.Item3);
            panel.Children.Add(button);
        }
        var bold = FormatToolButton("B", Localization.T("markdown.bold"));
        bold.FontWeight = FontWeights.Bold;
        bold.Click += (_, _) => editor.ToggleBold();
        panel.Children.Insert(2, bold);
        return panel;
    }

    private Button FormatToolButton(string label, string toolTip)
    {
        var button = ToolbarButton(label, toolTip, 30);
        button.Height = 30;
        button.Padding = new Thickness(0);
        button.Margin = new Thickness(0, 0, 2, 2);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Top;
        return button;
    }

    private static string[] DroppedImageFiles(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return Array.Empty<string>();
        return Array.FindAll(files, MarkdownImageStore.IsSupportedSourceFile);
    }

    private static bool ContainsClipboardImage(System.Windows.IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.Bitmap)) return true;
        return DroppedImageFiles(data).Length > 0;
    }
}
