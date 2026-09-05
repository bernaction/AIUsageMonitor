using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageGadget.Models;
using AIUsageGadget.Services;

namespace AIUsageGadget;

public partial class MainWindow : Window
{
    private static readonly int[] RefreshIntervalOptions = [1, 2, 5, 10, 15, 30];
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const double ResizeBorderSize = 8;

    private readonly IAiUsageProvider _codexUsageProvider = new CodexUsageProvider();
    private readonly IAiUsageProvider _claudeUsageProvider = new ClaudeUsageProvider();
    private readonly GadgetSettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<ProviderDisplayOption> _providerOptions = [];
    private readonly Dictionary<string, FrameworkElement> _providerCards = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _windowSource;
    private Point _dragStart;
    private ProviderDisplayOption? _draggedProvider;
    private bool _isRefreshing;
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _providerOptions.Add(new ProviderDisplayOption("codex", "Codex", "Live limits from the local session"));
        _providerOptions.Add(new ProviderDisplayOption("claude", "Claude", "Not connected yet"));
        _providerOptions.Add(new ProviderDisplayOption("gemini", "Gemini", "Not connected yet"));
        _providerCards.Add("codex", CodexCard);
        _providerCards.Add("claude", ClaudeCard);
        _providerCards.Add("gemini", GeminiCard);
        ProviderSettingsList.ItemsSource = _providerOptions;
        RefreshIntervalComboBox.ItemsSource = RefreshIntervalOptions;
        RefreshIntervalComboBox.SelectedItem = 1;

        _refreshTimer.Tick += RefreshTimer_Tick;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        _refreshTimer.Start();
        await RefreshUsageAsync();
    }

    private async Task LoadSettingsAsync()
    {
        GadgetSettings? settings = await _settingsStore.LoadAsync(_lifetimeCancellation.Token);
        if (settings is not null)
        {
            Dictionary<string, ProviderDisplayOption> known = _providerOptions.ToDictionary(
                item => item.ProviderId,
                StringComparer.OrdinalIgnoreCase);
            List<ProviderDisplayOption> ordered = [];
            foreach (ProviderPreference preference in settings.Providers)
            {
                if (!known.Remove(preference.ProviderId, out ProviderDisplayOption? option))
                {
                    continue;
                }

                option.IsVisible = preference.IsVisible;
                ordered.Add(option);
            }
            ordered.AddRange(_providerOptions.Where(known.ContainsValue));

            _providerOptions.Clear();
            foreach (ProviderDisplayOption option in ordered)
            {
                _providerOptions.Add(option);
            }

            int interval = RefreshIntervalOptions.Contains(settings.RefreshIntervalMinutes)
                ? settings.RefreshIntervalMinutes
                : 1;
            RefreshIntervalComboBox.SelectedItem = interval;
            _refreshTimer.Interval = TimeSpan.FromMinutes(interval);
        }

        _settingsLoaded = true;
        ApplyProviderLayout();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshUsageAsync();
    }

    private async Task RefreshUsageAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            Task<AiUsageSnapshot> codexTask = _codexUsageProvider.GetUsageAsync(_lifetimeCancellation.Token);
            Task<AiUsageSnapshot> claudeTask = _claudeUsageProvider.GetUsageAsync(_lifetimeCancellation.Token);
            AiUsageSnapshot[] snapshots = await Task.WhenAll(codexTask, claudeTask);
            ApplyCodexSnapshot(snapshots[0]);
            ApplyClaudeSnapshot(snapshots[1]);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing; no UI update is needed.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyCodexSnapshot(AiUsageSnapshot snapshot)
    {
        if (!snapshot.IsAvailable)
        {
            ApplyUnavailableCodexState(snapshot.StatusMessage ?? "Codex is unavailable.");
            return;
        }

        CodexStatusText.Text = "Live";
        CodexStatusText.Foreground = new SolidColorBrush(Color.FromRgb(103, 230, 167));
        CodexPlanText.Text = $"{snapshot.PlanLabel} · updated now";
        CodexPlanText.ToolTip = null;

        ApplyWindow(snapshot.Session, CodexSessionUsageText, CodexSessionProgress, CodexSessionResetText, includeDate: false);
        ApplyWindow(snapshot.Weekly, CodexWeeklyUsageText, CodexWeeklyProgress, CodexWeeklyResetText, includeDate: true);

        if (snapshot.Reserve is UsageWindow reserve)
        {
            CodexReserveLabelText.Text = reserve.Label.ToUpperInvariant();
            CodexReserveUsageText.Text = FormatPercent(reserve.UsedPercent);
            CodexReserveProgress.Value = reserve.UsedPercent;
        }
        else
        {
            CodexReserveLabelText.Text = "RESERVE";
            CodexReserveUsageText.Text = "Not available";
            CodexReserveProgress.Value = 0;
        }

        if (snapshot.ResetCredits is ResetCreditsInfo credits)
        {
            string noun = credits.AvailableCount == 1 ? "reset available" : "resets available";
            CodexResetCreditsText.Text = $"{credits.AvailableCount} {noun}";
            CodexResetExpiryText.Text = credits.Expirations.Count > 0
                ? $"Expires {FormatRemaining(credits.Expirations[0], "in ")}"
                : "Expiration unavailable";
        }
        else
        {
            CodexResetCreditsText.Text = "Reset credits unavailable";
            CodexResetExpiryText.Text = string.Empty;
        }

    }

    private void ApplyUnavailableCodexState(string message)
    {
        CodexStatusText.Text = "Unavailable";
        CodexStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
        CodexPlanText.Text = message;
        CodexPlanText.ToolTip = message;

        ClearWindow(CodexSessionUsageText, CodexSessionProgress, CodexSessionResetText);
        ClearWindow(CodexWeeklyUsageText, CodexWeeklyProgress, CodexWeeklyResetText);
        CodexReserveUsageText.Text = "-- used";
        CodexReserveProgress.Value = 0;
        CodexResetCreditsText.Text = "Reset credits unavailable";
        CodexResetExpiryText.Text = string.Empty;
    }

    private void ApplyClaudeSnapshot(AiUsageSnapshot snapshot)
    {
        ClaudeConnectionHint.Visibility = snapshot.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        ClaudeStatusText.Text = snapshot.IsAvailable ? "Live" : "Unavailable";
        ClaudeStatusText.Foreground = snapshot.IsAvailable
            ? new SolidColorBrush(Color.FromRgb(103, 230, 167))
            : new SolidColorBrush(Color.FromRgb(240, 184, 137));
        ClaudePlanText.Text = snapshot.IsAvailable
            ? $"{snapshot.PlanLabel} · updated now"
            : snapshot.StatusMessage ?? "Claude is unavailable.";
        ClaudePlanText.ToolTip = snapshot.IsAvailable ? null : snapshot.StatusMessage;

        ApplyWindow(snapshot.Session, ClaudeSessionUsageText, ClaudeSessionProgress, ClaudeSessionResetText, includeDate: false);
        ApplyWindow(snapshot.Weekly, ClaudeWeeklyUsageText, ClaudeWeeklyProgress, ClaudeWeeklyResetText, includeDate: true);
    }

    private static void ApplyWindow(
        UsageWindow? window,
        System.Windows.Controls.TextBlock usageText,
        System.Windows.Controls.ProgressBar progressBar,
        System.Windows.Controls.TextBlock resetText,
        bool includeDate)
    {
        if (window is null)
        {
            ClearWindow(usageText, progressBar, resetText);
            return;
        }

        usageText.Text = FormatPercent(window.UsedPercent);
        progressBar.Value = window.UsedPercent;
        resetText.Text = window.ResetsAt is DateTimeOffset resetsAt
            ? FormatResetTime(resetsAt, includeDate)
            : "Reset time unavailable";
    }

    private static string FormatResetTime(DateTimeOffset resetsAt, bool includeDate)
    {
        DateTimeOffset localReset = resetsAt.ToLocalTime();
        string remaining = FormatRemaining(resetsAt, "in ");
        string localTime = localReset.ToString("h:mm tt", EnglishCulture);

        if (includeDate)
        {
            string localDate = localReset.ToString("MMM d, yyyy", EnglishCulture);
            return $"Resets {remaining} · {localDate} at {localTime}";
        }

        DateTime today = DateTimeOffset.Now.Date;
        string dayLabel = localReset.Date == today.AddDays(1)
            ? "Tomorrow"
            : localReset.Date == today
                ? "Today"
                : localReset.ToString("MMM d, yyyy", EnglishCulture);
        return $"Resets {remaining} · {dayLabel} at {localTime}";
    }

    private static void ClearWindow(
        System.Windows.Controls.TextBlock usageText,
        System.Windows.Controls.ProgressBar progressBar,
        System.Windows.Controls.TextBlock resetText)
    {
        usageText.Text = "-- used";
        progressBar.Value = 0;
        resetText.Text = "Data unavailable";
    }

    private static string FormatPercent(double value)
    {
        return $"{Math.Round(value):0}% used";
    }

    private static string FormatRemaining(DateTimeOffset target, string prefix)
    {
        TimeSpan remaining = target - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }
        if (remaining.TotalDays >= 1)
        {
            int days = (int)remaining.TotalDays;
            return remaining.Hours > 0
                ? $"{prefix}{days}d {remaining.Hours}h"
                : $"{prefix}{days}d";
        }
        if (remaining.TotalHours >= 1)
        {
            int hours = (int)remaining.TotalHours;
            return remaining.Minutes > 0
                ? $"{prefix}{hours}h {remaining.Minutes}m"
                : $"{prefix}{hours}h";
        }
        return $"{prefix}{Math.Max(1, remaining.Minutes)}m";
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _windowSource?.RemoveHook(WindowProcedure);
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _codexUsageProvider.Dispose();
        _claudeUsageProvider.Dispose();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        DashboardView.Visibility = Visibility.Visible;
    }

    private async void ProviderVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ProviderDisplayOption option } checkBox)
        {
            option.IsVisible = checkBox.IsChecked == true;
        }

        ApplyProviderLayout();
        await SaveSettingsAsync();
    }

    private void ApplyProviderLayout()
    {
        ProviderCardsPanel.Children.Clear();
        List<FrameworkElement> visibleCards = [];
        foreach (ProviderDisplayOption option in _providerOptions.Where(item => item.IsVisible))
        {
            if (_providerCards.TryGetValue(option.ProviderId, out FrameworkElement? card))
            {
                visibleCards.Add(card);
            }
        }

        for (int index = 0; index < visibleCards.Count; index++)
        {
            visibleCards[index].Margin = index == visibleCards.Count - 1
                ? new Thickness(0)
                : new Thickness(0, 0, 0, 10);
            ProviderCardsPanel.Children.Add(visibleCards[index]);
        }

        NoProvidersMessage.Visibility = visibleCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProviderDisplayOption option })
        {
            _draggedProvider = option;
            _dragStart = e.GetPosition(ProviderSettingsList);
        }
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedProvider is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(ProviderSettingsList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        ProviderDisplayOption dragged = _draggedProvider;
        DragDrop.DoDragDrop(ProviderSettingsList, dragged, DragDropEffects.Move);
        _draggedProvider = null;
    }

    private void ProviderSettingsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ProviderDisplayOption))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ProviderSettingsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ProviderDisplayOption)) is not ProviderDisplayOption dragged)
        {
            return;
        }

        ListBoxItem? targetContainer = FindVisualParent<ListBoxItem>(
            ProviderSettingsList.InputHitTest(e.GetPosition(ProviderSettingsList)) as DependencyObject);
        ProviderDisplayOption? target = targetContainer?.DataContext as ProviderDisplayOption;

        int oldIndex = _providerOptions.IndexOf(dragged);
        int newIndex = target is null ? _providerOptions.Count : _providerOptions.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || dragged == target)
        {
            return;
        }

        if (targetContainer is not null
            && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
        {
            newIndex++;
        }

        _providerOptions.RemoveAt(oldIndex);
        if (oldIndex < newIndex)
        {
            newIndex--;
        }
        _providerOptions.Insert(Math.Clamp(newIndex, 0, _providerOptions.Count), dragged);

        ApplyProviderLayout();
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        GadgetSettings settings = new()
        {
            RefreshIntervalMinutes = RefreshIntervalComboBox.SelectedItem is int minutes ? minutes : 1,
            Providers = _providerOptions.Select(item => new ProviderPreference
            {
                ProviderId = item.ProviderId,
                IsVisible = item.IsVisible
            }).ToList()
        };

        try
        {
            await _settingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            SettingsHintText.Text = "Changes saved automatically";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (IOException)
        {
            SettingsHintText.Text = "Settings could not be saved";
        }
        catch (UnauthorizedAccessException)
        {
            SettingsHintText.Text = "Permission to save settings was denied";
        }
    }

    private async void RefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RefreshIntervalComboBox.SelectedItem is not int minutes
            || !RefreshIntervalOptions.Contains(minutes))
        {
            return;
        }

        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
        if (_refreshTimer.IsEnabled)
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        await SaveSettingsAsync();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest || ResizeMode != ResizeMode.CanResize || WindowState != WindowState.Normal)
        {
            return IntPtr.Zero;
        }

        long packedPoint = lParam.ToInt64();
        int screenX = unchecked((short)(packedPoint & 0xFFFF));
        int screenY = unchecked((short)((packedPoint >> 16) & 0xFFFF));
        Point position = PointFromScreen(new Point(screenX, screenY));

        bool left = position.X >= 0 && position.X < ResizeBorderSize;
        bool right = position.X <= ActualWidth && position.X > ActualWidth - ResizeBorderSize;
        bool top = position.Y >= 0 && position.Y < ResizeBorderSize;
        bool bottom = position.Y <= ActualHeight && position.Y > ActualHeight - ResizeBorderSize;

        int hit = top && left ? HtTopLeft
            : top && right ? HtTopRight
            : bottom && left ? HtBottomLeft
            : bottom && right ? HtBottomRight
            : left ? HtLeft
            : right ? HtRight
            : top ? HtTop
            : bottom ? HtBottom
            : 0;

        if (hit == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(hit);
    }

    private void WindowSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool isInteractiveControl = FindVisualParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null
            || FindVisualParent<ScrollBar>(e.OriginalSource as DependencyObject) is not null;
        bool isHeaderArea = e.GetPosition(this).Y <= 82;
        if (e.ButtonState == MouseButtonState.Pressed && isHeaderArea && !isInteractiveControl)
        {
            DragMove();
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Content = Topmost ? "●" : "○";
        PinButton.ToolTip = Topmost ? "Disable always on top" : "Enable always on top";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
