using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Orientation = System.Windows.Controls.Orientation;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextWrapping = System.Windows.TextWrapping;

namespace ScrewCalendar;

// Calendar settings window and its state bindings.
public sealed partial class MainWindow
{
    private void ShowSettings(Point? ownerPosition = null)
    {
        if (_settingsDialog is { IsVisible: true })
        {
            _settingsDialog.Close();
            return;
        }

        var dialog = NewSettingsDialog(Localization.T("settings.title"), ownerPosition);
        _settingsDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsDialog, dialog)) _settingsDialog = null;
        };
        var root = new StackPanel();

        var header = new Grid { Height = 52 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerTitle = Text(Localization.T("settings.title"), 24, FontWeights.SemiBold, Foreground());
        Grid.SetColumn(headerTitle, 0); header.Children.Add(headerTitle);
        var close = DialogCloseButton(dialog, 34, 22, 5, InputBackground());
        Grid.SetColumn(close, 1); header.Children.Add(close);
        var rowsLine = SettingValueLine(Localization.T("settings.rowsLabel"), Localization.T("settings.rowsValue", _state.Rows), out var rowsValue);
        root.Children.Add(rowsLine);
        var rows = new Slider { Minimum = CalendarLayout.MinimumRows, Maximum = CalendarLayout.MaximumRows, Value = _state.Rows, TickFrequency = 1, IsSnapToTickEnabled = true, Height = 24, Margin = new Thickness(0, 8, 0, 11) };
        rows.ValueChanged += (_, _) => { _state.Rows = (int)Math.Round(rows.Value); rowsValue.Text = Localization.T("settings.rowsValue", _state.Rows); SaveAndRender(); };
        root.Children.Add(rows);
        var styleHint = Text(Localization.T("settings.styleHint"), 12, Muted(), new Thickness(0, 0, 0, 17)); styleHint.TextWrapping = TextWrapping.Wrap; root.Children.Add(styleHint);

        root.Children.Add(Text(Localization.T("settings.appearance"), 14, Foreground(), new Thickness(0, 0, 0, 8)));
        var appearance = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        var light = SettingsRadio(Localization.T("settings.light"), "appearance", CalendarTheme.Light);
        var dark = SettingsRadio(Localization.T("settings.dark"), "appearance", CalendarTheme.Dark);
        light.IsChecked = _state.Theme == CalendarTheme.Light;
        dark.IsChecked = _state.Theme == CalendarTheme.Dark;
        void SelectTheme(CalendarTheme selected)
        {
            if (_state.Theme == selected) return;
            _state.Theme = selected;
            SaveAndRender();
        }
        light.Checked += (_, _) => SelectTheme(CalendarTheme.Light);
        dark.Checked += (_, _) => SelectTheme(CalendarTheme.Dark);
        appearance.Children.Add(light); appearance.Children.Add(dark); root.Children.Add(appearance);

        TextBlock opacityValue = null!;
        Slider opacity = null!;
        root.Children.Add(Text(Localization.T("settings.calendarStyle"), 14, Foreground(), new Thickness(0, 15, 0, 8)));
        var style = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        var windowsStyle = SettingsRadio(Localization.T("style.windows"), "calendarStyle", CalendarStyle.Windows);
        var minimalStyle = SettingsRadio(Localization.T("style.minimal"), "calendarStyle", CalendarStyle.Minimal);
        var widgetStyle = SettingsRadio(Localization.T("style.widget"), "calendarStyle", CalendarStyle.Widget);
        windowsStyle.IsChecked = _state.Style == CalendarStyle.Windows;
        minimalStyle.IsChecked = _state.Style == CalendarStyle.Minimal;
        widgetStyle.IsChecked = _state.Style == CalendarStyle.Widget;
        void SelectStyle(CalendarStyle selected)
        {
            if (_state.Style == selected) return;
            _state.Style = selected;
            _state.Opacity = selected == CalendarStyle.Widget ? .86 : 1.0;
            opacity.Value = _state.Opacity;
            opacityValue.Text = Localization.T("settings.opacityValue", (int)Math.Round(_state.Opacity * 100));
            SaveAndRender();
        }
        windowsStyle.Checked += (_, _) => SelectStyle(CalendarStyle.Windows);
        minimalStyle.Checked += (_, _) => SelectStyle(CalendarStyle.Minimal);
        widgetStyle.Checked += (_, _) => SelectStyle(CalendarStyle.Widget);
        style.Children.Add(windowsStyle); style.Children.Add(minimalStyle); style.Children.Add(widgetStyle); root.Children.Add(style);

        var opacityLine = SettingValueLine(Localization.T("settings.opacityLabel"), Localization.T("settings.opacityValue", (int)Math.Round(_state.Opacity * 100)), out opacityValue);
        opacityLine.Margin = new Thickness(0, 15, 0, 0); root.Children.Add(opacityLine);
        opacity = new Slider { Minimum = .4, Maximum = 1, Value = _state.Opacity, TickFrequency = .05, IsSnapToTickEnabled = true, Height = 24, Margin = new Thickness(0, 8, 0, 18) };
        opacity.ValueChanged += (_, _) => { _state.Opacity = opacity.Value; opacityValue.Text = Localization.T("settings.opacityValue", (int)Math.Round(_state.Opacity * 100)); SaveAndRender(); };
        root.Children.Add(opacity);

        root.Children.Add(SettingsDivider());
        root.Children.Add(Text(Localization.T("settings.sectionBehavior"), 15, FontWeights.SemiBold, Foreground(), new Thickness(0, 17, 0, 12)));
        var themeColorText = Text(Localization.T("settings.themeColor", ThemeColorName()), 14, Foreground());
        root.Children.Add(themeColorText);
        var themeColors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 9, 0, 14) };
        var themeChoices = new List<(Grid Host, Ellipse Selection)>();
        foreach (var themeColor in Enum.GetValues<CalendarThemeColor>())
        {
            var choice = ColorChoice(themeColor, ThemeColorName(themeColor), ThemeColorBrush(themeColor), themeColor == _state.ThemeColor);
            themeChoices.Add(choice);
            choice.Host.MouseLeftButtonDown += (_, _) => { _state.ThemeColor = themeColor; themeColorText.Text = Localization.T("settings.themeColor", ThemeColorName()); RefreshChoiceSelections(themeChoices, _state.ThemeColor); SaveAndRender(); };
            themeColors.Children.Add(choice.Host);
        }
        root.Children.Add(themeColors);
        var locked = new CheckBox { Content = Localization.T("settings.locked"), IsChecked = _state.Locked, FontSize = 14, Margin = new Thickness(0, 2, 0, 11) };
        locked.Checked += (_, _) => SetLocked(true); locked.Unchecked += (_, _) => SetLocked(false); root.Children.Add(locked);
        var topmost = new CheckBox { Content = Localization.T("settings.topmost"), IsChecked = _state.Topmost, FontSize = 14, Margin = new Thickness(0, 0, 0, 11) };
        topmost.Checked += (_, _) => SetTopmost(true); topmost.Unchecked += (_, _) => SetTopmost(false); root.Children.Add(topmost);
        var topmostHint = Text(Localization.T("settings.topmostHint"), 12, Muted(), new Thickness(0, -5, 0, 11));
        topmostHint.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(topmostHint);
        var startup = new CheckBox { Content = Localization.T("settings.startup"), IsChecked = _state.StartWithWindows, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) };
        startup.Checked += (_, _) => SetStartupEnabled(true); startup.Unchecked += (_, _) => SetStartupEnabled(false); root.Children.Add(startup);
        var todoPanel = new CheckBox { Content = Localization.T("settings.todoPanel"), IsChecked = _state.ShowTodoPanel, FontSize = 14, Margin = new Thickness(0, 7, 0, 4) };
        void SetTodoPanelVisible(bool visible)
        {
            if (_state.ShowTodoPanel == visible) return;
            _state.ShowTodoPanel = visible;
            SaveState();
            Render();
        }
        todoPanel.Checked += (_, _) => SetTodoPanelVisible(true);
        todoPanel.Unchecked += (_, _) => SetTodoPanelVisible(false);
        root.Children.Add(todoPanel);

        root.Children.Add(SettingsDivider());
        root.Children.Add(Text(Localization.T("settings.sectionLanguage"), 15, FontWeights.SemiBold, Foreground(), new Thickness(0, 17, 0, 8)));
        var language = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        var chinese = SettingsRadio(Localization.T("language.chinese"), "language", CalendarLanguage.ChineseSimplified);
        var english = SettingsRadio(Localization.T("language.english"), "language", CalendarLanguage.English);
        chinese.IsChecked = _state.Language == CalendarLanguage.ChineseSimplified;
        english.IsChecked = _state.Language == CalendarLanguage.English;
        void SelectLanguage(CalendarLanguage selected)
        {
            if (_state.Language == selected) return;
            _state.Language = selected;
            Localization.SetLanguage(selected);
            SaveState();
            dialog.Close();
            RefreshLocalizedUi();
        }
        chinese.Checked += (_, _) => SelectLanguage(CalendarLanguage.ChineseSimplified);
        english.Checked += (_, _) => SelectLanguage(CalendarLanguage.English);
        language.Children.Add(chinese); language.Children.Add(english); root.Children.Add(language);

        root.Children.Add(SettingsDivider());
        root.Children.Add(Text(Localization.T("settings.sectionData"), 15, FontWeights.SemiBold, Foreground(), new Thickness(0, 17, 0, 8)));
        var portableHint = Text(Localization.T("settings.portable"), 12, Muted(), new Thickness(0, 0, 0, 12)); portableHint.TextWrapping = TextWrapping.Wrap; root.Children.Add(portableHint);
        var export = Button(Localization.T("settings.export"), Localization.T("settings.exportTooltip"), 100); export.Height = 36; export.Click += (_, _) => ExportJson();
        var import = Button(Localization.T("settings.import"), Localization.T("settings.importTooltip"), 100); import.Height = 36; import.Margin = new Thickness(7, 2, 0, 0); import.Click += (_, _) => ImportJson();
        var dataButtons = new StackPanel { Orientation = Orientation.Horizontal }; dataButtons.Children.Add(export); dataButtons.Children.Add(import); root.Children.Add(dataButtons);
        root.Children.Add(Text(Localization.T("settings.dataLocation", _dataStore.DataDirectory), 11, Muted(), new Thickness(0, 15, 0, 4)));
        var holidayHint = Text(Localization.T("settings.holidayInfo"), 12, Muted(), new Thickness(0, 0, 0, 12)); holidayHint.TextWrapping = TextWrapping.Wrap; root.Children.Add(holidayHint);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root };
        scroll.Resources.Add(typeof(ScrollBar), SettingsScrollBarStyle());
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroll);
        dialog.Content = DialogSurface(layout, SettingsDialogPadding());
        dialog.Show();
    }
}
