using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public interface IAiUsageProvider : IDisposable
{
    string ProviderId { get; }

    Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
