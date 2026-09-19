using System;
using System.Collections.Generic;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopWidgets.Toolkit.Windows;

public sealed record TrayMenuItem(
    string Text,
    Action? Click = null,
    bool IsCheckable = false,
    bool IsChecked = false,
    bool IsSeparator = false)
{
    public static TrayMenuItem Separator() => new(string.Empty, IsSeparator: true);
}

/// <summary>A reusable notification-area icon with declarative menu entries.</summary>
public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _icon;

    public void Show(Icon icon, string title, Action? doubleClick, IEnumerable<TrayMenuItem> items)
    {
        Dispose();
        _icon = new Forms.NotifyIcon { Icon = icon, Visible = true, Text = Truncate(title, 63) };
        if (doubleClick is not null) _icon.DoubleClick += (_, _) => doubleClick();
        var menu = new Forms.ContextMenuStrip();
        foreach (var definition in items)
        {
            if (definition.IsSeparator) { menu.Items.Add(new Forms.ToolStripSeparator()); continue; }
            var item = new Forms.ToolStripMenuItem(definition.Text)
            {
                CheckOnClick = definition.IsCheckable,
                Checked = definition.IsChecked
            };
            if (definition.Click is not null) item.Click += (_, _) => definition.Click();
            menu.Items.Add(item);
        }
        _icon.ContextMenuStrip = menu;
    }

    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _icon = null;
    }
}
