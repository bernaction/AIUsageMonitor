using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageGadget.Models;

namespace AIUsageGadget.Services;

public sealed class CodexUsageProvider : IAiUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public CodexUsageProvider(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string ProviderId => "codex";

    public async Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            CodexCredentials credentials = await ReadCredentialsAsync(cancellationToken);
            using JsonDocument usageDocument = await GetJsonAsync(
                UsageUrl,
                credentials,
                includeResetHeaders: false,
                cancellationToken);

            ResetCreditsInfo? resetCredits = ParseEmbeddedResetCredits(usageDocument.RootElement);
            try
            {
                using JsonDocument resetDocument = await GetJsonAsync(
                    ResetCreditsUrl,
                    credentials,
                    includeResetHeaders: true,
                    cancellationToken);
                resetCredits = ParseResetCredits(resetDocument.RootElement) ?? resetCredits;
            }
            catch (ProviderException)
            {
                // Reset credits are optional; session and weekly usage remain useful.
            }
            catch (HttpRequestException)
            {
                // Preserve the main usage response if only this optional call fails.
            }

            return ParseUsage(usageDocument.RootElement, resetCredits, DateTimeOffset.Now);
        }
        catch (ProviderException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.UserMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Permission to read the local Codex session was denied.");
        }
        catch (JsonException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Codex data format has changed.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Codex request timed out.");
        }
        catch (HttpRequestException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Codex could not be reached.");
        }
        catch (IOException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The local Codex session could not be read.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string ResolveAuthPath()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome;
        return Path.Combine(codexHome, "auth.json");
    }

    private static async Task<CodexCredentials> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        string authPath = ResolveAuthPath();
        if (!File.Exists(authPath))
        {
            throw new ProviderException("Sign in to Codex to enable monitoring.");
        }

        await using FileStream stream = new(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;
        JsonElement tokens = TryGetProperty(root, "tokens", out JsonElement tokensElement)
            ? tokensElement
            : root;
        string accessToken = GetString(tokens, "access_token", "accessToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ProviderException("The local Codex session does not contain a valid token.");
        }

        string accountId = GetString(tokens, "account_id", "accountId");
        return new CodexCredentials(accessToken, accountId);
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        CodexCredentials credentials,
        bool includeResetHeaders,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AIUsageGadget/0.1");
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", credentials.AccountId);
        }
        if (includeResetHeaders)
        {
            request.Headers.TryAddWithoutValidation("openai-beta", "codex-1");
            request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderException("The Codex session has expired. Sign in again.");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderException("Codex temporarily rate-limited the requests.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException("The Codex usage service is unavailable.");
        }

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    }

    private static AiUsageSnapshot ParseUsage(
        JsonElement root,
        ResetCreditsInfo? resetCredits,
        DateTimeOffset updatedAt)
    {
        string planLabel = FormatPlanLabel(GetString(root, "plan_type", "planType"));
        JsonElement rateLimit = GetRequiredObject(root, "rate_limit", "rateLimit");

        UsageWindow? primary = ParseWindow(rateLimit, "primary_window", "primaryWindow", "Session", updatedAt);
        UsageWindow? secondary = ParseWindow(rateLimit, "secondary_window", "secondaryWindow", "Weekly", updatedAt);
        UsageWindow? session = PickByDuration(primary, secondary, TimeSpan.FromHours(5)) ?? primary;
        UsageWindow? weekly = PickByDuration(primary, secondary, TimeSpan.FromDays(7))
            ?? (ReferenceEquals(session, primary) ? secondary : primary);
        UsageWindow? reserve = ParseReserve(root, updatedAt);

        if (session is null && weekly is null)
        {
            throw new ProviderException("Codex did not return any usage windows.");
        }

        return new AiUsageSnapshot(
            "codex",
            planLabel,
            session,
            weekly,
            reserve,
            resetCredits,
            updatedAt,
            true,
            null);
    }

    private static UsageWindow? ParseReserve(JsonElement root, DateTimeOffset now)
    {
        if (!TryGetProperty(root, "additional_rate_limits", out JsonElement additional)
            && !TryGetProperty(root, "additionalRateLimits", out additional))
        {
            return null;
        }

        IEnumerable<JsonElement> entries = additional.ValueKind == JsonValueKind.Array
            ? additional.EnumerateArray()
            : additional.ValueKind == JsonValueKind.Object
                ? new[] { additional }
                : Array.Empty<JsonElement>();

        foreach (JsonElement entry in entries)
        {
            if (!TryGetProperty(entry, "rate_limit", out JsonElement rateLimit)
                && !TryGetProperty(entry, "rateLimit", out rateLimit))
            {
                continue;
            }

            string limitName = GetString(entry, "limit_name", "limitName");
            string model = GetString(entry, "normal_model_slug", "normalModelSlug");
            string label = model.Contains("luna", StringComparison.OrdinalIgnoreCase)
                ? "Luna Reserve"
                : string.IsNullOrWhiteSpace(limitName) ? "Reserve" : Humanize(limitName);
            UsageWindow? reserve = ParseWindow(rateLimit, "primary_window", "primaryWindow", label, now);
            if (reserve is not null)
            {
                return reserve with { Label = label };
            }
        }

        return null;
    }

    private static UsageWindow? ParseWindow(
        JsonElement parent,
        string snakeCaseName,
        string camelCaseName,
        string label,
        DateTimeOffset now)
    {
        if (!TryGetProperty(parent, snakeCaseName, out JsonElement window)
            && !TryGetProperty(parent, camelCaseName, out window))
        {
            return null;
        }
        if (window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        double usedPercent = Math.Clamp(GetDouble(window, "used_percent", "usedPercent") ?? 0, 0, 100);
        double? durationSeconds = GetDouble(window, "limit_window_seconds", "limitWindowSeconds");
        TimeSpan? duration = durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null;
        DateTimeOffset? resetsAt = ParseTimestamp(window, "reset_at", "resetAt");
        if (resetsAt is null && GetDouble(window, "reset_after_seconds", "resetAfterSeconds") is double resetAfter)
        {
            resetsAt = now.AddSeconds(Math.Max(0, resetAfter));
        }

        return new UsageWindow(label, usedPercent, duration, resetsAt);
    }

    private static UsageWindow? PickByDuration(
        UsageWindow? first,
        UsageWindow? second,
        TimeSpan expected)
    {
        foreach (UsageWindow? window in new[] { first, second })
        {
            if (window?.Duration is TimeSpan duration
                && Math.Abs((duration - expected).TotalMinutes) < 1)
            {
                return window;
            }
        }
        return null;
    }

    private static ResetCreditsInfo? ParseEmbeddedResetCredits(JsonElement root)
    {
        if (!TryGetProperty(root, "rate_limit_reset_credits", out JsonElement credits)
            && !TryGetProperty(root, "rateLimitResetCredits", out credits))
        {
            return null;
        }
        return ParseResetCredits(credits);
    }

    private static ResetCreditsInfo? ParseResetCredits(JsonElement root)
    {
        double? rawCount = GetDouble(root, "available_count", "availableCount");
        if (rawCount is null || rawCount < 0)
        {
            return null;
        }

        List<DateTimeOffset> expirations = [];
        if (TryGetProperty(root, "credits", out JsonElement credits)
            && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement credit in credits.EnumerateArray())
            {
                string status = GetString(credit, "status");
                if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                DateTimeOffset? expiresAt = ParseTimestamp(credit, "expires_at", "expiresAt");
                if (expiresAt is not null && expiresAt > DateTimeOffset.Now)
                {
                    expirations.Add(expiresAt.Value);
                }
            }
        }

        expirations.Sort();
        return new ResetCreditsInfo((int)Math.Floor(rawCount.Value), expirations);
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
            {
                try
                {
                    return numeric > 20_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                        : DateTimeOffset.FromUnixTimeSeconds(numeric);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static JsonElement GetRequiredObject(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        throw new ProviderException("Codex did not return the expected usage limits.");
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }
        return null;
    }

    private static string FormatPlanLabel(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Codex plan" : $"{Humanize(value)} plan";
    }

    private static string Humanize(string value)
    {
        TextInfo textInfo = CultureInfo.GetCultureInfo("en-US").TextInfo;
        return textInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant());
    }

    private sealed record CodexCredentials(string AccessToken, string AccountId);

    private sealed class ProviderException(string userMessage) : Exception(userMessage)
    {
        public string UserMessage { get; } = userMessage;
    }
}
