using System.IO;
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

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.ParseAdd($"AIUsageMonitor/{FormatVersion(currentVersion)}");

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.Failed("No published GitHub release was found.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed("GitHub Releases is temporarily unavailable.");
            }

            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
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
            return latestVersion > normalizedCurrentVersion
                ? UpdateCheckResult.Available(latestTag, releaseUrl)
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
