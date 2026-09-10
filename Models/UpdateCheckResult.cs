namespace AIUsageMonitor.Models;

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    string? LatestTag,
    Uri? ReleaseUrl,
    string? ErrorMessage)
{
    public static UpdateCheckResult Available(string latestTag, Uri releaseUrl) =>
        new(true, true, latestTag, releaseUrl, null);

    public static UpdateCheckResult Current(string latestTag, Uri releaseUrl) =>
        new(true, false, latestTag, releaseUrl, null);

    public static UpdateCheckResult Failed(string message) =>
        new(false, false, null, null, message);
}
