using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextBox = System.Windows.Controls.TextBox;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ScrewCalendar;

// One Markdown document per date, plus one permanent Markdown document.
public sealed partial class MainWindow
{
    private void ShowMarkdownEditor(DateTime date, bool permanent, Point? ownerPosition = null)
    {
        var editorKey = permanent ? "permanent" : $"date:{CalendarMath.Key(date)}";
        if (_markdownEditorDialogs.TryGetValue(editorKey, out var existingDialog) && existingDialog.IsVisible)
        {
            existingDialog.Activate();
            return;
        }

        var context = permanent ? Localization.T("todo.permanent") : CalendarMath.Key(date);
        var existing = GetMarkdownDocument(date, permanent);
        var dialog = NewEventDialog(Localization.T("markdown.editTitle"), ownerPosition);
        _markdownEditorDialogs[editorKey] = dialog;
        var previewsToday = !permanent && date.Date == _today.Date;
        dialog.Closed += (_, _) =>
        {
            if (_markdownEditorDialogs.TryGetValue(editorKey, out var current) && ReferenceEquals(current, dialog))
                _markdownEditorDialogs.Remove(editorKey);
            if (!previewsToday) return;
            _todayMarkdownPreview = null;
            _todayMarkdownPreviewSink = null;
            RenderTodoPanel();
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel();
        headerStack.Children.Add(Text(context, 13, Muted()));
        headerStack.Children.Add(Text(Localization.T("markdown.editTitle"), 23, FontWeights.SemiBold, Foreground(), new Thickness(0, 4, 0, 0)));
        header.Children.Add(headerStack);
        var close = DialogCloseButton(dialog, 32, 23, 0);
        Grid.SetColumn(close, 1); header.Children.Add(close);
        root.Children.Add(header);

        var normalEditor = new MarkdownBlockEditor(Foreground(), Muted(), InputBackground(), Line(), Accent(), _uiFont, LoadMarkdownImage);
        normalEditor.SetMarkdown(existing);
        var rawEditor = new TextBox
        {
            Text = existing,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Padding = new Thickness(10, 8, 10, 8),
            Visibility = Visibility.Collapsed
        };
        ApplyInputStyle(rawEditor);
        rawEditor.Resources.Add(typeof(ScrollBar), MinimalScrollBarStyle());

        var editorHost = new Grid();
        editorHost.Children.Add(normalEditor);
        editorHost.Children.Add(rawEditor);
        Grid.SetRow(editorHost, 2); root.Children.Add(editorHost);

        var markdownMode = false;
        if (previewsToday)
        {
            _todayMarkdownPreview = existing;
            _todayMarkdownPreviewSink = ApplyExternalTodayDraft;
        }

        normalEditor.ContentChanged += (_, _) =>
        {
            if (!markdownMode) PublishTodayPreview();
        };
        rawEditor.TextChanged += (_, _) =>
        {
            if (markdownMode) PublishTodayPreview();
        };
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        var normalModeButton = ToolbarButton(Localization.T("markdown.modeNormal"), Localization.T("markdown.modeNormal"), 58);
        var markdownModeButton = ToolbarButton(Localization.T("markdown.modeMarkdown"), Localization.T("markdown.modeMarkdown"), 76);
        normalModeButton.Background = Accent(); normalModeButton.Foreground = Brushes.White;
        modes.Children.Add(normalModeButton); modes.Children.Add(markdownModeButton);
        toolbar.Children.Add(modes);

        var formats = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        AddFormatButton(formats, "T", Localization.T("markdown.paragraph"), MarkdownLineKind.Paragraph);
        AddFormatButton(formats, "H1", Localization.T("markdown.heading1"), MarkdownLineKind.Heading1);
        AddFormatButton(formats, "H2", Localization.T("markdown.heading2"), MarkdownLineKind.Heading2);
        AddFormatButton(formats, "H3", Localization.T("markdown.heading3"), MarkdownLineKind.Heading3);
        AddFormatButton(formats, "•", Localization.T("markdown.list"), MarkdownLineKind.Bullet);
        AddFormatButton(formats, "☑", Localization.T("markdown.task"), MarkdownLineKind.Task);
        Grid.SetColumn(formats, 1); toolbar.Children.Add(formats);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);

        normalModeButton.Click += (_, _) => SwitchMode(false);
        markdownModeButton.Click += (_, _) => SwitchMode(true);
        DataObject.AddPastingHandler(rawEditor, PasteImage);
        DataObject.AddPastingHandler(normalEditor, PasteImage);
        editorHost.PreviewKeyDown += PasteImageShortcut;
        editorHost.AllowDrop = true;
        editorHost.PreviewDragOver += PreviewImageDragOver;
        editorHost.PreviewDrop += DropImages;

        void AddFormatButton(Panel panel, string label, string toolTip, MarkdownLineKind kind)
        {
            var button = ToolbarButton(label, toolTip, label.Length > 1 ? 38 : 34);
            button.Click += (_, _) =>
            {
                if (markdownMode) ApplyRawBlock(rawEditor, kind);
                else normalEditor.ApplyKind(kind);
            };
            panel.Children.Add(button);
        }

        void PublishTodayPreview()
        {
            if (!previewsToday) return;
            _todayMarkdownPreview = markdownMode ? rawEditor.Text : normalEditor.GetMarkdown();
            RenderTodoPanel();
        }

        void ApplyExternalTodayDraft(string markdown)
        {
            if (markdownMode)
            {
                var caret = Math.Min(rawEditor.CaretIndex, markdown.Length);
                rawEditor.Text = markdown;
                rawEditor.CaretIndex = caret;
            }
            else
            {
                normalEditor.SetMarkdown(markdown);
            }
        }

        void SwitchMode(bool useMarkdown)
        {
            if (markdownMode == useMarkdown) return;
            if (useMarkdown) rawEditor.Text = normalEditor.GetMarkdown();
            else normalEditor.SetMarkdown(rawEditor.Text);
            markdownMode = useMarkdown;
            rawEditor.Visibility = useMarkdown ? Visibility.Visible : Visibility.Collapsed;
            normalEditor.Visibility = useMarkdown ? Visibility.Collapsed : Visibility.Visible;
            normalModeButton.Background = useMarkdown ? InputBackground() : Accent();
            normalModeButton.Foreground = useMarkdown ? Foreground() : Brushes.White;
            markdownModeButton.Background = useMarkdown ? Accent() : InputBackground();
            markdownModeButton.Foreground = useMarkdown ? Brushes.White : Foreground();
            if (useMarkdown) rawEditor.Focus();
        }

        void InsertStoredImage(string path, string altText)
        {
            var safeAltText = altText.Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal);
            if (markdownMode) InsertRawText(rawEditor, $"![{safeAltText}]({path})");
            else normalEditor.AddImage(path, safeAltText);
        }

        void PasteImage(object sender, DataObjectPastingEventArgs args)
        {
            if (!ContainsClipboardImage(args.SourceDataObject)) return;
            if (!InsertClipboardImages()) return;
            args.CancelCommand();
        }

        void PasteImageShortcut(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (InsertClipboardImages()) args.Handled = true;
        }

        bool InsertClipboardImages()
        {
            var images = SaveClipboardImages();
            foreach (var item in images) InsertStoredImage(item.Path, item.AltText);
            return images.Count > 0;
        }

        void PreviewImageDragOver(object sender, DragEventArgs args)
        {
            var hasBitmap = args.Data.GetDataPresent(DataFormats.Bitmap);
            args.Effects = hasBitmap || DroppedImageFiles(args.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        }

        void DropImages(object sender, DragEventArgs args)
        {
            foreach (var sourcePath in DroppedImageFiles(args.Data))
            {
                var storedPath = SaveDroppedImage(sourcePath);
                if (storedPath is not null) InsertStoredImage(storedPath, Path.GetFileNameWithoutExtension(sourcePath));
            }
            if (DroppedImageFiles(args.Data).Length == 0 && args.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var storedPath = SaveDroppedImage(bitmap);
                if (storedPath is not null) InsertStoredImage(storedPath, "image");
            }
            args.Handled = true;
        }

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
            clear.Click += (_, _) =>
            {
                SetMarkdownDocument(date, permanent, string.Empty);
                SaveState(); dialog.Close(); Render();
            };
            actions.Children.Add(clear);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button(Localization.T("common.cancel"), Localization.T("common.close"), 70);
        cancel.Height = 38; cancel.Background = InputBackground(); cancel.Click += (_, _) => dialog.Close();
        var save = Button(Localization.T("markdown.save"), Localization.T("markdown.saveTooltip"), 90);
        save.Height = 38; save.Margin = new Thickness(8, 2, 0, 0); save.Background = Accent(); save.Foreground = Brushes.White; save.BorderBrush = Accent();
        save.Click += (_, _) =>
        {
            var markdown = (markdownMode ? rawEditor.Text : normalEditor.GetMarkdown()).Trim();
            SetMarkdownDocument(date, permanent, markdown);
            SaveState(); dialog.Close(); Render();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1); actions.Children.Add(buttons);
        Grid.SetRow(actions, 3); root.Children.Add(actions);

        dialog.Content = DialogSurface(root, DialogPadding());
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
        if (string.IsNullOrWhiteSpace(markdown)) _state.DateMarkdown.Remove(key);
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

    private static void ApplyRawBlock(TextBox editor, MarkdownLineKind kind)
    {
        var lineIndex = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex);
        var lineStart = editor.GetCharacterIndexFromLineIndex(Math.Max(0, lineIndex));
        var lineLength = editor.GetLineLength(Math.Max(0, lineIndex));
        var line = lineLength > 0 ? editor.Text.Substring(lineStart, lineLength).TrimEnd('\r', '\n') : string.Empty;
        var stripped = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*(#{1,3}\s+|[-*+]\s+(\[[ xX]\]\s*)?)", string.Empty);
        var prefix = kind switch
        {
            MarkdownLineKind.Heading1 => "# ",
            MarkdownLineKind.Heading2 => "## ",
            MarkdownLineKind.Heading3 => "### ",
            MarkdownLineKind.Bullet => "- ",
            MarkdownLineKind.Task => "- [ ] ",
            _ => string.Empty
        };
        editor.Select(lineStart, line.Length);
        editor.SelectedText = prefix + stripped;
        editor.CaretIndex = lineStart + prefix.Length + stripped.Length;
        editor.Focus();
    }

    private static void InsertRawText(TextBox editor, string text)
    {
        var start = editor.CaretIndex;
        var insertion = (editor.CaretIndex > 0 && editor.Text[editor.CaretIndex - 1] != '\n' ? Environment.NewLine : string.Empty) + text + Environment.NewLine;
        editor.Select(start, 0);
        editor.SelectedText = insertion;
        editor.CaretIndex = start + insertion.Length;
        editor.Focus();
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
