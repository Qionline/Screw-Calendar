using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopWidgets.Toolkit.Windows;

public static class DialogWindowBehavior
{
    public static void ActivateOnShow(Window dialog, bool keepTopmost)
    {
        dialog.ShowActivated = true;
        dialog.ContentRendered += (_, _) =>
        {
            dialog.Topmost = true;
            dialog.Activate();
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (!dialog.IsVisible) return;
                dialog.Topmost = keepTopmost;
                dialog.Activate();
            });
        };
    }

    public static void EnableTopDrag(Window dialog, double dragHeight)
    {
        dialog.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.GetPosition(dialog).Y > dragHeight || IsInsideButton(e.OriginalSource as DependencyObject)) return;
            dialog.DragMove();
            e.Handled = true;
        };
    }

    public static void PlaceNearOwnerPoint(Window dialog, Window owner, Point ownerPoint, double pointerGap = 12, double shiftFactor = 0.10, double edgeMargin = 8)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        var ownerLeft = double.IsNaN(owner.Left) ? 0 : owner.Left;
        var ownerTop = double.IsNaN(owner.Top) ? 0 : owner.Top;
        dialog.Left = ownerLeft + ownerPoint.X + pointerGap - dialog.Width * shiftFactor;
        dialog.Top = ownerTop + ownerPoint.Y + pointerGap - dialog.Height * shiftFactor;
        dialog.Loaded += (_, _) =>
        {
            var screenPoint = new System.Drawing.Point((int)(ownerLeft + ownerPoint.X), (int)(ownerTop + ownerPoint.Y));
            var workArea = Forms.Screen.FromPoint(screenPoint).WorkingArea;
            var maxLeft = Math.Max(workArea.Left + edgeMargin, workArea.Right - dialog.Width - edgeMargin);
            var maxTop = Math.Max(workArea.Top + edgeMargin, workArea.Bottom - dialog.Height - edgeMargin);
            dialog.Left = Math.Clamp(dialog.Left, workArea.Left + edgeMargin, maxLeft);
            dialog.Top = Math.Clamp(dialog.Top, workArea.Top + edgeMargin, maxTop);
        };
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
