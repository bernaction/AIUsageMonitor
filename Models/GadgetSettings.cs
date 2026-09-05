namespace AIUsageGadget.Models;

public sealed class GadgetSettings
{
    public List<ProviderPreference> Providers { get; init; } = [];

    public int RefreshIntervalMinutes { get; init; } = 1;
}

public sealed class ProviderPreference
{
    public string ProviderId { get; init; } = string.Empty;

    public bool IsVisible { get; init; } = true;
}
