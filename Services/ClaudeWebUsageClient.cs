using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIUsageMonitor.Services;

public sealed class ClaudeWebUsageClient : IDisposable
{
    private const string ClaudeBaseUrl = "https://claude.ai";
    private const string SessionCookieName = "sessionKey";

    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly WebView2 _webView = new();
    private readonly Window _hostWindow;
    private Task? _initializationTask;
    private string? _organizationId;
    private string? _planCode;
    private string? _rateLimitTier;
    private string? _sessionFingerprint;
    private bool _disposed;

    public ClaudeWebUsageClient()
    {
        _hostWindow = new Window
        {
            Title = "AI Usage Monitor web transport",
            Width = 1,
            Height = 1,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Content = _webView
        };
    }

    public async Task<ClaudeWebUsageResult> GetUsageAsync(
        string sessionKey,
        bool replaceBrowserSession,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            string sessionFingerprint = CreateSessionFingerprint(sessionKey);
            bool sessionChanged = replaceBrowserSession || !string.Equals(
                _sessionFingerprint,
                sessionFingerprint,
                StringComparison.Ordinal);
            if (sessionChanged)
            {
                _sessionFingerprint = sessionFingerprint;
                _organizationId = null;
                _planCode = null;
                _rateLimitTier = null;
            }

            await EnsureInitializedAsync(cancellationToken);
            CoreWebView2 core = _webView.CoreWebView2
                ?? throw new InvalidOperationException("The Claude browser transport could not be initialized.");

            if (sessionChanged)
            {
                IReadOnlyList<CoreWebView2Cookie> currentCookies = await core.CookieManager.GetCookiesAsync(ClaudeBaseUrl);
                foreach (CoreWebView2Cookie cookie in currentCookies.Where(cookie =>
                             string.Equals(cookie.Name, SessionCookieName, StringComparison.Ordinal)))
                {
                    core.CookieManager.DeleteCookie(cookie);
                }

                CoreWebView2Cookie sessionCookie = core.CookieManager.CreateCookie(
                    SessionCookieName,
                    sessionKey,
                    ".claude.ai",
                    "/");
                sessionCookie.IsHttpOnly = true;
                sessionCookie.IsSecure = true;
                core.CookieManager.AddOrUpdateCookie(sessionCookie);
            }

            string scriptResult = await ExecuteUsageScriptAsync(
                core,
                _organizationId,
                _planCode,
                _rateLimitTier,
                cancellationToken);
            using JsonDocument outerDocument = JsonDocument.Parse(scriptResult);
            using JsonDocument? innerDocument = outerDocument.RootElement.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(outerDocument.RootElement.GetString()
                    ?? throw new JsonException("Claude Web returned an empty script result."))
                : null;
            JsonElement result = innerDocument?.RootElement ?? outerDocument.RootElement;
            int status = result.TryGetProperty("status", out JsonElement statusElement)
                && statusElement.TryGetInt32(out int parsedStatus)
                    ? parsedStatus
                    : 0;
            if (!result.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean())
            {
                if (status == 404)
                {
                    _organizationId = null;
                    _planCode = null;
                    _rateLimitTier = null;
                }
                bool isChallenge = result.TryGetProperty("challenge", out JsonElement challengeElement)
                    && challengeElement.ValueKind is JsonValueKind.True;
                throw new ClaudeWebException(
                    status,
                    GetErrorMessage(result, scriptResult.Length),
                    isChallenge);
            }

            if (!result.TryGetProperty("usage", out JsonElement usage))
            {
                throw new ClaudeWebException(0, "Claude Web did not return usage data.");
            }

            if (result.TryGetProperty("organizationId", out JsonElement organizationIdElement)
                && organizationIdElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(organizationIdElement.GetString()))
            {
                _organizationId = organizationIdElement.GetString();
            }

            _planCode = GetOptionalString(result, "planCode") ?? _planCode;
            _rateLimitTier = GetOptionalString(result, "rateLimitTier") ?? _rateLimitTier;

            JsonDocument usageDocument = JsonDocument.Parse(usage.GetRawText());
            return new ClaudeWebUsageResult(
                usageDocument,
                _planCode,
                _rateLimitTier);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webView.Dispose();
        _hostWindow.Close();
        _requestLock.Dispose();
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        _initializationTask ??= InitializeAsync();
        return _initializationTask.WaitAsync(cancellationToken);
    }

    private async Task InitializeAsync()
    {
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIUsageMonitor",
            "WebView2");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder);

        _hostWindow.Show();
        await _webView.EnsureCoreWebView2Async(environment);
        CoreWebView2 core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        await NavigateAsync(core, $"{ClaudeBaseUrl}/robots.txt");
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url)
    {
        TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
        {
            completion.TrySetResult(eventArgs);
        }

        core.NavigationCompleted += NavigationCompleted;
        try
        {
            core.Navigate(url);
            CoreWebView2NavigationCompletedEventArgs result = await completion.Task;
            if (!result.IsSuccess)
            {
                throw new HttpRequestException("Claude Web could not be reached.");
            }
        }
        finally
        {
            core.NavigationCompleted -= NavigationCompleted;
        }
    }

    private static async Task<string> ExecuteUsageScriptAsync(
        CoreWebView2 core,
        string? organizationId,
        string? planCode,
        string? rateLimitTier,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
        {
            try
            {
                using JsonDocument message = JsonDocument.Parse(eventArgs.TryGetWebMessageAsString());
                JsonElement root = message.RootElement;
                if (root.TryGetProperty("requestId", out JsonElement messageRequestId)
                    && string.Equals(messageRequestId.GetString(), requestId, StringComparison.Ordinal)
                    && root.TryGetProperty("payload", out JsonElement payload))
                {
                    completion.TrySetResult(payload.GetRawText());
                }
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                // Ignore messages not produced by this request.
            }
        }

        core.WebMessageReceived += WebMessageReceived;
        try
        {
            await core.ExecuteScriptAsync(BuildUsageScript(
                requestId,
                organizationId,
                planCode,
                rateLimitTier));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        finally
        {
            core.WebMessageReceived -= WebMessageReceived;
        }
    }

    private static string CreateSessionFingerprint(string sessionKey)
    {
        byte[] sessionBytes = Encoding.UTF8.GetBytes(sessionKey);
        try
        {
            return Convert.ToHexString(SHA256.HashData(sessionBytes));
        }
        finally
        {
            Array.Clear(sessionBytes);
        }
    }

    private static string GetErrorMessage(JsonElement result, int scriptResultLength)
    {
        if (result.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            return error.GetString()!;
        }

        string properties = result.ValueKind == JsonValueKind.Object
            ? string.Join(",", result.EnumerateObject().Select(property => property.Name))
            : string.Empty;
        return $"Claude Web returned an unexpected browser result "
            + $"(type: {result.ValueKind}, properties: {properties}, length: {scriptResultLength}).";
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
    }

    private static string BuildUsageScript(
        string requestId,
        string? organizationId,
        string? planCode,
        string? rateLimitTier)
    {
        string organizationIdJson = JsonSerializer.Serialize(organizationId ?? string.Empty);
        string planCodeJson = JsonSerializer.Serialize(planCode ?? string.Empty);
        string rateLimitTierJson = JsonSerializer.Serialize(rateLimitTier ?? string.Empty);
        return $$"""
        (async () => {
          const result = await (async () => {
            try {
            const request = async (url) => {
              const response = await fetch(url, {
                credentials: 'include',
                headers: { 'Accept': 'application/json' }
              });
              const text = await response.text();
              let body = null;
              try { body = text ? JSON.parse(text) : null; } catch { }
              const contentType = String(response.headers.get('content-type') || '').toLowerCase();
              const challenge = response.headers.get('cf-mitigated') === 'challenge'
                || (!response.ok && contentType.includes('text/html'));
              return { response, body, text, challenge };
            };

            let organizationId = {{organizationIdJson}};
            let planCode = {{planCodeJson}};
            let rateLimitTier = {{rateLimitTierJson}};
            if (!organizationId) {
              const organizationsResult = await request('/api/organizations');
              if (!organizationsResult.response.ok) {
                return {
                  ok: false,
                  status: organizationsResult.response.status,
                  challenge: organizationsResult.challenge,
                  error: 'Claude Web rejected the saved session.'
                };
              }

              const value = organizationsResult.body;
              const organizations = Array.isArray(value)
                ? value
                : Array.isArray(value?.organizations)
                  ? value.organizations
                  : Array.isArray(value?.data) ? value.data : [];
              const idOf = organization => String(
                organization?.uuid || organization?.id || organization?.organization_uuid || '').trim();
              const capabilitiesOf = organization => Array.isArray(organization?.capabilities)
                ? organization.capabilities.map(value => String(value).toLowerCase())
                : [];
              const organization = organizations.find(value => capabilitiesOf(value).includes('chat'))
                || organizations.find(value => {
                  const capabilities = capabilitiesOf(value);
                  return !(capabilities.length === 1 && capabilities[0] === 'api');
                })
                || organizations[0];
              organizationId = idOf(organization);
              const capabilities = new Set(capabilitiesOf(organization));
              if (capabilities.has('claude_max')) {
                planCode = 'max';
              } else if (capabilities.has('claude_pro')) {
                planCode = 'pro';
              } else if (capabilities.has('raven')) {
                const ravenType = String(organization?.raven_type || '').trim().toLowerCase();
                planCode = ravenType === 'enterprise' ? 'enterprise' : ravenType ? 'team' : '';
              }
              rateLimitTier = String(organization?.rate_limit_tier || '').trim();
            }
            if (!organizationId) {
              return { ok: false, status: 0, error: 'Claude Web organization was not found.' };
            }

            const usageResult = await request(`/api/organizations/${encodeURIComponent(organizationId)}/usage`);
            if (!usageResult.response.ok) {
              return {
                ok: false,
                status: usageResult.response.status,
                challenge: usageResult.challenge,
                error: 'Claude Web could not load usage data.'
              };
            }

            return {
              ok: true,
              status: usageResult.response.status,
              organizationId,
              planCode,
              rateLimitTier,
              usage: usageResult.body
            };
            } catch (error) {
              return { ok: false, status: 0, error: String(error?.message || error) };
            }
          })();
          window.chrome.webview.postMessage(JSON.stringify({
            requestId: '{{requestId}}',
            payload: result
          }));
        })();
        """;
    }
}

public sealed record ClaudeWebUsageResult(
    JsonDocument Document,
    string? PlanCode,
    string? RateLimitTier) : IDisposable
{
    public void Dispose() => Document.Dispose();
}

public sealed class ClaudeWebException(
    int statusCode,
    string message,
    bool isChallenge = false) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public bool IsChallenge { get; } = isChallenge;
}
