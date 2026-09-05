using AIUsageGadget.Models;

namespace AIUsageGadget.Services;

public interface IAiUsageProvider : IDisposable
{
    string ProviderId { get; }

    Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default);
}
