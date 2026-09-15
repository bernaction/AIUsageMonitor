namespace AIUsageMonitor.Models;

public sealed class GadgetSettings
{
    public List<ProviderPreference> Providers { get; init; } = [];

    public bool AlwaysOnTop { get; init; } = true;

    public double FontScale { get; init; } = 1.0;
}

public sealed class ProviderPreference
{
    public string ProviderId { get; init; } = string.Empty;

    public bool IsVisible { get; init; } = true;

    public string? Source { get; init; }
}
