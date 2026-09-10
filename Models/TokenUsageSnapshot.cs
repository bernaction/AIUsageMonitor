namespace AIUsageMonitor.Models;

public sealed record ProviderTokenUsage(
    string ProviderId,
    long TotalTokens,
    decimal EstimatedCostUsd,
    long UnpricedTokens);

public sealed record TokenUsageSnapshot(
    long TotalTokens,
    decimal EstimatedCostUsd,
    long UnpricedTokens,
    IReadOnlyList<ProviderTokenUsage> Providers,
    DateTimeOffset UpdatedAt);
