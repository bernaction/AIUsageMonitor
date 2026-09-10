using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class ClaudeUsageProvider : IAiUsageProvider
{
    private readonly ClaudeSessionKeyStore _sessionKeyStore;
    private readonly ClaudeWebUsageClient _webUsageClient;

    public ClaudeUsageProvider(
        ClaudeSessionKeyStore sessionKeyStore,
        ClaudeWebUsageClient webUsageClient)
    {
        _sessionKeyStore = sessionKeyStore;
        _webUsageClient = webUsageClient;
    }

    public string ProviderId => "claude";

    public bool HasWebSession => _sessionKeyStore.IsConfigured;

    public async Task<AiUsageSnapshot> ConnectWebSessionAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        string sessionKey = NormalizeSessionKey(value);
        using ClaudeWebUsageResult result = await _webUsageClient.GetUsageAsync(
            sessionKey,
            replaceBrowserSession: true,
            cancellationToken);
        AiUsageSnapshot snapshot = ParseUsage(
            result.Document.RootElement,
            DateTimeOffset.Now,
            result.PlanCode,
            result.RateLimitTier);
        _sessionKeyStore.Save(sessionKey);
        return snapshot;
    }

    public void DisconnectWebSession() => _sessionKeyStore.Delete();

    public async Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string? webSessionKey = _sessionKeyStore.Read();
            if (string.IsNullOrWhiteSpace(webSessionKey))
            {
                return AiUsageSnapshot.Unavailable(
                    ProviderId,
                    "Connect Claude Web in Settings by pasting your sessionKey.",
                    ProviderIssueKind.AuthenticationRequired);
            }

            using ClaudeWebUsageResult webResult = await _webUsageClient.GetUsageAsync(
                webSessionKey,
                replaceBrowserSession: false,
                cancellationToken);
            return ParseUsage(
                webResult.Document.RootElement,
                DateTimeOffset.Now,
                webResult.PlanCode,
                webResult.RateLimitTier);
        }
        catch (ClaudeWebException exception) when (exception.IsChallenge)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "Claude Web presented a temporary browser verification challenge.");
        }
        catch (ClaudeWebException exception) when (exception.StatusCode is 401 or 403)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "The Claude Web session has expired. Paste a new sessionKey.",
                ProviderIssueKind.AuthenticationRequired);
        }
        catch (ClaudeWebException exception) when (exception.StatusCode == 429)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "Claude Web temporarily rate-limited the requests.",
                ProviderIssueKind.RateLimited);
        }
        catch (ClaudeWebException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.Message);
        }
        catch (ProviderException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.UserMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Permission to access the Claude Web session was denied.");
        }
        catch (JsonException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Claude data format has changed.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Claude request timed out.");
        }
        catch (HttpRequestException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Claude could not be reached.");
        }
        catch (IOException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Claude Web session could not be accessed.");
        }
        catch (Win32Exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The saved Claude Web session could not be read.");
        }
    }

    public void Dispose()
    {
    }

    private static string NormalizeSessionKey(string value)
    {
        string sessionKey = value.Trim();
        if (sessionKey.StartsWith("sessionKey=", StringComparison.OrdinalIgnoreCase))
        {
            sessionKey = sessionKey["sessionKey=".Length..];
        }

        if (!sessionKey.StartsWith("sk-ant-", StringComparison.Ordinal)
            || sessionKey.Length <= "sk-ant-".Length
            || sessionKey.Any(character => char.IsWhiteSpace(character) || character == ';'))
        {
            throw new ArgumentException(
                "Paste only the sessionKey value beginning with sk-ant-.",
                nameof(value));
        }

        return sessionKey;
    }

    private static AiUsageSnapshot ParseUsage(
        JsonElement root,
        DateTimeOffset now,
        string? organizationPlanCode = null,
        string? organizationRateLimitTier = null)
    {
        UsageWindow? session = ParseWindow(root, "five_hour", "fiveHour", "Session", now);
        UsageWindow? weekly = ParseWindow(root, "seven_day", "sevenDay", "Weekly", now);
        if (session is null && weekly is null)
        {
            throw new ProviderException("Claude did not return any usage windows.");
        }

        string plan = organizationPlanCode
            ?? GetString(root, "plan", "plan_type", "subscription_type", "tier");
        string rateLimitTier = organizationRateLimitTier
            ?? GetString(root, "rate_limit_tier", "rateLimitTier");
        string planLabel = FormatPlanLabel(plan, rateLimitTier);
        return new AiUsageSnapshot("claude", planLabel, session, weekly, null, null, now, true, null);
    }

    private static string FormatPlanLabel(string plan, string rateLimitTier)
    {
        string normalizedTier = rateLimitTier.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        if (string.Equals(plan, "max", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedTier.Contains("20x", StringComparison.Ordinal))
            {
                return "Max 20x Plan";
            }
            if (normalizedTier.Contains("5x", StringComparison.Ordinal))
            {
                return "Max 5x Plan";
            }
        }

        return string.IsNullOrWhiteSpace(plan) ? "Claude Plan" : $"{Humanize(plan)} Plan";
    }

    private static UsageWindow? ParseWindow(
        JsonElement root,
        string primaryName,
        string alternateName,
        string label,
        DateTimeOffset now)
    {
        if (!TryGetProperty(root, primaryName, out JsonElement window)
            && !TryGetProperty(root, alternateName, out window))
        {
            return null;
        }

        double? utilization = GetDouble(window, "utilization", "used_percent", "usedPercent");
        DateTimeOffset? resetsAt = ParseTimestamp(window, "resets_at", "reset_at", "resetsAt", "resetAt");
        return new UsageWindow(label, Math.Clamp(utilization ?? 0, 0, 100), null, resetsAt);
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
            {
                return numeric > 20_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            }
        }
        return null;
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
            if (TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
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

    private static string Humanize(string value)
    {
        TextInfo textInfo = CultureInfo.GetCultureInfo("en-US").TextInfo;
        return textInfo.ToTitleCase(value.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant());
    }

    private sealed class ProviderException(string userMessage) : Exception(userMessage)
    {
        public string UserMessage { get; } = userMessage;
    }
}
