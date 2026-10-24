using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrewCalendar;

public sealed partial class MainWindow
{
    private bool TryPersistMarkdown(DateTime date, bool permanent, RepeatDocument draft, Window? owner = null)
    {
        if (RepeatDocumentService.TrySave(_state, permanent ? null : CalendarMath.Key(date), draft, _dataStore.Save)) return true;
        System.Windows.MessageBox.Show(owner ?? this, Localization.T("markdown.saveError"), Localization.T("dialog.calendar"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool SaveMarkdownEditor(DateTime date, bool permanent, MarkdownBlockEditor editor, Window? owner = null)
    {
        var draft = editor.GetRepeatDraft();
        if (!TryPersistMarkdown(date, permanent, draft, owner)) return false;
        var saved = GetRepeatDocument(date, permanent);
        editor.AcceptRepeatSave(saved.Markdown == draft.Markdown ? saved.Bindings :
            draft.Bindings.Where(binding => _state.RepeatBlocks.ContainsKey(binding.Id)).ToArray());
        return true;
    }

    private RepeatDocument GetRepeatDocument(DateTime date, bool permanent) => permanent
        ? new(_state.PermanentMarkdown, []) : RepeatDocumentService.Load(_state, CalendarMath.Key(date));

    private void LoadMarkdownEditor(MarkdownBlockEditor editor, DateTime date, bool permanent) =>
        editor.LoadRepeatDocument(permanent ? null : date, GetRepeatDocument(date, permanent));

    private StackPanel RenderMarkdown(RepeatDocument document)
    {
        var panel = new StackPanel();
        foreach (var summary in MarkdownDocumentService.Parse(RepeatDocumentService.VisibleSource(document)).SummaryLines(3))
        {
            var text = Text(summary, 14, Foreground());
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.TextWrapping = TextWrapping.NoWrap;
            text.MaxHeight = 17;
            text.Margin = new Thickness(0, 0, 0, 1);
            panel.Children.Add(text);
        }
        return panel;
    }

    private MarkdownBlockEditor CreateMarkdownEditor(bool preview = false)
    {
        var editor = new MarkdownBlockEditor(Foreground(), Muted(), InputBackground(), Line(), Accent(), _uiFont, LoadMarkdownImage, MarkdownSelectionBrush())
        { SurfaceOpacity = _state.Opacity };
        editor.SetPreviewMode(preview);
        AttachMarkdownImageInput(editor);
        return editor;
    }

    private void AttachMarkdownImageInput(MarkdownBlockEditor editor)
    {
        bool PasteImages()
        {
            if (editor.IsPreviewMode) return false;
            var images = SaveClipboardImages();
            foreach (var image in images) editor.AddImage(image.Path, image.AltText);
            return images.Count > 0;
        }
        DataObject.AddPastingHandler(editor, (_, args) =>
        {
            if (!editor.IsPreviewMode && ContainsClipboardImage(args.SourceDataObject) && PasteImages()) args.CancelCommand();
        });
        editor.PreviewKeyDown += (_, args) =>
        {
            if (!editor.IsPreviewMode && args.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && PasteImages()) args.Handled = true;
        };
        editor.AllowDrop = true;
        editor.PreviewDragOver += (_, args) =>
        {
            args.Effects = !editor.IsPreviewMode && (args.Data.GetDataPresent(DataFormats.Bitmap) || DroppedImageFiles(args.Data).Length > 0)
                ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        };
        editor.PreviewDrop += (_, args) =>
        {
            if (editor.IsPreviewMode) { args.Handled = true; return; }
            var files = DroppedImageFiles(args.Data);
            foreach (var file in files)
            {
                var stored = SaveDroppedImage(file);
                if (stored is not null) editor.AddImage(stored, Path.GetFileNameWithoutExtension(file));
            }
            if (files.Length == 0 && args.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                var stored = SaveDroppedImage(bitmap);
                if (stored is not null) editor.AddImage(stored);
            }
            args.Handled = true;
        };
    }

    private BitmapSource? LoadMarkdownImage(string relativePath)
    {
        try
        {
            return _imageStore.Load(relativePath);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to load a Markdown image.", exception);
            return null;
        }
    }

    private IReadOnlyList<(string Path, string AltText)> SaveClipboardImages()
    {
        var stored = new List<(string Path, string AltText)>();
        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null) stored.Add((_imageStore.Save(image), "image"));
                return stored;
            }

            if (!Clipboard.ContainsFileDropList()) return stored;
            foreach (var sourcePath in Clipboard.GetFileDropList())
            {
                if (!File.Exists(sourcePath) || !MarkdownImageStore.IsSupportedSourceFile(sourcePath)) continue;
                stored.Add((_imageStore.Import(sourcePath), Path.GetFileNameWithoutExtension(sourcePath)));
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to save an image pasted into Markdown.", exception);
        }
        return stored;
    }

    private string? SaveDroppedImage(string sourcePath)
    {
        try
        {
            return _imageStore.Import(sourcePath);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Failed to import a dropped Markdown image: {sourcePath}", exception);
            return null;
        }
    }

    private string? SaveDroppedImage(BitmapSource image)
    {
        try
        {
            return _imageStore.Save(image);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Failed to import a dropped Markdown bitmap.", exception);
            return null;
        }
    }
}
