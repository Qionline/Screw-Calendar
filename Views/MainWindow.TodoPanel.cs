using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using IOPath = System.IO.Path;
using Path = System.Windows.Shapes.Path;

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

    private void RenderTodoPanel()
    {
        EndTodoEditorInteraction();
        var visible = _state.ShowTodoPanel;
        _todoPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _todoResizeGrip.Visibility = visible && !_state.Locked ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _todoPanel.Child = null;
            return;
        }

        UpdateTodoPanelWidth();
        _todoPanel.Background = new SolidColorBrush(SurfaceColor(_state.Theme == CalendarTheme.Dark ? Color.FromRgb(32, 37, 44) : Color.FromRgb(248, 250, 252)));
        _todoPanel.BorderBrush = Line();
        var today = _today.Date;
        var todayMarkdown = _todayMarkdownPreview ?? GetMarkdownDocument(today, false);

        if (string.IsNullOrWhiteSpace(todayMarkdown))
        {
            _todoPanel.Child = BuildMarkdownSection(
                Localization.T("todo.permanent"),
                _state.PermanentMarkdown,
                today,
                true,
                Localization.T("todo.editToday"));
            return;
        }

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = BuildMarkdownSection(Localization.T("todo.permanent"), _state.PermanentMarkdown, today, true);
        var divider = new Border
        {
            Width = 1,
            Margin = new Thickness(12, 4, 12, 4),
            Background = Line(),
            Opacity = .7
        };
        var right = BuildMarkdownSection(Localization.T("todo.today", FormatDay(today)), todayMarkdown, today, false);
        Grid.SetColumn(left, 0); columns.Children.Add(left);
        Grid.SetColumn(divider, 1); columns.Children.Add(divider);
        Grid.SetColumn(right, 2); columns.Children.Add(right);
        _todoPanel.Child = columns;
    }

    private void UpdateTodoPanelWidth()
    {
        if (_calendarDisplayWidth <= 0) return;
        _todoPanel.Width = CalendarComponentSizing.TodoPanelWidth(
            _calendarDisplayWidth,
            _state.Style == CalendarStyle.Widget);
    }

    private Grid BuildMarkdownSection(string heading, string markdown, DateTime date, bool permanent, string? secondaryAction = null)
    {
        var section = new Grid();
        section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Text(heading, 15, FontWeights.SemiBold, Foreground()));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (secondaryAction is not null)
        {
            var today = HeaderActionButton(secondaryAction);
            today.Click += (_, _) => BeginInlineMarkdownEdit(date, false);
            actions.Children.Add(today);
        }
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        section.Children.Add(header);

        UIElement content;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            var empty = new Border
            {
                Width = 220,
                Height = 32,
                Padding = new Thickness(5, 0, 5, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = Text(permanent ? Localization.T("todo.emptyPermanent") : Localization.T("todo.emptyToday"), 14, Muted())
            };
            empty.MouseLeftButtonDown += (_, e) => { e.Handled = true; BeginInlineMarkdownEdit(date, permanent); };
            content = empty;
        }
        else
        {
            var rendered = RenderSelectableMarkdown(markdown, (taskIndex, completed) => ToggleMarkdownTask(date, permanent, taskIndex, completed));
            rendered.Cursor = Cursors.Hand;
            rendered.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsTodoInteractiveSource(e.OriginalSource)) return;
                e.Handled = true;
                BeginInlineMarkdownEdit(date, permanent);
            };
            content = rendered;
        }
        Grid.SetRow(content, 1); section.Children.Add(content);
        return section;
    }

    private Button HeaderActionButton(string label, string? toolTip = null, double width = 88)
    {
        var button = Button(label, toolTip ?? label, width);
        button.Height = 28;
        button.Margin = new Thickness(5, 0, 0, 0);
        button.Padding = new Thickness(5, 0, 5, 0);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Foreground = Accent();
        return button;
    }

    private void BeginInlineMarkdownEdit(DateTime date, bool permanent)
    {
        _todoEditorActive = true;
        _windowLayerController.SetInteractive(true);
        var editor = new MarkdownBlockEditor(Foreground(), Muted(), InputBackground(), Line(), Accent(), _uiFont, LoadMarkdownImage);
        editor.SetMarkdown(GetMarkdownDocument(date, permanent));

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var formats = new StackPanel { Orientation = Orientation.Horizontal };
        AddFormatButton("T", Localization.T("markdown.paragraph"), MarkdownLineKind.Paragraph);
        AddFormatButton("H1", Localization.T("markdown.heading1"), MarkdownLineKind.Heading1);
        AddFormatButton("H2", Localization.T("markdown.heading2"), MarkdownLineKind.Heading2);
        AddFormatButton("H3", Localization.T("markdown.heading3"), MarkdownLineKind.Heading3);
        AddFormatButton("•", Localization.T("markdown.list"), MarkdownLineKind.Bullet);
        AddFormatButton("☑", Localization.T("markdown.task"), MarkdownLineKind.Task);
        toolbar.Children.Add(formats);

        var save = IconHeaderButton(
            "M5,3H17L21,7V21H3V3H5M7,5V10H17V5H7M7,14V19H17V14H7Z",
            Localization.T("markdown.save"),
            32,
            Brushes.White);
        save.Background = Accent();
        save.BorderBrush = Accent();
        save.Click += (_, _) =>
        {
            SetMarkdownDocument(date, permanent, editor.GetMarkdown().Trim());
            SaveState();
            Render();
        };
        Grid.SetColumn(save, 1); toolbar.Children.Add(save);
        host.Children.Add(toolbar);
        Grid.SetRow(editor, 1); host.Children.Add(editor);

        DataObject.AddPastingHandler(editor, PasteImage);
        editor.PreviewKeyDown += PasteImageShortcut;
        editor.AllowDrop = true;
        editor.PreviewDragOver += PreviewImageDragOver;
        editor.PreviewDrop += DropImages;

        _todoPanel.Child = host;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(editor.FocusEditor));

        void AddFormatButton(string label, string toolTip, MarkdownLineKind kind)
        {
            var button = ToolbarButton(label, toolTip, label.Length > 1 ? 38 : 34);
            button.Margin = new Thickness(0, 0, 3, 0);
            button.Click += (_, _) => editor.ApplyKind(kind);
            formats.Children.Add(button);
        }

        bool InsertClipboardImages()
        {
            var images = SaveClipboardImages();
            foreach (var item in images) editor.AddImage(item.Path, item.AltText);
            return images.Count > 0;
        }

        void PasteImage(object sender, DataObjectPastingEventArgs args)
        {
            if (!ContainsClipboardImage(args.SourceDataObject) || !InsertClipboardImages()) return;
            args.CancelCommand();
        }

        void PasteImageShortcut(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && InsertClipboardImages())
                args.Handled = true;
        }

        void PreviewImageDragOver(object sender, DragEventArgs args)
        {
            args.Effects = args.Data.GetDataPresent(DataFormats.Bitmap) || DroppedImageFiles(args.Data).Length > 0
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            args.Handled = true;
        }

        void DropImages(object sender, DragEventArgs args)
        {
            var files = DroppedImageFiles(args.Data);
            foreach (var sourcePath in files)
            {
                var storedPath = SaveDroppedImage(sourcePath);
                if (storedPath is not null) editor.AddImage(storedPath, IOPath.GetFileNameWithoutExtension(sourcePath));
            }
            if (files.Length == 0 && args.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var storedPath = SaveDroppedImage(bitmap);
                if (storedPath is not null) editor.AddImage(storedPath);
            }
            args.Handled = true;
        }
    }

    private void EndTodoEditorInteraction()
    {
        if (!_todoEditorActive) return;
        _todoEditorActive = false;
        _windowLayerController.SetInteractive(false);
    }

    private Button IconHeaderButton(string geometry, string toolTip, double width, Brush fill)
    {
        var button = HeaderActionButton(string.Empty, toolTip, width);
        button.Content = new Path
        {
            Data = Geometry.Parse(geometry),
            Fill = fill,
            Stretch = Stretch.Uniform,
            Width = 15,
            Height = 15
        };
        return button;
    }

    private void ToggleMarkdownTask(DateTime date, bool permanent, int taskIndex, bool completed)
    {
        var isTodayPreview = !permanent && date.Date == _today.Date && _todayMarkdownPreview is not null;
        var markdown = isTodayPreview ? _todayMarkdownPreview! : GetMarkdownDocument(date, permanent);
        var updated = MarkdownDocumentService.ToggleTask(markdown, taskIndex, completed);
        if (string.Equals(markdown, updated, StringComparison.Ordinal)) return;
        if (isTodayPreview)
        {
            _todayMarkdownPreview = updated;
            _todayMarkdownPreviewSink?.Invoke(updated);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, new Action(() =>
            {
                if (string.Equals(_todayMarkdownPreview, updated, StringComparison.Ordinal))
                    RenderTodoPanel();
            }));
            return;
        }
        SetMarkdownDocument(date, permanent, updated);
        SaveState();
        Render();
    }

    private static bool IsTodoInteractiveSource(object source)
    {
        if (source is not DependencyObject current) return false;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (current is CheckBox or System.Windows.Controls.Button or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
