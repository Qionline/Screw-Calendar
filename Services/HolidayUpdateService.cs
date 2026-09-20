using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewCalendar;

public sealed class HolidayUpdateService : IDisposable
{
    internal const int MaximumDocumentCharacters = 256 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly string[] SourceTemplates =
    [
        "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{0}.json",
        "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json"
    ];
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly CalendarMetadataService _metadata;
    private readonly string _cacheDirectory;
    private readonly Func<int, CancellationToken, Task<string?>> _download;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _downloadGate = new(2, 2);
    private readonly object _attemptGate = new();
    private readonly HashSet<int> _attemptedYears = [];

    public HolidayUpdateService(
        CalendarMetadataService metadata,
        string? cacheDirectory = null,
        Func<int, CancellationToken, Task<string?>>? download = null)
    {
        _metadata = metadata;
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;
        _download = download ?? DownloadYearAsync;
    }

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScrewCalendar", "data", "holiday-cache");

    public event EventHandler? DataUpdated;

    public async Task RefreshForCalendarYearAsync(int year)
    {
        var maximumYear = DateTime.Today.Year + 2;
        foreach (var sourceYear in new[] { year, year + 1 })
        {
            if (sourceYear is < 2007 || sourceYear > maximumYear || !ShouldAttempt(sourceYear)) continue;
            try
            {
                var cachePath = CachePath(sourceYear);
                if (File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheLifetime &&
                    _metadata.TryApplyHolidayDocument(File.ReadAllText(cachePath), sourceYear))
                    continue;
                await _downloadGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                string? json;
                try
                {
                    json = await _download(sourceYear, _shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    _downloadGate.Release();
                }
                if (json is null || !_metadata.TryApplyHolidayDocument(json, sourceYear)) continue;
                SaveCacheAtomically(cachePath, json);
                DataUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogger.Error($"Failed to save holiday data for {sourceYear}.", exception);
            }
            catch (Exception)
            {
                // Offline access, timeouts, and unavailable future-year files are expected and stay silent.
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
    }

    private bool ShouldAttempt(int year)
    {
        lock (_attemptGate) return _attemptedYears.Add(year);
    }

    private string CachePath(int year) => Path.Combine(_cacheDirectory, year.ToString(CultureInfo.InvariantCulture) + ".json");

    private static async Task<string?> DownloadYearAsync(int year, CancellationToken cancellationToken)
    {
        foreach (var template in SourceTemplates)
        {
            try
            {
                using var response = await HttpClient.GetAsync(string.Format(CultureInfo.InvariantCulture, template, year), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumDocumentCharacters) continue;
                var json = await ReadLimitedStringAsync(response, cancellationToken).ConfigureAwait(false);
                if (json is not null) return json;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Try the next official upstream delivery endpoint.
            }
        }
        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ScrewCalendar/0.1 (+https://github.com/Qionline/Screw-Calendar)");
        return client;
    }

    private static async Task<string?> ReadLimitedStringAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return result.ToString();
            if (result.Length + count > MaximumDocumentCharacters) return null;
            result.Append(buffer, 0, count);
        }
    }

    private static void SaveCacheAtomically(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
