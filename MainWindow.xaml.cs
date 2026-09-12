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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.Models;
using AIUsageMonitor.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Size = System.Windows.Size;

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
    private const double ResizeBorderSize = 8;
    private const double MinimumEmptyGadgetWidth = 220;
    private const double MinimumEmptyGadgetHeight = 160;
    private const double GadgetHorizontalChrome = 32;
    private const double GadgetVerticalChrome = 32;
    private const double ProviderSectionHorizontalPadding = 8;
    private const double TokenSummaryHorizontalPadding = 8;
    private const double TokenSummaryColumnGap = 14;
    private const double TextEdgeSafety = 8;
    private static readonly TimeSpan CodexRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaudeRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KiroRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AntigravityRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TokenUsageRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TokenRollDuration = TimeSpan.FromMilliseconds(520);

    private readonly IAiUsageProvider _codexUsageProvider = new CodexUsageProvider();
    private readonly IAiUsageProvider _kiroUsageProvider = new KiroUsageProvider();
    private readonly AntigravityUsageProvider _antigravityUsageProvider = new();
    private readonly LocalTokenUsageService _localTokenUsageService;
    private readonly ClaudeSessionKeyStore _claudeSessionKeyStore = new();
    private readonly ClaudeWebUsageClient _claudeWebUsageClient = new();
    private readonly ClaudeUsageProvider _claudeUsageProvider;
    private readonly GitHubReleaseUpdateService _updateService = new();
    private readonly GadgetSettingsStore _settingsStore = new();
    private readonly WindowsStartupService _windowsStartupService = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _codexRefreshTimer;
    private readonly DispatcherTimer _claudeRefreshTimer;
    private readonly DispatcherTimer _kiroRefreshTimer;
    private readonly DispatcherTimer _antigravityRefreshTimer;
    private readonly DispatcherTimer _tokenUsageRefreshTimer;
    private readonly DispatcherTimer _tokenRollTimer;
    private readonly Stopwatch _tokenRollStopwatch = new();
    private readonly ObservableCollection<ProviderDisplayOption> _providerOptions = [];
    private readonly Dictionary<string, FrameworkElement> _providerCards = new(StringComparer.OrdinalIgnoreCase);
    private StackPanel _claudeConnectControls = null!;
    private PasswordBox _claudeSessionKeyPasswordBox = null!;
    private System.Windows.Controls.Button _saveClaudeSessionKeyButton = null!;
    private TextBlock _claudeConnectionStatusText = null!;
    private System.Windows.Controls.Button _disconnectClaudeButton = null!;
    private HwndSource? _windowSource;
    private SettingsWindow? _settingsWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayAlwaysOnTopMenuItem;
    private readonly Dictionary<double, Forms.ToolStripMenuItem> _trayFontSizeItems = [];
    private Drawing.Icon? _trayIconResource;
    private Point _dragStart;
    private Point _dragGrabOffset;
    private ProviderDisplayOption? _draggedProvider;
    private ListBoxItem? _draggedProviderContainer;
    private ProviderDragAdorner? _providerDragAdorner;
    private AdornerLayer? _providerDragAdornerLayer;
    private Uri? _latestReleaseUrl;
    private bool _isRefreshingCodex;
    private bool _isRefreshingClaude;
    private bool _isRefreshingKiro;
    private bool _isRefreshingAntigravity;
    private bool _isRefreshingTokenUsage;
    private bool _isCheckingForUpdates;
    private bool _settingsLoaded;
    private bool _isUpdatingWindowsStartupOption;
    private bool _minimumSizeUpdatePending;
    private bool _isUpdatingMinimumSize;
    private long? _displayedTokenTotal;
    private string _tokenRollTargetText = string.Empty;
    private TokenUsageSnapshot? _latestTokenUsageSnapshot;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSettingsWindow();
        _claudeUsageProvider = new ClaudeUsageProvider(_claudeSessionKeyStore, _claudeWebUsageClient);
        _localTokenUsageService = new LocalTokenUsageService(_antigravityUsageProvider);
        InitializeTrayIcon();

        AboutVersionText.Text = $"Version {GetApplicationVersion().ToString(3)}";

        _codexRefreshTimer = new DispatcherTimer
        {
            Interval = CodexRefreshInterval
        };
        _claudeRefreshTimer = new DispatcherTimer
        {
            Interval = ClaudeRefreshInterval
        };
        _kiroRefreshTimer = new DispatcherTimer
        {
            Interval = KiroRefreshInterval
        };
        _antigravityRefreshTimer = new DispatcherTimer
        {
            Interval = AntigravityRefreshInterval
        };
        _tokenUsageRefreshTimer = new DispatcherTimer
        {
            Interval = TokenUsageRefreshInterval
        };
        _tokenRollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _providerOptions.Add(new ProviderDisplayOption("codex", "Codex", "Live limits from the local session"));
        _providerOptions.Add(new ProviderDisplayOption("claude", "Claude", "Live limits from the protected Claude Web session"));
        _providerOptions.Add(new ProviderDisplayOption("kiro", "Kiro", "Local limits from the Kiro CLI", isVisible: false));
        _providerOptions.Add(new ProviderDisplayOption("antigravity", "Antigravity", "Local limits from the running Antigravity IDE", isVisible: false));
        _providerOptions.Add(new ProviderDisplayOption("gemini", "Gemini", "Not connected yet"));
        _providerCards.Add("codex", CodexCard);
        _providerCards.Add("claude", ClaudeCard);
        _providerCards.Add("kiro", KiroCard);
        _providerCards.Add("antigravity", AntigravityCard);
        _providerCards.Add("gemini", GeminiCard);
        ProviderSettingsList.ItemsSource = _providerOptions;
        _codexRefreshTimer.Tick += CodexRefreshTimer_Tick;
        _claudeRefreshTimer.Tick += ClaudeRefreshTimer_Tick;
        _kiroRefreshTimer.Tick += KiroRefreshTimer_Tick;
        _antigravityRefreshTimer.Tick += AntigravityRefreshTimer_Tick;
        _tokenUsageRefreshTimer.Tick += TokenUsageRefreshTimer_Tick;
        _tokenRollTimer.Tick += TokenRollTimer_Tick;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SizeChanged += MainWindow_SizeChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        _codexRefreshTimer.Start();
        _claudeRefreshTimer.Start();
        _kiroRefreshTimer.Start();
        _antigravityRefreshTimer.Start();
        _tokenUsageRefreshTimer.Start();
        await Task.WhenAll(
            RefreshCodexUsageAsync(),
            RefreshClaudeUsageAsync(),
            RefreshKiroUsageAsync(),
            RefreshAntigravityUsageAsync(),
            RefreshTokenUsageAsync(),
            CheckForUpdatesAsync());
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

            Topmost = settings.AlwaysOnTop;
            ApplyFontScale(settings.FontScale);
        }

        _settingsLoaded = true;
        UpdateWindowsStartupOption();
        UpdateClaudeConnectionSettings();
        ApplyProviderLayout();
    }

    private void UpdateWindowsStartupOption()
    {
        _isUpdatingWindowsStartupOption = true;
        try
        {
            StartWithWindowsCheckBox.IsChecked = _windowsStartupService.IsEnabled();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            StartWithWindowsCheckBox.IsChecked = false;
            SettingsHintText.Text = "Windows startup status could not be read";
        }
        finally
        {
            _isUpdatingWindowsStartupOption = false;
        }
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingWindowsStartupOption)
        {
            return;
        }

        try
        {
            bool enabled = StartWithWindowsCheckBox.IsChecked == true;
            _windowsStartupService.SetEnabled(enabled);
            SettingsHintText.Text = enabled
                ? "AI Usage Monitor will start with Windows"
                : "Windows startup disabled";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            SettingsHintText.Text = "Windows startup setting could not be changed";
            UpdateWindowsStartupOption();
        }
    }

    private async void CodexRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshCodexUsageAsync();
    }

    private async void ClaudeRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshClaudeUsageAsync();
    }

    private async void KiroRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshKiroUsageAsync();
    }

    private async void AntigravityRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAntigravityUsageAsync();
    }

    private async void TokenUsageRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshTokenUsageAsync();
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

    private async Task RefreshTokenUsageAsync()
    {
        if (_isRefreshingTokenUsage)
        {
            return;
        }

        _isRefreshingTokenUsage = true;
        try
        {
            HashSet<string> selectedProviderIds = _providerOptions
                .Where(option => option.IsVisible)
                .Select(option => option.ProviderId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            TokenUsageSnapshot snapshot = await _localTokenUsageService.GetTodayUsageAsync(
                selectedProviderIds,
                _lifetimeCancellation.Token);
            ApplyTokenUsageSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing; no UI update is needed.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StopTokenRollAnimation();
            _displayedTokenTotal = null;
            TotalTokensText.Text = "—";
            EstimatedCostText.Text = "$—";
            TokenSummaryGrid.ToolTip = "Local token history could not be read.";
            ScheduleMinimumGadgetSizeUpdate();
        }
        finally
        {
            _isRefreshingTokenUsage = false;
        }
    }

    private async Task RefreshKiroUsageAsync()
    {
        if (_isRefreshingKiro || !IsProviderVisible("kiro"))
        {
            return;
        }

        _isRefreshingKiro = true;
        try
        {
            ApplyKiroSnapshot(await _kiroUsageProvider.GetUsageAsync(_lifetimeCancellation.Token));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isRefreshingKiro = false;
        }
    }

    private async Task RefreshAntigravityUsageAsync()
    {
        if (_isRefreshingAntigravity || !IsProviderVisible("antigravity"))
        {
            return;
        }

        _isRefreshingAntigravity = true;
        try
        {
            ApplyAntigravitySnapshot(await _antigravityUsageProvider.GetUsageAsync(_lifetimeCancellation.Token));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _isRefreshingAntigravity = false;
        }
    }

    private void ApplyTokenUsageSnapshot(TokenUsageSnapshot snapshot)
    {
        _latestTokenUsageSnapshot = snapshot;
        HashSet<string> selectedProviderIds = _providerOptions
            .Where(option => option.IsVisible)
            .Select(option => option.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProviderTokenUsage[] selectedProviders = snapshot.Providers
            .Where(provider => selectedProviderIds.Contains(provider.ProviderId))
            .ToArray();
        long totalTokens = selectedProviders.Sum(provider => provider.TotalTokens);
        decimal estimatedCost = selectedProviders.Sum(provider => provider.EstimatedCostUsd);
        long unpricedTokens = selectedProviders.Sum(provider => provider.UnpricedTokens);

        AnimateTokenTotal(totalTokens);
        string estimatedCostPrefix = unpricedTokens > 0 ? "≥ $" : "$";
        EstimatedCostText.Text = $"{estimatedCostPrefix}{estimatedCost.ToString("F4", EnglishCulture)}";

        string[] providerLines = selectedProviders
            .Where(provider => provider.TotalTokens > 0)
            .Select(provider =>
            {
                string name = provider.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                    ? "Codex"
                    : provider.ProviderId.Equals("claude", StringComparison.OrdinalIgnoreCase)
                        ? "Claude"
                        : provider.ProviderId.Equals("kiro", StringComparison.OrdinalIgnoreCase)
                            ? "Kiro"
                            : provider.ProviderId.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
                                ? "Antigravity"
                                : provider.ProviderId;
                string costPrefix = provider.UnpricedTokens > 0 ? "≥ $" : "$";
                return $"{name}: {provider.TotalTokens.ToString("N0", EnglishCulture)} tokens · {costPrefix}{provider.EstimatedCostUsd.ToString("F4", EnglishCulture)}";
            })
            .ToArray();
        string details = providerLines.Length > 0
            ? string.Join(Environment.NewLine, providerLines)
            : "No local token usage recorded today.";
        if (unpricedTokens > 0)
        {
            details += $"{Environment.NewLine}The estimate excludes {unpricedTokens.ToString("N0", EnglishCulture)} tokens from unknown models.";
        }
        TokenSummaryGrid.ToolTip = details;
        ScheduleMinimumGadgetSizeUpdate();
    }

    private void AnimateTokenTotal(long totalTokens)
    {
        string targetText = totalTokens.ToString("N0", EnglishCulture);
        if (_displayedTokenTotal == totalTokens)
        {
            if (!_tokenRollTimer.IsEnabled)
            {
                TotalTokensText.Text = targetText;
            }
            return;
        }

        _displayedTokenTotal = totalTokens;
        _tokenRollTargetText = targetText;
        _tokenRollStopwatch.Restart();
        _tokenRollTimer.Start();
        RenderTokenRollFrame(0);
    }

    private void TokenRollTimer_Tick(object? sender, EventArgs e)
    {
        double progress = Math.Clamp(
            _tokenRollStopwatch.Elapsed.TotalMilliseconds / TokenRollDuration.TotalMilliseconds,
            0,
            1);
        if (progress >= 1)
        {
            TotalTokensText.Text = _tokenRollTargetText;
            StopTokenRollAnimation();
            ScheduleMinimumGadgetSizeUpdate();
            return;
        }

        RenderTokenRollFrame(progress);
    }

    private void RenderTokenRollFrame(double progress)
    {
        char[] characters = _tokenRollTargetText.ToCharArray();
        int digitCount = characters.Count(char.IsDigit);
        int lockedDigits = progress <= 0.38
            ? 0
            : Math.Clamp((int)Math.Floor((progress - 0.38) / 0.5 * digitCount), 0, digitCount);
        int digitIndex = 0;

        for (int index = 0; index < characters.Length; index++)
        {
            if (!char.IsDigit(characters[index]))
            {
                continue;
            }

            if (digitIndex >= lockedDigits)
            {
                bool isLeadingDigit = digitIndex == 0 && digitCount > 1;
                characters[index] = (char)('0' + Random.Shared.Next(isLeadingDigit ? 1 : 0, 10));
            }
            digitIndex++;
        }

        TotalTokensText.Text = new string(characters);
    }

    private void StopTokenRollAnimation()
    {
        _tokenRollTimer.Stop();
        _tokenRollStopwatch.Stop();
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
        CodexPlanText.Text = snapshot.PlanLabel;
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

        ScheduleMinimumGadgetSizeUpdate();
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
        ScheduleMinimumGadgetSizeUpdate();
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
            ? snapshot.PlanLabel
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
        ScheduleMinimumGadgetSizeUpdate();
    }

    private void ApplyKiroSnapshot(AiUsageSnapshot snapshot)
    {
        ApplyProviderStatus(snapshot, KiroStatusText, KiroPlanText, KiroConnectionHint, KiroConnectionHintText,
            "Install and sign in to the Kiro CLI to display usage limits.");
        if (snapshot.IsAvailable && snapshot.Session is null)
        {
            KiroStatusText.Text = "Managed";
            KiroConnectionHint.Visibility = Visibility.Visible;
            KiroConnectionHintText.Text = snapshot.StatusMessage
                ?? "This managed Kiro plan does not expose credit metrics.";
        }
        ApplyWindow(snapshot.Session, KiroPrimaryUsageText, KiroPrimaryProgress, KiroPrimaryResetText, includeDate: true);
        KiroPrimaryLabelText.Text = snapshot.Session?.Label.ToUpperInvariant() ?? "CREDITS";

        KiroBonusPanel.Visibility = snapshot.Weekly is null ? Visibility.Collapsed : Visibility.Visible;
        KiroBonusLabelText.Text = snapshot.Weekly?.Label.ToUpperInvariant() ?? "BONUS";
        ApplyWindow(snapshot.Weekly, KiroBonusUsageText, KiroBonusProgress, KiroBonusResetText, includeDate: true);
        ScheduleMinimumGadgetSizeUpdate();
    }

    private void ApplyAntigravitySnapshot(AiUsageSnapshot snapshot)
    {
        ApplyProviderStatus(snapshot, AntigravityStatusText, AntigravityPlanText,
            AntigravityConnectionHint, AntigravityConnectionHintText,
            "Open and sign in to Antigravity IDE to display usage limits.");
        AntigravitySessionLabelText.Text = snapshot.Session?.Label.ToUpperInvariant() ?? "SESSION";
        AntigravityWeeklyLabelText.Text = snapshot.Weekly?.Label.ToUpperInvariant() ?? "WEEKLY";
        ApplyWindow(snapshot.Session, AntigravitySessionUsageText, AntigravitySessionProgress,
            AntigravitySessionResetText, includeDate: false);
        ApplyWindow(snapshot.Weekly, AntigravityWeeklyUsageText, AntigravityWeeklyProgress,
            AntigravityWeeklyResetText, includeDate: true);
        ScheduleMinimumGadgetSizeUpdate();
    }

    private static void ApplyProviderStatus(
        AiUsageSnapshot snapshot,
        TextBlock statusText,
        System.Windows.Documents.Run planText,
        FrameworkElement connectionHint,
        TextBlock connectionHintText,
        string notDetectedMessage)
    {
        connectionHint.Visibility = snapshot.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        statusText.Text = snapshot.IsAvailable
            ? "Live"
            : snapshot.IssueKind switch
            {
                ProviderIssueKind.NotDetected => "Not detected",
                ProviderIssueKind.AuthenticationRequired => "Sign-in required",
                ProviderIssueKind.RateLimited => "Rate limited",
                _ => "Unavailable"
            };
        statusText.Foreground = snapshot.IsAvailable
            ? new SolidColorBrush(Color.FromRgb(103, 230, 167))
            : new SolidColorBrush(Color.FromRgb(240, 184, 137));
        planText.Text = snapshot.IsAvailable
            ? snapshot.PlanLabel
            : snapshot.StatusMessage ?? notDetectedMessage;
        planText.ToolTip = snapshot.IsAvailable ? null : snapshot.StatusMessage;
        connectionHintText.Text = snapshot.IssueKind == ProviderIssueKind.NotDetected
            ? notDetectedMessage
            : snapshot.StatusMessage ?? "Usage data is temporarily unavailable. The app will try again automatically.";
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
        _kiroRefreshTimer.Stop();
        _antigravityRefreshTimer.Stop();
        _tokenUsageRefreshTimer.Stop();
        StopTokenRollAnimation();
        _windowSource?.RemoveHook(WindowProcedure);
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _codexUsageProvider.Dispose();
        _claudeUsageProvider.Dispose();
        _kiroUsageProvider.Dispose();
        _antigravityUsageProvider.Dispose();
        _claudeWebUsageClient.Dispose();
        _updateService.Dispose();
        _settingsWindow?.ClosePermanently();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconResource?.Dispose();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsWindow?.Hide();
    }

    private void CloseAboutButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsWindow?.Hide();
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

    private void ClaudeSessionKeyPasswordBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_saveClaudeSessionKeyButton.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        _saveClaudeSessionKeyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private async void SaveClaudeSessionKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string sessionKey = _claudeSessionKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            _claudeConnectionStatusText.Text = "Paste the sessionKey value first.";
            _claudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
            return;
        }

        _saveClaudeSessionKeyButton.IsEnabled = false;
        _claudeRefreshTimer.Stop();
        _claudeConnectionStatusText.Text = "Checking the Claude Web session...";
        _claudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 181));
        try
        {
            AiUsageSnapshot snapshot = await _claudeUsageProvider.ConnectWebSessionAsync(
                sessionKey,
                _lifetimeCancellation.Token);
            _claudeSessionKeyPasswordBox.Clear();
            _claudeConnectionStatusText.Text = "Connected securely through Claude Web.";
            _claudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(103, 230, 167));
            ApplyClaudeSnapshot(snapshot);
            UpdateClaudeConnectionSettings();
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
            _saveClaudeSessionKeyButton.IsEnabled = true;
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
            _claudeSessionKeyPasswordBox.Clear();
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
            _claudeConnectionStatusText.Text = isConfigured
                ? "Claude Web sessionKey is saved securely."
                : "Not connected to Claude Web.";
            _claudeConnectionStatusText.Foreground = isConfigured
                ? new SolidColorBrush(Color.FromRgb(103, 230, 167))
                : new SolidColorBrush(Color.FromRgb(150, 160, 181));
            _disconnectClaudeButton.IsEnabled = isConfigured;
            _claudeConnectControls.Visibility = isConfigured ? Visibility.Collapsed : Visibility.Visible;
            _disconnectClaudeButton.Visibility = isConfigured ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Win32Exception)
        {
            ShowClaudeConnectionError("The saved Claude Web session could not be read.");
            _disconnectClaudeButton.IsEnabled = false;
            _disconnectClaudeButton.Visibility = Visibility.Collapsed;
            _claudeConnectControls.Visibility = Visibility.Visible;
        }
    }

    private void ShowClaudeConnectionError(string message)
    {
        _claudeConnectionStatusText.Text = message;
        _claudeConnectionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
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
                ViewReleaseButton.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = result.ErrorMessage ?? "The update check failed.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(240, 184, 137));
                return;
            }

            _latestReleaseUrl = result.ReleaseUrl;
            if (result.IsUpdateAvailable)
            {
                ViewReleaseButton.Visibility = Visibility.Visible;
                UpdateStatusText.Text = $"Version {result.LatestTag} is available.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(103, 230, 167));
            }
            else
            {
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
        if (_latestTokenUsageSnapshot is TokenUsageSnapshot snapshot)
        {
            ApplyTokenUsageSnapshot(snapshot);
        }
        await SaveSettingsAsync();
        if (sender is CheckBox { DataContext: ProviderDisplayOption enabledOption }
            && enabledOption.IsVisible)
        {
            if (enabledOption.ProviderId.Equals("kiro", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshKiroUsageAsync();
            }
            else if (enabledOption.ProviderId.Equals("antigravity", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshAntigravityUsageAsync();
            }
            await RefreshTokenUsageAsync();
        }
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
            ProviderCardsPanel.Children.Add(visibleCards[index]);
            if (index < visibleCards.Count - 1)
            {
                ProviderCardsPanel.Children.Add(new Border
                {
                    Style = (Style)FindResource("ProviderSeparatorStyle")
                });
            }
        }

        NoProvidersMessage.Visibility = visibleCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMinimumGadgetSizeUpdate();
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProviderDisplayOption option })
        {
            _draggedProvider = option;
            _dragStart = e.GetPosition(ProviderSettingsList);
            _draggedProviderContainer = FindVisualParent<ListBoxItem>(sender as DependencyObject);
            if (_draggedProviderContainer is not null)
            {
                _dragGrabOffset = e.GetPosition(_draggedProviderContainer);
            }
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
        BeginProviderDrag(current);
        try
        {
            DragDrop.DoDragDrop(ProviderSettingsList, dragged, DragDropEffects.Move);
        }
        finally
        {
            EndProviderDrag();
        }
    }

    private void ProviderSettingsList_DragOver(object sender, DragEventArgs e)
    {
        _providerDragAdorner?.UpdatePosition(e.GetPosition(ProviderSettingsList));
        ListBoxItem? targetContainer = FindVisualParent<ListBoxItem>(
            ProviderSettingsList.InputHitTest(e.GetPosition(ProviderSettingsList)) as DependencyObject);
        ProviderSettingsList.SelectedItem = targetContainer?.DataContext;
        e.Effects = e.Data.GetDataPresent(typeof(ProviderDisplayOption))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProviderSettingsList_DragLeave(object sender, DragEventArgs e)
    {
        ProviderSettingsList.SelectedItem = null;
    }

    private void ProviderSettingsList_GiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        if (_providerDragAdorner is not null)
        {
            Drawing.Point screenPosition = Forms.Cursor.Position;
            Point position = ProviderSettingsList.PointFromScreen(
                new Point(screenPosition.X, screenPosition.Y));
            _providerDragAdorner.UpdatePosition(position);
        }
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void BeginProviderDrag(Point position)
    {
        if (_draggedProviderContainer is null)
        {
            return;
        }

        _providerDragAdornerLayer = AdornerLayer.GetAdornerLayer(ProviderSettingsList);
        if (_providerDragAdornerLayer is not null)
        {
            _providerDragAdorner = new ProviderDragAdorner(
                ProviderSettingsList,
                _draggedProviderContainer,
                _dragGrabOffset);
            _providerDragAdorner.UpdatePosition(position);
            _providerDragAdornerLayer.Add(_providerDragAdorner);
        }
        _draggedProviderContainer.Opacity = 0.28;
    }

    private void EndProviderDrag()
    {
        if (_providerDragAdornerLayer is not null && _providerDragAdorner is not null)
        {
            _providerDragAdornerLayer.Remove(_providerDragAdorner);
        }
        if (_draggedProviderContainer is not null)
        {
            _draggedProviderContainer.Opacity = 1;
        }

        ProviderSettingsList.SelectedItem = null;
        _providerDragAdorner = null;
        _providerDragAdornerLayer = null;
        _draggedProviderContainer = null;
        _draggedProvider = null;
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
            AlwaysOnTop = Topmost,
            FontScale = DashboardScaleTransform.ScaleX,
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
        int hit = left ? HtLeft : right ? HtRight : 0;

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
        bool canDrag = DashboardView.IsVisible || e.GetPosition(this).Y <= 82;
        if (e.ButtonState == MouseButtonState.Pressed && canDrag && !isInteractiveControl)
        {
            DragMove();
        }
    }

    private void InitializeTrayIcon()
    {
        Drawing.Icon sourceIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? Drawing.SystemIcons.Application;
        _trayIconResource = (Drawing.Icon)sourceIcon.Clone();

        Forms.ContextMenuStrip trayMenu = new();
        trayMenu.Items.Add("Show or hide", null, (_, _) => Dispatcher.Invoke(ToggleGadgetVisibility));
        trayMenu.Items.Add("Settings...", null, (_, _) => Dispatcher.Invoke(OpenSettings));

        Forms.ToolStripMenuItem fontSizeMenu = new("Font size");
        AddTrayFontSizeItem(fontSizeMenu, "Small", 0.85);
        AddTrayFontSizeItem(fontSizeMenu, "Default", 1.0);
        AddTrayFontSizeItem(fontSizeMenu, "Large", 1.15);
        trayMenu.Items.Add(fontSizeMenu);

        _trayAlwaysOnTopMenuItem = new Forms.ToolStripMenuItem("Always on top", null, (_, _) =>
            Dispatcher.Invoke(async () => await ToggleAlwaysOnTopAsync()));
        trayMenu.Items.Add(_trayAlwaysOnTopMenuItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
        trayMenu.Opening += (_, _) => Dispatcher.Invoke(UpdateMenuChecks);

        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = trayMenu,
            Icon = _trayIconResource,
            Text = "AI Usage Monitor",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleGadgetVisibility);
    }

    private void AddTrayFontSizeItem(Forms.ToolStripMenuItem parent, string label, double scale)
    {
        Forms.ToolStripMenuItem item = new(label, null, (_, _) =>
            Dispatcher.Invoke(async () => await SetFontScaleAsync(scale)));
        item.Tag = scale;
        _trayFontSizeItems.Add(scale, item);
        parent.DropDownItems.Add(item);
    }

    private void GadgetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateMenuChecks();
    }

    private void ContextSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private async void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ToggleAlwaysOnTopAsync();
    }

    private async void FontSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string scaleText }
            && double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            await SetFontScaleAsync(scale);
        }
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSettings()
    {
        ShowGadget();
        UpdateWindowsStartupOption();
        UpdateClaudeConnectionSettings();
        if (_settingsWindow is null)
        {
            return;
        }

        if (_settingsWindow.Owner is null)
        {
            _settingsWindow.Owner = this;
        }

        _settingsWindow.ShowSettingsTab();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ToggleGadgetVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        ShowGadget();
    }

    private void ShowGadget()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private async Task ToggleAlwaysOnTopAsync()
    {
        Topmost = !Topmost;
        UpdateMenuChecks();
        await SaveSettingsAsync();
    }

    private async Task SetFontScaleAsync(double scale)
    {
        ApplyFontScale(scale);
        UpdateMenuChecks();
        await SaveSettingsAsync();
    }

    private void ApplyFontScale(double scale)
    {
        double safeScale = double.IsFinite(scale) ? Math.Clamp(scale, 0.85, 1.15) : 1.0;
        DashboardScaleTransform.ScaleX = safeScale;
        DashboardScaleTransform.ScaleY = safeScale;
        ScheduleMinimumGadgetSizeUpdate();
    }

    private void InitializeSettingsWindow()
    {
        if (SettingsView.Resources["ClaudeConnectionPanel"] is not FrameworkElement claudeConnectionPanel)
        {
            throw new InvalidOperationException("The Claude connection panel could not be created.");
        }

        _claudeConnectControls = FindRequiredNamedElement<StackPanel>(claudeConnectionPanel, "ClaudeConnectControls");
        _claudeSessionKeyPasswordBox = FindRequiredNamedElement<PasswordBox>(claudeConnectionPanel, "ClaudeSessionKeyPasswordBox");
        _saveClaudeSessionKeyButton = FindRequiredNamedElement<System.Windows.Controls.Button>(claudeConnectionPanel, "SaveClaudeSessionKeyButton");
        _claudeConnectionStatusText = FindRequiredNamedElement<TextBlock>(claudeConnectionPanel, "ClaudeConnectionStatusText");
        _disconnectClaudeButton = FindRequiredNamedElement<System.Windows.Controls.Button>(claudeConnectionPanel, "DisconnectClaudeButton");

        if (SettingsView.Parent is not Panel parent || AboutView.Parent != parent)
        {
            throw new InvalidOperationException("The settings and About views must share a panel.");
        }

        parent.Children.Remove(SettingsView);
        parent.Children.Remove(AboutView);
        SettingsView.Visibility = Visibility.Visible;
        AboutView.Visibility = Visibility.Visible;
        _settingsWindow = new SettingsWindow(SettingsView, AboutView);
    }

    private static T FindRequiredNamedElement<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T { Name: var elementName } match && elementName == name)
        {
            return match;
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try
            {
                return FindRequiredNamedElement<T>(VisualTreeHelper.GetChild(root, index), name);
            }
            catch (InvalidOperationException)
            {
                // Continue searching the remaining descendants.
            }
        }

        throw new InvalidOperationException($"The settings control '{name}' could not be found.");
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isUpdatingMinimumSize && e.WidthChanged && DashboardView.IsVisible)
        {
            ScheduleMinimumGadgetSizeUpdate();
        }
    }

    private void ScheduleMinimumGadgetSizeUpdate()
    {
        if (_minimumSizeUpdatePending)
        {
            return;
        }

        _minimumSizeUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _minimumSizeUpdatePending = false;
            UpdateMinimumGadgetSize();
        });
    }

    private void UpdateMinimumGadgetSize()
    {
        if (!DashboardView.IsVisible)
        {
            return;
        }

        double scale = DashboardScaleTransform.ScaleX;
        double textDrivenContentWidth = GetMinimumContentWidth();
        double minimumWidth = Math.Ceiling(Math.Max(
            MinimumEmptyGadgetWidth * scale,
            textDrivenContentWidth * scale + GadgetHorizontalChrome));
        bool shouldExpandWidth = ActualWidth < minimumWidth;
        double effectiveWidth = Math.Max(ActualWidth, minimumWidth);
        double availableWidth = Math.Max(1, (effectiveWidth - GadgetHorizontalChrome) / scale);
        FrameworkElement measuredContent = DashboardContentPanel;
        measuredContent.Measure(new Size(availableWidth, double.PositiveInfinity));

        double minimumHeight = Math.Max(
            MinimumEmptyGadgetHeight,
            Math.Ceiling(measuredContent.DesiredSize.Height * scale + GadgetVerticalChrome));

        _isUpdatingMinimumSize = true;
        try
        {
            MaxHeight = double.PositiveInfinity;
            MinWidth = minimumWidth;
            if (shouldExpandWidth)
            {
                Width = minimumWidth;
            }
            MinHeight = minimumHeight;
            MaxHeight = minimumHeight;
            Height = minimumHeight;
        }
        finally
        {
            _isUpdatingMinimumSize = false;
        }
    }

    private double GetMinimumContentWidth()
    {
        double tokensColumnWidth = Math.Max(
            MeasureTextWidth(TokensTodayLabelText),
            MeasureTextWidth(TotalTokensText));
        double costColumnWidth = Math.Max(
            MeasureTextWidth(EstimatedCostLabelText),
            MeasureTextWidth(EstimatedCostText));
        double minimumContentWidth = tokensColumnWidth
            + TokenSummaryColumnGap
            + costColumnWidth
            + TokenSummaryHorizontalPadding;

        if (IsProviderVisible("codex"))
        {
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(CodexSessionResetText) + ProviderSectionHorizontalPadding);
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(CodexWeeklyResetText) + ProviderSectionHorizontalPadding);
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(CodexResetCreditsText)
                    + MeasureTextWidth(CodexResetExpiryText)
                    + 28
                    + ProviderSectionHorizontalPadding);
        }

        if (IsProviderVisible("claude"))
        {
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(ClaudeSessionResetText) + ProviderSectionHorizontalPadding);
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(ClaudeWeeklyResetText) + ProviderSectionHorizontalPadding);
        }

        if (IsProviderVisible("kiro"))
        {
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(KiroPrimaryResetText) + ProviderSectionHorizontalPadding);
            if (KiroBonusPanel.IsVisible)
            {
                minimumContentWidth = Math.Max(
                    minimumContentWidth,
                    MeasureTextWidth(KiroBonusResetText) + ProviderSectionHorizontalPadding);
            }
        }

        if (IsProviderVisible("antigravity"))
        {
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(AntigravitySessionResetText) + ProviderSectionHorizontalPadding);
            minimumContentWidth = Math.Max(
                minimumContentWidth,
                MeasureTextWidth(AntigravityWeeklyResetText) + ProviderSectionHorizontalPadding);
        }

        return minimumContentWidth + TextEdgeSafety;
    }

    private bool IsProviderVisible(string providerId)
    {
        return _providerOptions.Any(option =>
            option.IsVisible
            && string.Equals(option.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    private double MeasureTextWidth(TextBlock textBlock)
    {
        if (string.IsNullOrEmpty(textBlock.Text))
        {
            return 0;
        }

        FormattedText formattedText = new(
            textBlock.Text,
            CultureInfo.CurrentUICulture,
            textBlock.FlowDirection,
            new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch),
            textBlock.FontSize,
            textBlock.Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(formattedText.WidthIncludingTrailingWhitespace);
    }

    private void UpdateMenuChecks()
    {
        AlwaysOnTopMenuItem.IsChecked = Topmost;
        if (_trayAlwaysOnTopMenuItem is not null)
        {
            _trayAlwaysOnTopMenuItem.Checked = Topmost;
        }

        double currentScale = DashboardScaleTransform.ScaleX;
        SmallFontMenuItem.IsChecked = Math.Abs(currentScale - 0.85) < 0.001;
        DefaultFontMenuItem.IsChecked = Math.Abs(currentScale - 1.0) < 0.001;
        LargeFontMenuItem.IsChecked = Math.Abs(currentScale - 1.15) < 0.001;
        foreach ((double scale, Forms.ToolStripMenuItem item) in _trayFontSizeItems)
        {
            item.Checked = Math.Abs(currentScale - scale) < 0.001;
        }
    }
}
