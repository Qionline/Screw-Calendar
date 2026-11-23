using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewCalendar;

internal sealed class AppUpdateService
{
    private const string ReleaseUrl = "https://github.com/Qionline/Screw-Calendar/releases/";
    private const string ReleaseTagPrefix = ReleaseUrl + "tag/v";
    private static readonly Uri LatestReleaseUri = new(ReleaseUrl + "latest");
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly HttpClient _httpClient;

    internal AppUpdateService(HttpClient? httpClient = null) => _httpClient = httpClient ?? DefaultClient;

    internal static Version CurrentVersion { get; } = ReadCurrentVersion();

    internal async Task<Guid> DownloadUpdateAsync(Version currentVersion, Version version, IProgress<int?> progress, CancellationToken cancellationToken)
    {
        if (version <= currentVersion || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException("The requested release is not newer than this installation.");

        var session = Guid.NewGuid();
        var directory = AppUpdatePackage.CreateSession(session);
        try
        {
            var name = $"ScrewCalendar-v{version}-win-x64.zip";
            var asset = new Uri($"{ReleaseUrl}download/v{version}/{name}");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            using var checksum = new MemoryStream();
            await CopyAssetAsync(new Uri(asset.AbsoluteUri + ".sha256"), checksum, 4096, null, deadline.Token).ConfigureAwait(false);
            var line = Encoding.ASCII.GetString(checksum.ToArray()).Trim();
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0].Length != 64 || fields[1] != name)
                throw new InvalidDataException("The published update has no matching SHA-256 checksum.");
            var expected = Convert.FromHexString(fields[0]);

            var archive = Path.Combine(directory, "update.zip");
            await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                await CopyAssetAsync(asset, file, 256 * 1024 * 1024, progress, deadline.Token).ConfigureAwait(false);
                file.Position = 0;
                var actual = await SHA256.HashDataAsync(file, deadline.Token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                    throw new InvalidDataException("The update ZIP does not match its published SHA-256 checksum.");
            }

            await Task.Run(() => AppUpdatePackage.Stage(session, version, deadline.Token), deadline.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            AppUpdatePackage.Cleanup(session);
            throw;
        }
    }

    internal async Task<Version?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd($"ScrewCalendar/{currentVersion}");

        // Read GitHub's release redirect without fetching the next page or using the anonymous API quota.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is < 300 or >= 400)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("GitHub did not redirect to a release.");
        }
        if (!Uri.TryCreate(LatestReleaseUri, response.Headers.Location, out var releaseUri))
            throw new InvalidDataException("GitHub did not redirect to a release.");

        var releasePath = releaseUri.GetLeftPart(UriPartial.Path);
        if (!releasePath.StartsWith(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(releasePath[ReleaseTagPrefix.Length..], out var latest) || latest.Build < 0 || latest.Revision >= 0)
            throw new InvalidDataException("GitHub returned an invalid release version.");

        return latest > currentVersion ? latest : null;
    }

    private async Task CopyAssetAsync(Uri uri, Stream destination, int maximumBytes, IProgress<int?>? progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await OpenAssetAsync(uri, idle.Token).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength;
        if (length > maximumBytes) throw new InvalidDataException("The update asset exceeds the permitted size.");

        await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long received = 0;
        int? lastPercent = -1;
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (count == 0) break;
            received += count;
            if (received > maximumBytes) throw new InvalidDataException("The update asset exceeds the permitted size.");
            await destination.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
            int? percent = length > 0 ? (int)Math.Min(100, received * 100 / length.Value) : null;
            if (percent == lastPercent) continue;
            lastPercent = percent;
            progress?.Report(percent);
        }
        if (length.HasValue && received != length.Value)
            throw new InvalidDataException("The update download was incomplete.");
    }

    private async Task<HttpResponseMessage> OpenAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd($"ScrewCalendar/{CurrentVersion}");
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    response.EnsureSuccessStatusCode();
                if (!Uri.TryCreate(uri, response.Headers.Location, out var target) || target.Scheme != Uri.UriSchemeHttps ||
                    !target.IsDefaultPort || target.UserInfo.Length != 0 ||
                    target.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("GitHub returned an unexpected update download redirect.");
                uri = target;
            }
        }
        throw new InvalidDataException("GitHub returned too many update download redirects.");
    }

    private static Version ReadCurrentVersion()
    {
        var version = typeof(AppUpdateService).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Screw Calendar has no assembly version.");
        return new Version(version.Major, version.Minor, version.Build);
    }
}
