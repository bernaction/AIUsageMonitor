namespace AIUsageGadget.Models;

public sealed record UsageWindow(
    string Label,
    double UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt);

public sealed record ResetCreditsInfo(
    int AvailableCount,
    IReadOnlyList<DateTimeOffset> Expirations);

public sealed record AiUsageSnapshot(
    string ProviderId,
    string PlanLabel,
    UsageWindow? Session,
    UsageWindow? Weekly,
    UsageWindow? Reserve,
    ResetCreditsInfo? ResetCredits,
    DateTimeOffset UpdatedAt,
    bool IsAvailable,
    string? StatusMessage)
{
    public static AiUsageSnapshot Unavailable(string providerId, string message) => new(
        providerId,
        "Unknown plan",
        null,
        null,
        null,
        null,
        DateTimeOffset.Now,
        false,
        message);
}
