using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class GitHubReleaseUpdateService : IDisposable
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/bernaction/AIUsageMonitor/releases/latest";
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(UpdateCheckTimeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.ParseAdd($"AIUsageMonitor/{FormatVersion(currentVersion)}");

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Failed("No published GitHub release was found.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed("GitHub Releases is temporarily unavailable.");
            }

            await using Stream content = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token);
            using JsonDocument document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: timeoutCancellation.Token);
            JsonElement root = document.RootElement;

            if (!TryGetRequiredString(root, "tag_name", out string latestTag)
                || !TryParseVersion(latestTag, out Version? latestVersion)
                || !TryGetRequiredString(root, "html_url", out string releaseUrlText)
                || !Uri.TryCreate(releaseUrlText, UriKind.Absolute, out Uri? releaseUrl)
                || releaseUrl.Scheme != Uri.UriSchemeHttps
                || !releaseUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return UpdateCheckResult.Failed("The GitHub release information is invalid.");
            }

            Version normalizedCurrentVersion = NormalizeVersion(currentVersion);
            TryGetWindowsZipAsset(root, latestTag, out Uri? downloadUrl, out string? assetName);
            return latestVersion > normalizedCurrentVersion
                ? UpdateCheckResult.Available(latestTag, releaseUrl, downloadUrl, assetName)
                : UpdateCheckResult.Current(latestTag, releaseUrl);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed("The GitHub release format has changed.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("The update check timed out.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed("GitHub could not be reached.");
        }
        catch (IOException)
        {
            return UpdateCheckResult.Failed("The GitHub response could not be read.");
        }
    }

    public async Task DownloadAsync(
        Uri downloadUrl,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedReleaseDownloadUri(downloadUrl))
        {
            throw new ArgumentException("The release download URL is invalid.", nameof(downloadUrl));
        }

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException("The selected download folder does not exist.");
        }

        string temporaryPath = $"{fullDestinationPath}.download";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, downloadUrl);
            request.Headers.UserAgent.ParseAdd("AIUsageMonitor/updater");
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                if (destination.Length == 0)
                {
                    throw new InvalidDataException("The downloaded update is empty.");
                }
            }

            ValidateDownloadedArchive(temporaryPath);
            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static bool TryGetRequiredString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetWindowsZipAsset(
        JsonElement release,
        string latestTag,
        out Uri? downloadUrl,
        out string? assetName)
    {
        downloadUrl = null;
        assetName = null;
        string expectedAssetName = $"AIUsageMonitor-{latestTag}-win-x64.zip";
        if (!release.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!TryGetRequiredString(asset, "name", out string candidateName)
                || !candidateName.Equals(expectedAssetName, StringComparison.OrdinalIgnoreCase)
                || !candidateName.Equals(Path.GetFileName(candidateName), StringComparison.Ordinal)
                || candidateName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !TryGetRequiredString(asset, "browser_download_url", out string candidateUrl)
                || !Uri.TryCreate(candidateUrl, UriKind.Absolute, out Uri? candidateDownloadUrl)
                || !IsTrustedReleaseDownloadUri(candidateDownloadUrl))
            {
                continue;
            }

            assetName = candidateName;
            downloadUrl = candidateDownloadUrl;
            return true;
        }

        return false;
    }

    private static void ValidateDownloadedArchive(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        bool hasMainExecutable = false;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string entryPath = entry.FullName.Replace('\\', '/');
            if (!entryPath.StartsWith("AIUsageMonitor/", StringComparison.Ordinal)
                || entryPath.Contains("../", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The downloaded update has an unexpected folder structure.");
            }

            hasMainExecutable |= entryPath.Equals(
                "AIUsageMonitor/AIUsageMonitor.exe",
                StringComparison.OrdinalIgnoreCase);
        }

        if (!hasMainExecutable)
        {
            throw new InvalidDataException("The downloaded update does not contain AIUsageMonitor.exe.");
        }
    }

    private static bool IsTrustedReleaseDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(
            "/bernaction/AIUsageMonitor/releases/download/",
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the original download result.
        }
    }

    private static bool TryParseVersion(string tag, out Version? version)
    {
        string value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        int suffixIndex = value.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            value = value[..suffixIndex];
        }

        if (!Version.TryParse(value, out Version? parsed))
        {
            version = null;
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
