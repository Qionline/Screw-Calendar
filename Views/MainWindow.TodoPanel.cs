using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace ScrewCalendar;

// Optional Markdown todo panel shown below the independently scaled calendar.
public sealed partial class MainWindow
{
    private Grid BuildTodoPanel()
    {
        var layer = new Grid { Background = Brushes.Transparent };
        layer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CalendarLayout.TodoResizeGripHeight) });

        _todoPanel = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(_todoPanel, 0);
        layer.Children.Add(_todoPanel);

        _todoResizeGrip = new Border
        {
            Height = CalendarLayout.TodoResizeGripHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new Border
            {
                Width = 48,
                Height = 3,
                CornerRadius = new CornerRadius(2),
                Background = Line(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _todoResizeGrip.MouseLeftButtonDown += TodoResizeGripMouseDown;
        _todoResizeGrip.MouseMove += TodoResizeGripMouseMove;
        _todoResizeGrip.MouseLeftButtonUp += TodoResizeGripMouseUp;
        Grid.SetRow(_todoResizeGrip, 1);
        layer.Children.Add(_todoResizeGrip);
        return layer;
    }

    private sealed class TodoMarkdownSection
    {
        public required bool Permanent { get; init; }
        public required DateTime Date { get; init; }
        public required Grid Root { get; init; }
        public required MarkdownBlockEditor Editor { get; init; }
        public required TextBlock EmptyHint { get; init; }
        public bool Editing => !Editor.IsPreviewMode;
    }

    private readonly Grid _todoLayout = new();
    private TodoMarkdownSection? _permanentSection;
    private TodoMarkdownSection? _todaySection;
    private TodoMarkdownSection? _activeInlineSection;
    private bool _todoLayoutHasToday;

    private void RenderTodoPanel()
    {
        var visible = _state.ShowTodoPanel;
        _todoPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _todoResizeGrip.Visibility = visible && !_state.Locked ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { EndTodoEditorInteraction(); return; }
        if (_activeInlineSection is { Editing: true } && !_todoEditorActive)
        {
            _todoEditorActive = true;
            _windowLayerController.SetInteractive(true);
        }
        UpdateTodoPanelWidth();
        _todoPanel.Background = new SolidColorBrush(SurfaceColor(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(32, 37, 44) : Color.FromRgb(248, 250, 252)));
        _todoPanel.BorderBrush = Line();
        _permanentSection ??= BuildMarkdownSection(_today.Date, true);
        if (_todaySection is null || _todaySection.Date != _today.Date && !_todaySection.Editing)
            _todaySection = BuildMarkdownSection(_today.Date, false);
        foreach (var section in new[] { _permanentSection, _todaySection })
        {
            section.Editor.UpdateAppearance(Foreground(), Muted(), InputBackground(), Line(), Accent(), MarkdownSelectionBrush());
            section.EmptyHint.Foreground = Muted();
            if (!section.Editing) section.Editor.UpdateMarkdown(SectionMarkdown(section));
            ConfigureSectionHeader(section);
            UpdateSectionState(section);
        }
        RebuildTodoColumns();
    }

    private string SectionMarkdown(TodoMarkdownSection section) =>
        !section.Permanent && section.Date == _today.Date && _todayMarkdownPreview is not null
            ? _todayMarkdownPreview : GetMarkdownDocument(section.Date, section.Permanent);

    // A draft update touches the existing reader. The surrounding panel changes only when
    // today's document transitions between empty/nonempty, or the external editing session changes.
    private void RefreshTodayMarkdownPreview()
    {
        if (!_state.ShowTodoPanel) return;
        if (_todaySection is null || _permanentSection is null) { RenderTodoPanel(); return; }
        if (!_todaySection.Editing) _todaySection.Editor.UpdateMarkdown(SectionMarkdown(_todaySection));
        UpdateSectionState(_todaySection);
        var hasToday = _todaySection.Editing || !string.IsNullOrWhiteSpace(SectionMarkdown(_todaySection));
        if (hasToday != _todoLayoutHasToday) RebuildTodoColumns();
        ConfigureSectionHeader(_permanentSection);
    }

    private void RebuildTodoColumns()
    {
        if (_permanentSection is null || _todaySection is null) return;
        _todoLayout.Children.Clear();
        _todoLayout.ColumnDefinitions.Clear();
        _todoLayoutHasToday = _todaySection.Editing || !string.IsNullOrWhiteSpace(SectionMarkdown(_todaySection));
        _todoLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_permanentSection.Root, 0);
        _todoLayout.Children.Add(_permanentSection.Root);
        if (_todoLayoutHasToday)
        {
            _todoLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _todoLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var divider = new Border { Width = 1, Margin = new Thickness(12, 4, 12, 4), Background = Line(), Opacity = .7 };
            Grid.SetColumn(divider, 1);
            _todoLayout.Children.Add(divider);
            Grid.SetColumn(_todaySection.Root, 2);
            _todoLayout.Children.Add(_todaySection.Root);
        }
        ConfigureSectionHeader(_permanentSection);
        _todoPanel.Child = _todoLayout;
    }

    private void UpdateTodoPanelWidth()
    {
        if (_calendarDisplayWidth <= 0) return;
        _todoPanel.Width = CalendarComponentSizing.TodoPanelWidth(_calendarDisplayWidth, _state.Style == CalendarStyle.Widget);
    }

    private TodoMarkdownSection BuildMarkdownSection(DateTime date, bool permanent)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var editor = CreateMarkdownEditor(true);
        editor.SetMarkdown(GetMarkdownDocument(date, permanent));
        var content = new Grid();
        content.Children.Add(editor);
        var empty = Text(Localization.T(permanent ? "todo.emptyPermanent" : "todo.emptyToday"), 14, Muted());
        empty.VerticalAlignment = VerticalAlignment.Top;
        empty.Margin = new Thickness(5, 5, 0, 0);
        empty.Cursor = Cursors.Hand;
        content.Children.Add(empty);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        var section = new TodoMarkdownSection { Permanent = permanent, Date = date, Root = root, Editor = editor, EmptyHint = empty };
        empty.MouseLeftButtonDown += (_, args) => { args.Handled = true; BeginInlineMarkdownEdit(date, permanent, 0); };
        editor.EditRequested += offset => BeginInlineMarkdownEdit(date, permanent, offset);
        editor.ContentChanged += (_, _) =>
        {
            if (section.Editing) return; // Closing the editor or Ctrl+S persists the draft.
            if (!TryPersistMarkdown(section.Date, section.Permanent, editor.GetMarkdown()))
                editor.UpdateMarkdown(GetMarkdownDocument(section.Date, section.Permanent));
            else RefreshMarkdownDateCell(section.Date, section.Permanent);
        };
        editor.PreviewKeyDown += (_, args) =>
        {
            if (!section.Editing) return;
            if (args.Key == Key.Escape) { CancelInlineMarkdownEdit(section); args.Handled = true; }
            else if (args.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            { SaveInlineMarkdownEdit(section); args.Handled = true; }
        };
        UpdateSectionState(section);
        return section;
    }

    private void ConfigureSectionHeader(TodoMarkdownSection section)
    {
        var old = section.Root.Children.Count > 1 ? section.Root.Children[1] : null;
        if (old is not null) section.Root.Children.Remove(old);
        var header = new Grid { MinHeight = 34, Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (section.Editing)
        {
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var formats = CreateMarkdownFormatTools(section.Editor);
            header.Children.Add(formats);
            var close = FormatToolButton("×", Localization.T("markdown.closeAndSave"));
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Click += (_, _) => SaveInlineMarkdownEdit(section);
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            header.SizeChanged += (_, _) =>
            {
                // Wrap/reflow at narrow widths instead of shrinking the format buttons.
                var narrow = header.ActualWidth < 230;
                Grid.SetColumnSpan(formats, narrow ? 2 : 1);
                Grid.SetRow(close, narrow ? 1 : 0);
                Grid.SetColumn(close, narrow ? 0 : 1);
                Grid.SetColumnSpan(close, narrow ? 2 : 1);
            };
        }
        else
        {
            var title = Text(section.Permanent ? Localization.T("todo.permanent") : Localization.T("todo.today", FormatDay(section.Date)), 15, FontWeights.SemiBold, Foreground());
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(title);
            if (section.Permanent && !_todoLayoutHasToday)
            {
                var today = HeaderActionButton(Localization.T("todo.editToday"));
                today.IsEnabled = _todayMarkdownPreviewSink is null;
                today.Click += (_, _) => BeginInlineMarkdownEdit(_today.Date, false);
                Grid.SetColumn(today, 1);
                header.Children.Add(today);
            }
        }
        section.Root.Children.Add(header);
    }

    private void UpdateSectionState(TodoMarkdownSection section)
    {
        section.EmptyHint.Text = Localization.T(section.Permanent ? "todo.emptyPermanent" : "todo.emptyToday");
        section.EmptyHint.Visibility = !section.Editing && string.IsNullOrWhiteSpace(section.Editor.GetMarkdown()) ? Visibility.Visible : Visibility.Collapsed;
        var external = !section.Permanent && section.Date == _today.Date && _todayMarkdownPreviewSink is not null;
        section.Root.IsEnabled = !external;
        section.Root.Opacity = external ? .58 : 1;
        section.Editor.CanToggleTasks = !external;
    }

    private void BeginInlineMarkdownEdit(DateTime date, bool permanent, int? caretOffset = null)
    {
        if (!permanent && date.Date == _today.Date && _todayMarkdownPreviewSink is not null) return;
        if (_permanentSection is null || _todaySection is null) RenderTodoPanel();
        var section = permanent ? _permanentSection! : _todaySection!;
        if (_activeInlineSection is not null && _activeInlineSection != section && !SaveInlineMarkdownEdit(_activeInlineSection)) return;
        _activeInlineSection = section;
        _todoEditorActive = true;
        _windowLayerController.SetInteractive(true);
        section.Editor.SetPreviewMode(false, caretOffset);
        ConfigureSectionHeader(section);
        UpdateSectionState(section);
        if (!permanent && !_todoLayoutHasToday) RebuildTodoColumns();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(section.Editor.FocusEditor));
    }

    private bool SaveInlineMarkdownEdit(TodoMarkdownSection section)
    {
        if (!TryPersistMarkdown(section.Date, section.Permanent, section.Editor.GetMarkdown())) return false;
        RefreshMarkdownDateCell(section.Date, section.Permanent);
        EndInlineMarkdownEdit(section);
        return true;
    }

    private void CancelInlineMarkdownEdit(TodoMarkdownSection section)
    {
        section.Editor.DiscardDraft(GetMarkdownDocument(section.Date, section.Permanent));
        EndInlineMarkdownEdit(section);
    }

    private void EndInlineMarkdownEdit(TodoMarkdownSection section)
    {
        section.Editor.SetPreviewMode(true);
        _activeInlineSection = null;
        EndTodoEditorInteraction();
        RenderTodoPanel();
    }

    private void RefreshMarkdownDateCell(DateTime date, bool permanent)
    {
        if (permanent || !_dateCells.TryGetValue(CalendarMath.Key(date), out var old) || old.Parent is not Panel parent) return;
        var index = parent.Children.IndexOf(old);
        var replacement = BuildDateCell(date);
        Grid.SetRow(replacement, Grid.GetRow(old));
        Grid.SetColumn(replacement, Grid.GetColumn(old));
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, replacement);
        _dateCells[CalendarMath.Key(date)] = replacement;
    }

    private void EndTodoEditorInteraction()
    {
        if (!_todoEditorActive) return;
        _todoEditorActive = false;
        _windowLayerController.SetInteractive(false);
    }

    private Button HeaderActionButton(string label, string? toolTip = null, double width = 88)
    {
        var button = Button(label, toolTip ?? label, width);
        button.Height = 28;
        button.Margin = new Thickness(3, 0, 0, 0);
        button.Padding = new Thickness(2, 0, 2, 0);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Foreground = Accent();
        return button;
    }
}
