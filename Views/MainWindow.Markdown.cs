using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Brushes = System.Windows.Media.Brushes;

namespace ScrewCalendar;

// Native WPF rendering and local-image handling for the task-focused Markdown subset.
public sealed partial class MainWindow
{
    private StackPanel RenderMarkdown(string markdown, bool compact, Action<int, bool>? toggleTask = null)
    {
        var panel = new StackPanel();
        var taskIndex = 0;
        var shown = 0;
        foreach (var line in MarkdownDocumentService.ParseLines(markdown))
        {
            if (compact && shown >= 3) break;
            UIElement element;
            if (line.Kind == MarkdownLineKind.Image)
            {
                if (compact)
                {
                    element = Text(Localization.T("markdown.image"), 11, Muted());
                }
                else
                {
                    element = new Image
                    {
                        Source = line.ImagePath is null ? null : LoadMarkdownImage(line.ImagePath),
                        MaxHeight = 320,
                        Stretch = Stretch.Uniform,
                        StretchDirection = StretchDirection.DownOnly,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 5, 0, 8)
                    };
                }
            }
            else if (line.Kind == MarkdownLineKind.Task)
            {
                var currentTask = taskIndex++;
                var row = new Grid { Margin = new Thickness(line.Indent * (compact ? 7 : 16), compact ? 0 : 2, 0, compact ? 1 : 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 18 : 28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var checkbox = new CheckBox
                {
                    IsChecked = line.IsChecked,
                    IsHitTestVisible = !compact && toggleTask is not null,
                    Focusable = !compact && toggleTask is not null,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = Localization.T("todo.complete"),
                    Style = Resource<Style>("ToolkitRoundedCheckBoxStyle")
                };
                if (!compact && toggleTask is not null)
                {
                    checkbox.Checked += (_, _) => toggleTask(currentTask, true);
                    checkbox.Unchecked += (_, _) => toggleTask(currentTask, false);
                }
                var text = InlineMarkdown(line.Text, 14, line.IsChecked ? Muted() : Foreground());
                if (line.IsChecked) text.TextDecorations = TextDecorations.Strikethrough;
                text.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(checkbox);
                Grid.SetColumn(text, 1); row.Children.Add(text);
                element = row;
            }
            else
            {
                var prefix = line.Kind == MarkdownLineKind.Bullet ? "•  " : string.Empty;
                var size = compact ? 14 : line.Kind switch
                {
                    MarkdownLineKind.Heading1 => 22,
                    MarkdownLineKind.Heading2 => 18,
                    MarkdownLineKind.Heading3 => 16,
                    _ => 14
                };
                var text = InlineMarkdown(prefix + line.Text, size, Foreground());
                text.FontWeight = line.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? FontWeights.SemiBold : FontWeights.Normal;
                text.Margin = new Thickness(line.Indent * (compact ? 7 : 16), compact ? 0 : 2, 0, compact ? 1 : 4);
                text.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
                text.MaxHeight = compact ? 17 : double.PositiveInfinity;
                element = text;
            }
            panel.Children.Add(element);
            shown++;
        }
        return panel;
    }

    private TextBlock InlineMarkdown(string source, double fontSize, Brush foreground)
    {
        var text = new TextBlock { FontSize = fontSize, Foreground = foreground, TextWrapping = TextWrapping.Wrap };
        AppendInlineMarkdown(text.Inlines, source);
        return text;
    }

    private void AppendInlineMarkdown(InlineCollection target, string source)
    {
        var document = Markdown.Parse(source, MarkdownDocumentService.Pipeline);
        var container = document.OfType<LeafBlock>().Select(block => block.Inline).FirstOrDefault(inline => inline is not null);
        if (container is null)
        {
            target.Add(new Run(source));
            return;
        }
        AppendInlines(target, container.FirstChild);
    }

    private RichTextBox RenderSelectableMarkdown(string markdown, Action<int, bool>? toggleTask = null)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = _uiFont,
            Foreground = Foreground()
        };
        var taskIndex = 0;
        foreach (var line in MarkdownDocumentService.ParseLines(markdown))
        {
            if (line.Kind == MarkdownLineKind.Image)
            {
                document.Blocks.Add(new BlockUIContainer(new Image
                {
                    Source = line.ImagePath is null ? null : LoadMarkdownImage(line.ImagePath),
                    MaxHeight = 320,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 5, 0, 8)
                })
                { Margin = new Thickness(0) });
                continue;
            }

            var paragraph = new Paragraph
            {
                Margin = new Thickness(line.Indent * 16, 2, 0, line.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? 6 : 3),
                FontSize = line.Kind switch
                {
                    MarkdownLineKind.Heading1 => 22,
                    MarkdownLineKind.Heading2 => 18,
                    MarkdownLineKind.Heading3 => 16,
                    _ => 14
                },
                FontWeight = line.Kind is MarkdownLineKind.Heading1 or MarkdownLineKind.Heading2 or MarkdownLineKind.Heading3 ? FontWeights.SemiBold : FontWeights.Normal
            };
            if (line.Kind == MarkdownLineKind.Task)
            {
                var currentTask = taskIndex++;
                var checkbox = new CheckBox { IsChecked = line.IsChecked, ToolTip = Localization.T("todo.complete"), Margin = new Thickness(0, 0, 7, -2), Style = Resource<Style>("ToolkitRoundedCheckBoxStyle") };
                if (toggleTask is not null)
                {
                    checkbox.Checked += (_, _) => toggleTask(currentTask, true);
                    checkbox.Unchecked += (_, _) => toggleTask(currentTask, false);
                }
                else checkbox.IsHitTestVisible = false;
                paragraph.Inlines.Add(new InlineUIContainer(checkbox) { BaselineAlignment = BaselineAlignment.Center });
                var taskText = new Span { Foreground = line.IsChecked ? Muted() : Foreground() };
                if (line.IsChecked) taskText.TextDecorations = TextDecorations.Strikethrough;
                AppendInlineMarkdown(taskText.Inlines, line.Text);
                paragraph.Inlines.Add(taskText);
            }
            else
            {
                if (line.Kind == MarkdownLineKind.Bullet) paragraph.Inlines.Add(new Run("•  "));
                AppendInlineMarkdown(paragraph.Inlines, line.Text);
            }
            document.Blocks.Add(paragraph);
        }

        var viewer = new RichTextBox(document)
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = MarkdownSelectionBrush()
        };
        viewer.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), MinimalScrollBarStyle());
        return viewer;
    }

    private void AppendInlines(InlineCollection target, Markdig.Syntax.Inlines.Inline? inline)
    {
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = InputBackground() });
                    break;
                case EmphasisInline emphasis:
                    {
                        var span = new Span();
                        if (emphasis.DelimiterChar == '*' && emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                        else if (emphasis.DelimiterChar is '*' or '_') span.FontStyle = FontStyles.Italic;
                        else if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                        AppendInlines(span.Inlines, emphasis.FirstChild);
                        target.Add(span);
                        break;
                    }
                case LinkInline link when !link.IsImage:
                    {
                        var hyperlink = new Hyperlink { Foreground = Accent(), TextDecorations = null, ToolTip = link.Url };
                        AppendInlines(hyperlink.Inlines, link.FirstChild);
                        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        {
                            hyperlink.NavigateUri = uri;
                            hyperlink.RequestNavigate += (_, args) =>
                            {
                                Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                                args.Handled = true;
                            };
                        }
                        target.Add(hyperlink);
                        break;
                    }
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline nested:
                    AppendInlines(target, nested.FirstChild);
                    break;
            }
        }
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
