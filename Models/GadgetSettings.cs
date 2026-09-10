namespace AIUsageMonitor.Models;

public sealed class GadgetSettings
{
    public List<ProviderPreference> Providers { get; init; } = [];
}

public sealed class ProviderPreference
{
    public string ProviderId { get; init; } = string.Empty;

    public bool IsVisible { get; init; } = true;
}
