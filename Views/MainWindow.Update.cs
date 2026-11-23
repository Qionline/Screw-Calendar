using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

public sealed partial class MainWindow
{
    private async Task DownloadAndInstallUpdateAsync(Button button, Version version, CancellationToken cancellationToken)
    {
        var (meter, label) = ShowUpdateProgress(button, "update.downloading");
        var progress = new Progress<int?>(percent =>
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(label.Parent, button.Content)) return;
            meter.IsIndeterminate = !percent.HasValue;
            if (!percent.HasValue) return;
            meter.Value = percent.Value;
            label.Text = Localization.T("update.percent", percent.Value);
            AutomationProperties.SetName(button, label.Text);
        });
        var session = await _appUpdates.DownloadUpdateAsync(AppUpdateService.CurrentVersion, version, progress, cancellationToken);
        var handedOff = false;
        Process? helper = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowUpdateProgress(button, "update.preparing");
            helper = await AppUpdateInstaller.StartHelperAsync(session, version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_activeInlineSection is not null && !SaveInlineMarkdownEdit(_activeInlineSection)) return;
            if (!_markdownEditors.TrySaveAndClose()) return;
            cancellationToken.ThrowIfCancellationRequested();
            WindowPlacementService.Capture(this, _state.Geometry);
            if (!_dataStore.NeedsRecovery && !_dataStore.Save(_state))
                throw new System.IO.IOException("Unable to save calendar data before updating.");

            AppUpdateInstaller.Approve(session);
            handedOff = true;
            _allowExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        finally
        {
            try
            {
                if (!handedOff)
                {
                    if (helper is not null) AppUpdateInstaller.Stop(helper);
                    AppUpdatePackage.Cleanup(session);
                }
            }
            finally { helper?.Dispose(); }
        }
    }
}
