using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopWidgets.Toolkit.Windows;

namespace ScrewCalendar;

internal static class AppUpdateInstaller
{
    internal const string ApplyArgument = "--apply-update";
    internal const string ConfirmArgument = "--confirm-update";
    internal const string RolledBackArgument = "--update-rolled-back";
    internal const string MutexName = @"Local\ScrewCalendar.Update";
    private const string PlanFile = "plan.json";
    private const string ReadyFile = "helper-ready";
    private const string ApprovedFile = "approved";
    private const string StartupFile = "startup-confirmed";
    private const string FinishedFile = "finished";

    internal static async Task<Process> StartHelperAsync(Guid session, Version version, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Screw Calendar.");
        if (!Path.GetFileName(executable).Equals(AppUpdatePackage.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.ChangeExtension(executable, ".dll")))
            throw new InvalidOperationException("In-app updates require the published Screw Calendar executable.");

        using var original = Process.GetCurrentProcess();
        var plan = new UpdatePlan(executable, original.Id, original.StartTime.ToUniversalTime().Ticks, version.ToString());
        var helperPath = SessionFile(session, AppUpdatePackage.ExecutableName);
        await Task.Run(() =>
        {
            CheckInstallation(session, Path.GetDirectoryName(executable)!);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(executable, helperPath);
            File.WriteAllText(SessionFile(session, PlanFile), JsonSerializer.Serialize(plan));
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var helper = Launch(helperPath, ApplyArgument, session);
        try
        {
            using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(SessionFile(session, ReadyFile)))
            {
                if (helper.HasExited) throw new InvalidOperationException("The update helper could not start.");
                await Task.Delay(100, readyDeadline.Token).ConfigureAwait(false);
            }
            if (helper.HasExited) throw new InvalidOperationException("The update helper exited before the update was approved.");
            return helper;
        }
        catch
        {
            Stop(helper);
            helper.Dispose();
            throw;
        }
    }

    internal static void Approve(Guid session) => WriteMarker(session, ApprovedFile);

    internal static void Stop(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill();
            if (!process.WaitForExit(5000)) throw new TimeoutException("The update process did not exit.");
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    internal static int Run(Guid session)
    {
        string? executableToRestart = null;
        try
        {
            var plan = ReadPlan(session);
            if (!SamePath(Environment.ProcessPath, SessionFile(session, AppUpdatePackage.ExecutableName)))
                throw new InvalidDataException("The update helper is not running from the staged session.");
            using var original = Process.GetProcessById(plan.ProcessId);
            if (original.StartTime.ToUniversalTime().Ticks != plan.ProcessStartTicks ||
                !SamePath(original.MainModule?.FileName, plan.Executable))
                throw new InvalidDataException("The staged update does not match the running calendar.");

            using (var gate = new SingleInstanceGuard(MutexName))
            {
                if (!gate.IsPrimaryInstance) return 1;
                File.WriteAllText(SessionFile(session, ReadyFile), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!original.WaitForExit(30000) || !File.Exists(SessionFile(session, ApprovedFile))) return 1;
                var directory = Path.GetDirectoryName(plan.Executable)!;
                try
                {
                    InstallFiles(session, directory, () =>
                    {
                        using var updated = Launch(plan.Executable, ConfirmArgument, session);
                        var wait = Stopwatch.StartNew();
                        while (!updated.HasExited && wait.Elapsed < TimeSpan.FromSeconds(45))
                        {
                            if (File.Exists(SessionFile(session, StartupFile))) return true;
                            Thread.Sleep(100);
                        }
                        Stop(updated);
                        return false;
                    });
                }
                catch (UpdateRollbackException)
                {
                    // Keep the complete backup available for recovery when Windows blocks restoration.
                    throw;
                }
                catch (Exception exception)
                {
                    AppLogger.Error("Update failed. Restarting the previous Screw Calendar version.", exception);
                    executableToRestart = plan.Executable;
                }
                try { WriteMarker(session, FinishedFile); }
                catch (Exception exception) { AppLogger.Error("Failed to mark update files for cleanup.", exception); }
            }

            if (executableToRestart is not null)
            {
                using var restarted = Launch(executableToRestart, RolledBackArgument, session);
                return 1;
            }
            return 0;
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Unable to finish the update. Session: {AppUpdatePackage.SessionDirectory(session)}", exception);
            return 1;
        }
    }

    internal static bool CanConfirmStartup(Guid session)
    {
        try
        {
            var plan = ReadPlan(session);
            return SamePath(Environment.ProcessPath, plan.Executable) && AppUpdateService.CurrentVersion.ToString() == plan.Version &&
                File.Exists(SessionFile(session, ApprovedFile)) && File.Exists(SessionFile(session, ReadyFile)) &&
                !File.Exists(SessionFile(session, FinishedFile));
        }
        catch (Exception exception)
        {
            AppLogger.Error("Unable to verify the update startup session.", exception);
            return false;
        }
    }

    internal static void ConfirmStartup(Guid session)
    {
        try { WriteMarker(session, StartupFile); }
        catch (Exception exception) { AppLogger.Error("Unable to confirm startup after updating.", exception); return; }
        ScheduleCleanup(session);
    }

    internal static void ScheduleCleanup(Guid session) => _ = Task.Run(async () =>
    {
        try
        {
            var id = File.ReadAllText(SessionFile(session, ReadyFile));
            if (int.TryParse(id, out var processId) && processId != Environment.ProcessId)
            {
                try
                {
                    using var helper = Process.GetProcessById(processId);
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (ArgumentException) { }
            }
            if (File.Exists(SessionFile(session, FinishedFile))) AppUpdatePackage.Cleanup(session);
        }
        catch (Exception exception) { AppLogger.Error("Unable to clean up the update session.", exception); }
    });

    internal static void InstallFiles(Guid session, string installation, Func<bool> startUpdatedVersion)
    {
        var files = AppUpdatePackage.GetStagedFiles(session);
        var payload = AppUpdatePackage.PayloadDirectory(session);
        var backup = SessionFile(session, "backup");
        foreach (var file in files)
        {
            var target = Path.Combine(installation, file);
            AppUpdatePackage.CheckForReparsePoints(target);
            if (!File.Exists(target)) continue;
            var saved = Path.Combine(backup, file);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            CopyDurably(target, saved);
        }

        var replaced = new List<string>();
        try
        {
            foreach (var file in files)
            {
                ReplaceFile(Path.Combine(payload, file), Path.Combine(installation, file));
                replaced.Add(file);
            }
            if (!startUpdatedVersion()) throw new InvalidOperationException("The new version did not confirm startup.");
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<Exception>();
            replaced.Reverse();
            foreach (var file in replaced)
            {
                try
                {
                    var target = Path.Combine(installation, file);
                    var saved = Path.Combine(backup, file);
                    if (File.Exists(saved)) ReplaceFile(saved, target);
                    else
                    {
                        AppUpdatePackage.CheckForReparsePoints(target);
                        File.Delete(target);
                    }
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new UpdateRollbackException(new AggregateException([exception, .. rollbackErrors]));
            throw;
        }
    }

    private static void CheckInstallation(Guid session, string directory)
    {
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in AppUpdatePackage.GetStagedFiles(session))
        {
            var path = Path.Combine(directory, file);
            AppUpdatePackage.CheckForReparsePoints(path);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException($"The update target is read-only: {path}");
            var destination = Path.GetDirectoryName(path)!;
            while (!Directory.Exists(destination)) destination = Path.GetDirectoryName(destination)!;
            if (!checkedDirectories.Add(destination)) continue;
            var probePath = Path.Combine(destination, $".screw-calendar-{Guid.NewGuid():N}.tmp");
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
    }

    private static void ReplaceFile(string source, string target)
    {
        AppUpdatePackage.CheckForReparsePoints(source);
        AppUpdatePackage.CheckForReparsePoints(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            CopyDurably(source, temporary);
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception exception) { AppLogger.Error("Unable to clean up a temporary update file.", exception); }
            throw;
        }
    }

    private static void CopyDurably(string source, string target)
    {
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static Process Launch(string executable, string argument, Guid session)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(argument);
        start.ArgumentList.Add(session.ToString("N"));
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start a Screw Calendar process.");
    }

    private static UpdatePlan ReadPlan(Guid session)
    {
        var path = SessionFile(session, PlanFile);
        AppUpdatePackage.CheckForReparsePoints(path);
        return JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing update plan.");
    }

    private static bool SamePath(string? first, string second) =>
        first is not null && Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static string SessionFile(Guid session, string name) => Path.Combine(AppUpdatePackage.SessionDirectory(session), name);

    private static void WriteMarker(Guid session, string name)
    {
        using var marker = new FileStream(SessionFile(session, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    private sealed record UpdatePlan(string Executable, int ProcessId, long ProcessStartTicks, string Version);

    private sealed class UpdateRollbackException(Exception inner) : IOException("Unable to restore all files from the update backup.", inner);
}
