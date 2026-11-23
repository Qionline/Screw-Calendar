using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ScrewCalendar;

internal static class AppUpdatePackage
{
    internal const string ExecutableName = "ScrewCalendar.exe";
    private const long MaximumExtractedBytes = 512 * 1024 * 1024;
    private static readonly string[] RequiredFiles =
    [
        ExecutableName, "LICENSE", "README.md", "THIRD_PARTY_NOTICES.md",
        "locales/en-US.json", "locales/zh-CN.json", "Resources/Data/calendar-data.json"
    ];
    private static readonly string[] ReleaseFiles = [.. RequiredFiles, "ScrewCalendar.pdb", "DesktopWidgets.Toolkit.pdb"];
    private static readonly string[] ReleaseFolders = ["locales/", "Resources/", "Resources/Data/"];

    internal static string SessionDirectory(Guid session) =>
        Path.Combine(Path.GetTempPath(), "ScrewCalendarUpdates", session.ToString("N"));

    internal static string CreateSession(Guid session)
    {
        var directory = SessionDirectory(session);
        CheckForReparsePoints(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string PayloadDirectory(Guid session) => Path.Combine(SessionDirectory(session), "payload");

    internal static void Stage(Guid session, Version version, CancellationToken cancellationToken)
    {
        var directory = SessionDirectory(session);
        var payload = PayloadDirectory(session);
        Directory.CreateDirectory(payload);
        using var archive = ZipFile.OpenRead(Path.Combine(directory, "update.zip"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extracted = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (entry.Length == 0 && ReleaseFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var canonicalName = ReleaseFiles.FirstOrDefault(file => file.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonicalName is null || !seen.Add(canonicalName))
                throw new InvalidDataException("The update ZIP contains an unexpected or duplicate path.");
            if (entry.Length > MaximumExtractedBytes - extracted)
                throw new InvalidDataException("The extracted update exceeds the permitted size.");

            var target = Path.Combine(payload, canonicalName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = source.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                extracted += count;
                if (extracted > MaximumExtractedBytes)
                    throw new InvalidDataException("The extracted update exceeds the permitted size.");
                destination.Write(buffer, 0, count);
            }
        }

        if (!RequiredFiles.All(seen.Contains))
            throw new InvalidDataException("The update ZIP is missing required release files.");
        VerifyExecutableVersion(Path.Combine(payload, ExecutableName), version);
    }

    internal static string[] GetStagedFiles(Guid session)
    {
        var payload = PayloadDirectory(session);
        if (RequiredFiles.Any(file => !File.Exists(Path.Combine(payload, file))))
            throw new InvalidDataException("The staged update is incomplete.");
        // Switch the executable last, after the resources it needs are in place.
        return ReleaseFiles.Skip(1).Append(ExecutableName).Where(file => File.Exists(Path.Combine(payload, file))).ToArray();
    }

    internal static void VerifyExecutableVersion(string path, Version expected)
    {
        var file = FileVersionInfo.GetVersionInfo(path);
        if (file.FileMajorPart != expected.Major || file.FileMinorPart != expected.Minor || file.FileBuildPart != expected.Build)
            throw new InvalidDataException("The staged executable does not match the update version.");
    }

    internal static void CheckForReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Cannot update through a linked path: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void Cleanup(Guid session)
    {
        try
        {
            var directory = SessionDirectory(session);
            CheckForReparsePoints(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) { AppLogger.Error("Failed to remove temporary update files.", exception); }
    }
}
