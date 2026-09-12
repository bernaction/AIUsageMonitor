using System.ComponentModel;
using System.Windows;

namespace AIUsageMonitor;

public partial class SettingsWindow : Window
{
    private bool _allowClose;
    private bool _isClosed;

    public SettingsWindow(FrameworkElement settingsContent, FrameworkElement aboutContent)
    {
        InitializeComponent();
        SettingsContentHost.Content = settingsContent;
        AboutContentHost.Content = aboutContent;
        SelectTab(showAbout: false);
        Closing += SettingsWindow_Closing;
        Closed += (_, _) => _isClosed = true;
    }

    public void ShowSettingsTab()
    {
        SelectTab(showAbout: false);
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

    private void SettingsTabButton_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(showAbout: false);
    }

    private void AboutTabButton_Click(object sender, RoutedEventArgs e)
    {
        SelectTab(showAbout: true);
    }

    private void SelectTab(bool showAbout)
    {
        SettingsContentHost.Visibility = showAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutContentHost.Visibility = showAbout ? Visibility.Visible : Visibility.Collapsed;
        ApplyTabState(SettingsTabButton, isSelected: !showAbout);
        ApplyTabState(AboutTabButton, isSelected: showAbout);
    }

    private static void ApplyTabState(System.Windows.Controls.Button button, bool isSelected)
    {
        button.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                isSelected ? "#F2F4F8" : "#96A0B5"));
        button.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                isSelected ? "#247C5CFC" : "#00FFFFFF"));
        button.BorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                isSelected ? "#557C5CFC" : "#00FFFFFF"));
    }
}
