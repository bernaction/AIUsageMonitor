using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;

namespace AIUsageMonitor;

public partial class MainWindow : Window
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly Uri AuthorUrl = new("https://github.com/bernaction");
    private static readonly Uri RepositoryUrl = new("https://github.com/bernaction/AIUsageMonitor");
    private static readonly Uri ClaudeUsageUrl = new("https://claude.ai/settings/usage");

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
    private static readonly TimeSpan CodexRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaudeRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly IAiUsageProvider _codexUsageProvider = new CodexUsageProvider();
    private readonly ClaudeSessionKeyStore _claudeSessionKeyStore = new();
    private readonly ClaudeWebUsageClient _claudeWebUsageClient = new();
    private readonly ClaudeUsageProvider _claudeUsageProvider;
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly GadgetSettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _codexRefreshTimer;
    private readonly DispatcherTimer _claudeRefreshTimer;
    private readonly ObservableCollection<ProviderDisplayOption> _providerOptions = [];
    private readonly Dictionary<string, FrameworkElement> _providerCards = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _windowSource;
    private Point _dragStart;
    private ProviderDisplayOption? _draggedProvider;
    private Uri? _latestReleaseUrl;
    private bool _isRefreshingCodex;
    private bool _isRefreshingClaude;
    private bool _isCheckingForUpdates;
    private bool _settingsLoaded;

    public MainWindow()
    {
        InitializeComponent();
        _claudeUsageProvider = new ClaudeUsageProvider(_claudeSessionKeyStore, _claudeWebUsageClient);

        AboutVersionText.Text = $"Version {GetApplicationVersion().ToString(3)}";

        _codexRefreshTimer = new DispatcherTimer
        {
            Interval = CodexRefreshInterval
        };
        _claudeRefreshTimer = new DispatcherTimer
        {
            Interval = ClaudeRefreshInterval
        };
        _providerOptions.Add(new ProviderDisplayOption("codex", "Codex", "Live limits from the local session"));
        _providerOptions.Add(new ProviderDisplayOption("claude", "Claude", "Live limits from the protected Claude Web session"));
        _providerOptions.Add(new ProviderDisplayOption("gemini", "Gemini", "Not connected yet"));
        _providerCards.Add("codex", CodexCard);
        _providerCards.Add("claude", ClaudeCard);
        _providerCards.Add("gemini", GeminiCard);
        ProviderSettingsList.ItemsSource = _providerOptions;
        _codexRefreshTimer.Tick += CodexRefreshTimer_Tick;
        _claudeRefreshTimer.Tick += ClaudeRefreshTimer_Tick;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        _codexRefreshTimer.Start();
        _claudeRefreshTimer.Start();
        await Task.WhenAll(RefreshCodexUsageAsync(), RefreshClaudeUsageAsync(), CheckForUpdatesAsync());
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

        }

        _settingsLoaded = true;
        UpdateClaudeConnectionSettings();
        ApplyProviderLayout();
    }

    private async void CodexRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshCodexUsageAsync();
    }

    private async void ClaudeRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshClaudeUsageAsync();
    }

    private async Task RefreshCodexUsageAsync()
    {
        if (_isRefreshingCodex)
        {
            return;
        }

        _isRefreshingCodex = true;
        try
        {
            AiUsageSnapshot snapshot = await _codexUsageProvider.GetUsageAsync(_lifetimeCancellation.Token);
            ApplyCodexSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing; no UI update is needed.
        }
        finally
        {
            _isRefreshingCodex = false;
        }
    }

    private async Task RefreshClaudeUsageAsync()
    {
        if (_isRefreshingClaude)
        {
            return;
        }

        _isRefreshingClaude = true;
        try
        {
            AiUsageSnapshot snapshot = await _claudeUsageProvider.GetUsageAsync(_lifetimeCancellation.Token);
            ApplyClaudeSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing; no UI update is needed.
        }
        finally
        {
            _isRefreshingClaude = false;
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
        ClaudeStatusText.Text = snapshot.IsAvailable
            ? "Live"
            : snapshot.IssueKind switch
            {
                ProviderIssueKind.AuthenticationRequired => "Sign-in required",
                ProviderIssueKind.RateLimited => "Rate limited",
                _ => "Unavailable"
            };
        ClaudeStatusText.Foreground = snapshot.IsAvailable
            ? new SolidColorBrush(Color.FromRgb(103, 230, 167))
            : new SolidColorBrush(Color.FromRgb(240, 184, 137));
        ClaudePlanText.Text = snapshot.IsAvailable
            ? $"{snapshot.PlanLabel} · updated now"
            : snapshot.StatusMessage ?? "Claude is unavailable.";
        ClaudePlanText.ToolTip = snapshot.IsAvailable ? null : snapshot.StatusMessage;
        ClaudeConnectionHintText.Text = snapshot.IssueKind switch
        {
            ProviderIssueKind.AuthenticationRequired =>
                "Connect Claude Web in Settings by pasting your sessionKey.",
            ProviderIssueKind.RateLimited =>
                "Claude is connected. Usage will be checked again automatically.",
            _ =>
                "Usage data is temporarily unavailable. The app will try again automatically."
        };

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
        _codexRefreshTimer.Stop();
        _claudeRefreshTimer.Stop();
        _windowSource?.RemoveHook(WindowProcedure);
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _codexUsageProvider.Dispose();
        _claudeUsageProvider.Dispose();
        _claudeWebUsageClient.Dispose();
        _updateService.Dispose();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateClaudeConnectionSettings();
        ShowView(SettingsView);
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowView(DashboardView);
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutHintText.Text = string.Empty;
        ShowView(AboutView);
    }

    private void CloseAboutButton_Click(object sender, RoutedEventArgs e)
    {
        ShowView(DashboardView);
    }

    private void AuthorLinkButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(AuthorUrl);
    }

    private void RepositoryLinkButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(RepositoryUrl);
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private void OpenClaudeUsageButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(ClaudeUsageUrl);
    }

    private async void SaveClaudeSessionKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string sessionKey = ClaudeSessionKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            ClaudeConnectionStatusText.Text = "Paste the sessionKey value first.";
            ClaudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
            return;
        }

        SaveClaudeSessionKeyButton.IsEnabled = false;
        _claudeRefreshTimer.Stop();
        ClaudeConnectionStatusText.Text = "Checking the Claude Web session...";
        ClaudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 181));
        try
        {
            AiUsageSnapshot snapshot = await _claudeUsageProvider.ConnectWebSessionAsync(
                sessionKey,
                _lifetimeCancellation.Token);
            ClaudeSessionKeyPasswordBox.Clear();
            ClaudeConnectionStatusText.Text = "Connected securely through Claude Web.";
            ClaudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(103, 230, 167));
            DisconnectClaudeButton.IsEnabled = true;
            ApplyClaudeSnapshot(snapshot);
        }
        catch (ArgumentException)
        {
            ShowClaudeConnectionError("Paste only the sessionKey value beginning with sk-ant-.");
        }
        catch (ClaudeWebException exception) when (exception.IsChallenge)
        {
            ShowClaudeConnectionError("Claude Web requested temporary browser verification. Try again shortly.");
        }
        catch (ClaudeWebException exception) when (exception.StatusCode is 401 or 403)
        {
            ShowClaudeConnectionError("Claude rejected this sessionKey. Sign in again and copy a fresh value.");
        }
        catch (ClaudeWebException exception) when (exception.StatusCode == 429)
        {
            ShowClaudeConnectionError("Claude temporarily rate-limited the check. Try again shortly.");
        }
        catch (Exception exception) when (exception is ClaudeWebException
            or HttpRequestException
            or InvalidOperationException
            or JsonException
            or Win32Exception)
        {
            ShowClaudeConnectionError("The Claude Web session could not be checked.");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            SaveClaudeSessionKeyButton.IsEnabled = true;
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                _claudeRefreshTimer.Start();
            }
        }
    }

    private async void DisconnectClaudeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _claudeUsageProvider.DisconnectWebSession();
            ClaudeSessionKeyPasswordBox.Clear();
            UpdateClaudeConnectionSettings();
            await RefreshClaudeUsageAsync();
        }
        catch (Win32Exception)
        {
            ShowClaudeConnectionError("The saved Claude Web session could not be removed.");
        }
    }

    private void UpdateClaudeConnectionSettings()
    {
        try
        {
            bool isConfigured = _claudeUsageProvider.HasWebSession;
            ClaudeConnectionStatusText.Text = isConfigured
                ? "Claude Web sessionKey is saved securely."
                : "Not connected to Claude Web.";
            ClaudeConnectionStatusText.Foreground = isConfigured
                ? new SolidColorBrush(Color.FromRgb(103, 230, 167))
                : new SolidColorBrush(Color.FromRgb(150, 160, 181));
            DisconnectClaudeButton.IsEnabled = isConfigured;
        }
        catch (Win32Exception)
        {
            ShowClaudeConnectionError("The saved Claude Web session could not be read.");
            DisconnectClaudeButton.IsEnabled = false;
        }
    }

    private void ShowClaudeConnectionError(string message)
    {
        ClaudeConnectionStatusText.Text = message;
        ClaudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
    }

    private void ViewReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestReleaseUrl is not null)
        {
            OpenExternalUrl(_latestReleaseUrl);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub Releases...";
        UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 181));

        try
        {
            Version currentVersion = GetApplicationVersion();
            UpdateCheckResult result = await _updateService.CheckAsync(
                currentVersion,
                _lifetimeCancellation.Token);
            if (!result.Succeeded)
            {
                _latestReleaseUrl = null;
                UpdateAvailableBadge.Visibility = Visibility.Collapsed;
                ViewReleaseButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = result.ErrorMessage ?? "The update check failed.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
                return;
            }

            _latestReleaseUrl = result.ReleaseUrl;
            if (result.IsUpdateAvailable)
            {
                UpdateAvailableBadge.Visibility = Visibility.Visible;
                ViewReleaseButton.Visibility = Visibility.Visible;
                UpdateStatusText.Text = $"Version {result.LatestTag} is available.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(103, 230, 167));
            }
            else
            {
                UpdateAvailableBadge.Visibility = Visibility.Collapsed;
                ViewReleaseButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"You're up to date. Latest stable release: {result.LatestTag}.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(197, 204, 218));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void ShowView(FrameworkElement view)
    {
        DashboardView.Visibility = view == DashboardView ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == SettingsView ? Visibility.Visible : Visibility.Collapsed;
        AboutView.Visibility = view == AboutView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenExternalUrl(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            AboutHintText.Text = string.Empty;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AboutHintText.Text = "The link could not be opened.";
        }
    }

    private static Version GetApplicationVersion()
    {
        Version? version = typeof(MainWindow).Assembly.GetName().Version;
        return version ?? new Version(0, 0, 0);
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
