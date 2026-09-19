using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopWidgets.Toolkit.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace ScrewCalendar;

// Shared dialog shells and layout helpers used by the calendar windows.
public sealed partial class MainWindow
{
    private Window NewDialog(string title, double width, double height, Point? ownerPosition = null)
    {
        var dialog = new Window { Title = title, Owner = this, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = CardBackground(), Foreground = Foreground(), FontFamily = _uiFont, Topmost = Topmost };
        PlaceDialogAt(dialog, ownerPosition);
        PrepareDialogLayer(dialog);
        return dialog;
    }

    private Window NewEventDialog(string title, Point? ownerPosition = null) =>
        NewBorderlessDialog(title, 540, 620, 72, ownerPosition);

    private Window NewSettingsDialog(string title, Point? ownerPosition = null) =>
        NewBorderlessDialog(title, 420, 680, 68, ownerPosition);

    private Window NewBorderlessDialog(string title, double width, double height, double dragHeight, Point? ownerPosition)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            Foreground = Foreground(),
            FontFamily = _uiFont,
            Topmost = Topmost
        };
        PlaceDialogAt(dialog, ownerPosition);
        DialogWindowBehavior.EnableTopDrag(dialog, dragHeight);
        PrepareDialogLayer(dialog);
        return dialog;
    }

    private void PrepareDialogLayer(Window dialog)
    {
        DialogWindowBehavior.ActivateOnShow(dialog, Topmost);
        dialog.Closed += (_, _) => _windowLayerController.Refresh();
    }

    private Button DialogCloseButton(Window dialog, double size, double fontSize, double topMargin, Brush? background = null)
    {
        var close = Button("×", Localization.T("common.close"), size);
        close.Height = size;
        close.FontSize = fontSize;
        close.Padding = new Thickness(0);
        close.Margin = new Thickness(0, topMargin, 0, 0);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.BorderThickness = new Thickness(0);
        if (background is not null) close.Background = background;
        close.Click += (_, _) => { if (dialog.IsVisible) dialog.Close(); };
        return close;
    }

    private Border DialogSurface(UIElement content, Thickness padding) => new()
    {
        Background = DialogBackground(),
        BorderBrush = Line(),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = padding,
        Child = content
    };

    private static Thickness DialogPadding() => new(
        CalendarLayout.DialogHorizontalPadding,
        CalendarLayout.DialogVerticalPadding,
        CalendarLayout.DialogHorizontalPadding,
        CalendarLayout.DialogVerticalPadding);

    private static Thickness SettingsDialogPadding() => new(
        CalendarLayout.SettingsDialogLeftPadding,
        CalendarLayout.DialogVerticalPadding,
        CalendarLayout.DialogHorizontalPadding,
        CalendarLayout.DialogVerticalPadding);

    private void PlaceDialogAt(Window dialog, Point? ownerPosition)
    {
        if (ownerPosition is not Point point) return;

        DialogWindowBehavior.PlaceNearOwnerPoint(dialog, this, point);
    }

    private Grid SettingValueLine(string label, string value, out TextBlock valueText)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(Text(label, 14, Foreground()));
        valueText = Text(value, 14, FontWeights.SemiBold, Accent());
        Grid.SetColumn(valueText, 1); line.Children.Add(valueText);
        return line;
    }

    private RadioButton SettingsRadio(string label, string groupName, object tag)
    {
        var outline = new Ellipse { Width = 18, Height = 18, Stroke = Line(), StrokeThickness = 1.5 };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = Accent(), Visibility = Visibility.Collapsed };
        var bullet = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        bullet.Children.Add(outline);
        bullet.Children.Add(dot);

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(bullet);
        content.Children.Add(Text(label, 14, Foreground(), new Thickness(8, 0, 0, 0)));

        var radio = new RadioButton
        {
            Content = content,
            GroupName = groupName,
            Tag = tag,
            FontSize = 14,
            Foreground = Foreground(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        radio.Template = Resource<ControlTemplate>("ToolkitContentOnlyRadioTemplate");

        void RefreshVisual(bool selected)
        {
            outline.Stroke = selected ? Accent() : Line();
            outline.StrokeThickness = selected ? 2 : 1.5;
            dot.Fill = Accent();
            dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        radio.Checked += (_, _) => RefreshVisual(true);
        radio.Unchecked += (_, _) => RefreshVisual(false);
        RefreshVisual(false);
        return radio;
    }

    private (Grid Host, Ellipse Selection) ColorChoice(object tag, string toolTip, Brush fill, bool selected)
    {
        var host = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 7, 0), Tag = tag, ToolTip = toolTip, Cursor = Cursors.Hand };
        var dot = new Ellipse { Width = 27, Height = 27, Fill = fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var selection = new Ellipse { Width = 33, Height = 33, Stroke = Accent(), StrokeThickness = 2, Visibility = selected ? Visibility.Visible : Visibility.Collapsed };
        host.Children.Add(dot);
        host.Children.Add(selection);
        return (host, selection);
    }

    private static void RefreshChoiceSelections(IEnumerable<(Grid Host, Ellipse Selection)> choices, object selected)
    {
        foreach (var choice in choices)
            choice.Selection.Visibility = Equals(choice.Host.Tag, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Style MinimalScrollBarStyle() => Resource<Style>("ToolkitMinimalScrollBarStyle");

    private static Style SettingsScrollBarStyle()
    {
        var style = new Style(typeof(ScrollBar), MinimalScrollBarStyle());
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(CalendarLayout.SettingsScrollBarLeftMargin, 4, 1, 4)));
        return style;
    }

    private Border SettingsDivider() => new() { Height = 1, Background = Line(), Margin = new Thickness(0, 1, 0, 0) };

    private Border FieldSection(string caption, UIElement control)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(caption, 14, Foreground(), new Thickness(0, 0, 0, 8)));
        stack.Children.Add(control);
        return new Border { Child = stack };
    }

    private static TextBox SingleLineInput(string text) => new()
    {
        Text = text,
        Height = 42,
        Padding = new Thickness(10, 0, 10, 0),
        FontSize = 14,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private Grid InputWithPlaceholder(TextBox input, string placeholder)
    {
        ApplyInputStyle(input);
        var host = new Grid();
        var hint = Text(placeholder, 14, Muted(), new Thickness(10, 0, 10, 0));
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => hint.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
        host.Children.Add(input);
        host.Children.Add(hint);
        return host;
    }

    private void ApplyInputStyle(TextBox input)
    {
        input.Template = Resource<ControlTemplate>("ToolkitUnderlineTextInputTemplate");
        input.BorderThickness = new Thickness(0);
        input.Background = InputBackground();
    }

}
