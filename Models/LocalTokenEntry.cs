namespace AIUsageMonitor.Models;

internal sealed record LocalTokenEntry(
    string ProviderId,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0)
{
    public long TotalTokens => Math.Max(0, InputTokens)
        + Math.Max(0, OutputTokens)
        + Math.Max(0, CacheReadTokens)
        + Math.Max(0, CacheWriteTokens)
        + Math.Max(0, ReasoningTokens);
}
