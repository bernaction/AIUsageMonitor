namespace AIUsageMonitor.Models;

public sealed record UsageWindow(
    string Label,
    double UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt);

public sealed record ResetCreditsInfo(
    int AvailableCount,
    IReadOnlyList<DateTimeOffset> Expirations);

public enum ProviderIssueKind
{
    None,
    AuthenticationRequired,
    RateLimited,
    TemporaryFailure
}

public sealed record AiUsageSnapshot(
    string ProviderId,
    string PlanLabel,
    UsageWindow? Session,
    UsageWindow? Weekly,
    UsageWindow? Reserve,
    ResetCreditsInfo? ResetCredits,
    DateTimeOffset UpdatedAt,
    bool IsAvailable,
    string? StatusMessage,
    ProviderIssueKind IssueKind = ProviderIssueKind.None)
{
    public static AiUsageSnapshot Unavailable(
        string providerId,
        string message,
        ProviderIssueKind issueKind = ProviderIssueKind.TemporaryFailure) => new(
        providerId,
        "Unknown Plan",
        null,
        null,
        null,
        null,
        DateTimeOffset.Now,
        false,
        message,
        issueKind);
}
