using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class ClaudeUsageProvider : IAiUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ClaudeUsageProvider(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string ProviderId => "claude";

    public async Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string accessToken = await ReadAccessTokenAsync(cancellationToken);
            using JsonDocument document = await GetUsageDocumentAsync(accessToken, cancellationToken);
            return ParseUsage(document.RootElement, DateTimeOffset.Now);
        }
        catch (ProviderException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.UserMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Permission to read the local Claude session was denied.");
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
            return AiUsageSnapshot.Unavailable(ProviderId, "The local Claude session could not be read.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static string ResolveCredentialsPath()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string claudeHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configuredHome;
        return Path.Combine(claudeHome, ".credentials.json");
    }

    private static async Task<string> ReadAccessTokenAsync(CancellationToken cancellationToken)
    {
        string credentialsPath = ResolveCredentialsPath();
        if (!File.Exists(credentialsPath))
        {
            throw new ProviderException("Sign in to Claude Code to enable monitoring.");
        }

        await using FileStream stream = new(
            credentialsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;
        foreach (string containerName in new[] { "claudeAiOauth", "oauth", "credentials" })
        {
            if (TryGetProperty(root, containerName, out JsonElement container))
            {
                string token = GetString(container, "accessToken", "access_token");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }

        string rootToken = GetString(root, "accessToken", "access_token");
        if (!string.IsNullOrWhiteSpace(rootToken))
        {
            return rootToken;
        }

        throw new ProviderException("The local Claude session does not contain a valid token.");
    }

    private async Task<JsonDocument> GetUsageDocumentAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.UserAgent.ParseAdd("AIUsageMonitor/0.1");

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderException("The Claude session has expired. Sign in again.");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderException("Claude temporarily rate-limited the requests.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException("The Claude usage service is unavailable.");
        }

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    }

    private static AiUsageSnapshot ParseUsage(JsonElement root, DateTimeOffset now)
    {
        UsageWindow? session = ParseWindow(root, "five_hour", "fiveHour", "Session", now);
        UsageWindow? weekly = ParseWindow(root, "seven_day", "sevenDay", "Weekly", now);
        if (session is null && weekly is null)
        {
            throw new ProviderException("Claude did not return any usage windows.");
        }

        string plan = GetString(root, "plan", "plan_type", "subscription_type", "tier");
        string planLabel = string.IsNullOrWhiteSpace(plan) ? "Claude plan" : $"{Humanize(plan)} plan";
        return new AiUsageSnapshot("claude", planLabel, session, weekly, null, null, now, true, null);
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
