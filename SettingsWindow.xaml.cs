using System.ComponentModel;
using System.Windows;

namespace AIUsageMonitor;

public partial class SettingsWindow : Window
{
    private bool _allowClose;
    private bool _isClosed;

    public SettingsWindow(FrameworkElement settingsContent)
    {
        InitializeComponent();
        ContentHost.Child = settingsContent;
        Closing += SettingsWindow_Closing;
        Closed += (_, _) => _isClosed = true;
    }

    public void ClosePermanently()
    {
        if (_isClosed)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
