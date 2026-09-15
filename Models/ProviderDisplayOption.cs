using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIUsageMonitor.Models;

public sealed class ProviderDisplayOption : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _sourcePreference = "automatic";

    public ProviderDisplayOption(string providerId, string displayName, string description, bool isVisible = true)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        Description = description;
        _isVisible = isVisible;
    }

    public string ProviderId { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public string SourcePreference
    {
        get => _sourcePreference;
        set
        {
            if (string.Equals(_sourcePreference, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _sourcePreference = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
