extern alias CalendarApp;

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AppLocalization = CalendarApp::ScrewCalendar.Localization;
using AppStore = CalendarApp::ScrewCalendar.CalendarDataStore;
using AppUpdateInstaller = CalendarApp::ScrewCalendar.AppUpdateInstaller;
using AppUpdatePackage = CalendarApp::ScrewCalendar.AppUpdatePackage;
using AppUpdateService = CalendarApp::ScrewCalendar.AppUpdateService;
using AppWindow = CalendarApp::ScrewCalendar.MainWindow;

namespace ScrewCalendar.Tests;

internal static class AppUpdateTests
{
    private static readonly string[] ReleaseFiles =
    [
        "ScrewCalendar.exe", "LICENSE", "README.md", "THIRD_PARTY_NOTICES.md",
        "locales/en-US.json", "locales/zh-CN.json", "Resources/Data/calendar-data.json"
    ];

    internal static void CheckVersions()
    {
        var installed = new Version(1, 2, 2);
        var location = new Uri("/Qionline/Screw-Calendar/releases/tag/v1.10.0", UriKind.Relative);
        var status = HttpStatusCode.Found;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Check(request.RequestUri?.AbsoluteUri == "https://github.com/Qionline/Screw-Calendar/releases/latest" &&
                request.Headers.UserAgent.ToString() == "ScrewCalendar/1.2.2", "The check requests the official latest release");
            var response = new HttpResponseMessage(status);
            response.Headers.Location = location;
            return Task.FromResult(response);
        }));
        var updates = new AppUpdateService(client);
        Check(CheckLatest() == new Version(1, 10, 0), "Version components compare numerically");
        location = ReleaseLocation(new Version(1, 2, 2));
        Check(CheckLatest() is null, "The installed version is current");
        location = ReleaseLocation(new Version(1, 2, 1));
        Check(CheckLatest() is null, "An older release is not offered");
        location = new Uri("https://github.com/Qionline/Screw-Calendar/releases/tag/v1.3.0-preview");
        Throws<InvalidDataException>(() => CheckLatest());
        location = new Uri("https://example.com/Qionline/Screw-Calendar/releases/tag/v1.3.0");
        Throws<InvalidDataException>(() => CheckLatest());
        status = HttpStatusCode.Forbidden;
        Throws<HttpRequestException>(() => CheckLatest());

        Version? CheckLatest() => updates.CheckForUpdateAsync(installed, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static void DownloadAndInstall()
    {
        var latest = AppUpdateService.CurrentVersion;
        var installed = new Version(0, 0, 0);
        var archiveName = $"ScrewCalendar-v{latest}-win-x64.zip";
        var archive = CreateArchive();
        var checksum = Convert.ToHexString(SHA256.HashData(archive));
        var packageReads = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "release-assets.githubusercontent.com")
            {
                packageReads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            }
            if (uri.AbsolutePath.EndsWith(archiveName + ".sha256", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{checksum}  {archiveName}\n") });
            Check(uri.AbsolutePath.EndsWith(archiveName, StringComparison.Ordinal), "The update downloads the selected release asset");
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/asset");
            return Task.FromResult(redirect);
        }));
        var service = new AppUpdateService(client);
        var installationId = Guid.NewGuid();
        var installation = AppUpdatePackage.CreateSession(installationId);
        try
        {
            var executable = Path.Combine(installation, "ScrewCalendar.exe");
            var readme = Path.Combine(installation, "README.md");
            var externalFile = Path.Combine(installation, "my-notes.txt");
            File.WriteAllText(executable, "previous executable");
            File.WriteAllText(readme, "previous readme");
            File.WriteAllText(externalFile, "local file");

            var first = Download();
            try
            {
                Throws<InvalidOperationException>(() => AppUpdateInstaller.InstallFiles(first, installation, () =>
                {
                    Check(File.ReadAllText(readme) == "updated", "Launch receives the staged application files");
                    return false;
                }));
                Check(File.ReadAllText(executable) == "previous executable" && File.ReadAllText(readme) == "previous readme",
                    "Startup failure restores the previous executable and resources");
                Check(!File.Exists(Path.Combine(installation, "locales", "en-US.json")), "Rollback removes files introduced by the update");
            }
            finally { AppUpdatePackage.Cleanup(first); }

            var second = Download();
            try
            {
                AppUpdateInstaller.InstallFiles(second, installation, () => true);
                Check(File.ReadAllText(readme) == "updated" && File.ReadAllText(externalFile) == "local file",
                    "Successful installation updates release files and preserves unrelated files");
            }
            finally { AppUpdatePackage.Cleanup(second); }

            checksum = new string('0', 64);
            Throws<InvalidDataException>(() => Download());
            archive = CreateArchive("../calendar.json");
            checksum = Convert.ToHexString(SHA256.HashData(archive));
            Throws<InvalidDataException>(() => Download());
            Check(packageReads == 4, "The check validates both the published digest and the ZIP's allowed paths");
        }
        finally { AppUpdatePackage.Cleanup(installationId); }

        Guid Download() => service.DownloadUpdateAsync(installed, latest, new Progress<int?>(), CancellationToken.None).GetAwaiter().GetResult();
    }

    private static byte[] CreateArchive(string? extraFile = null)
    {
        using var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(typeof(AppUpdateService).Assembly.Location, "ScrewCalendar.exe");
            foreach (var file in ReleaseFiles[1..])
            {
                using var writer = new StreamWriter(archive.CreateEntry(file).Open());
                writer.Write("updated");
            }
            if (extraFile is not null) archive.CreateEntry(extraFile);
        }
        return content.ToArray();
    }

    internal static void VerifySettingsFooter(AppStore store)
    {
        TaskCompletionSource<HttpResponseMessage> response = new();
        var requestCancellation = CancellationToken.None;
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, cancellation) =>
        {
            requestCancellation = cancellation;
            requests++;
            return response.Task.WaitAsync(cancellation);
        }));
        var window = new AppWindow(store, false, new AppUpdateService(client));
        try
        {
            window.Show();
            var (dialog, button) = OpenSettings();
            StartCheck(button);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(requests == 1, "The active update check ignores duplicate clicks");
            Respond(AppUpdateService.CurrentVersion);
            WaitUntil(() => Equals(button.Content, AppLocalization.T("update.current")));
            Check(!button.IsEnabled && button.Opacity < 1, "The current-version state is visibly disabled");
            dialog.Close();

            response = new TaskCompletionSource<HttpResponseMessage>();
            (dialog, button) = OpenSettings();
            StartCheck(button);
            var newer = new Version(AppUpdateService.CurrentVersion.Major + 1, 0, 0);
            Respond(newer);
            WaitUntil(() => Equals(button.Content, AppLocalization.T("update.download")));
            Check(button.IsEnabled && Equals(button.ToolTip, AppLocalization.T("update.available", newer)),
                "A new release displays the download label and its version");
            dialog.Close();

            response = new TaskCompletionSource<HttpResponseMessage>();
            (dialog, button) = OpenSettings();
            StartCheck(button);
            dialog.Close();
            Check(requestCancellation.IsCancellationRequested, "Closing settings cancels the pending GitHub request");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
        finally
        {
            typeof(AppWindow).GetField("_allowExit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
        }

        (Window Dialog, Button Button) OpenSettings()
        {
            typeof(AppWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null]);
            var dialog = window.OwnedWindows[0];
            var layout = (Grid)((Border)dialog.Content).Child;
            var footer = (StackPanel)layout.Children[^1];
            var row = (Grid)footer.Children[1];
            var version = (TextBlock)row.Children[0];
            var button = (Button)row.Children[1];
            Check(Grid.GetRow(footer) == layout.RowDefinitions.Count - 1 && footer.Children[0] is Border &&
                layout.Children[1] is ScrollViewer, "The divider and update row sit below the scrolling settings");
            Check(version.Text == $"v{AppUpdateService.CurrentVersion}" && Grid.GetColumn(version) == 0 &&
                Grid.GetColumn(button) == 1 && Equals(button.Content, AppLocalization.T("update.check")), "The version is left of the check button");
            return (dialog, button);
        }

        void Respond(Version version)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = ReleaseLocation(version);
            response.SetResult(redirect);
        }
    }

    private static Uri ReleaseLocation(Version version) =>
        new($"https://github.com/Qionline/Screw-Calendar/releases/tag/v{version}");

    private static void StartCheck(Button button)
    {
        Dispatcher.CurrentDispatcher.Invoke(
            () => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), DispatcherPriority.Normal);
        Check(!button.IsEnabled && button.Content is StackPanel loading &&
            loading.Children[0] is ProgressBar { IsIndeterminate: true } &&
            loading.Children[1] is TextBlock status && status.Text == AppLocalization.T("update.checking"),
            "The button shows a loading indicator while the update request is pending");
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
            Thread.Sleep(10);
        }
        Check(condition(), "The update button did not show the completed check result");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
