using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class AntigravityUsageProvider : IAiUsageProvider
{
    private const string ServicePath = "exa.language_server_pb.LanguageServerService";
    private const string ProcessDiscoveryScript = "$items = Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'language_server*' -or $_.Name -like 'language-server*' -or $_.Name -like 'agy*' -or $_.Name -like 'antigravity*' }; foreach ($item in $items) { $value = \"$($item.ProcessId)`t$($item.CommandLine)\"; [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value)) }";
    private static readonly string MetadataBody = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AntigravityUsageProvider(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            return;
        }

        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public string ProviderId => "antigravity";

    public async Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<RpcEndpoint> endpoints = await DiscoverEndpointsAsync(cancellationToken);
            foreach (RpcEndpoint endpoint in endpoints)
            {
                AiUsageSnapshot? grouped = await TryReadGroupedQuotaAsync(endpoint, cancellationToken);
                if (grouped is not null)
                {
                    return grouped;
                }
            }
            foreach (RpcEndpoint endpoint in endpoints)
            {
                AiUsageSnapshot? legacy = await TryReadLegacyQuotaAsync(endpoint, cancellationToken);
                if (legacy is not null)
                {
                    return legacy;
                }
            }
            return AiUsageSnapshot.Unavailable(ProviderId, "Antigravity did not return usable quota windows.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AntigravityException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.Message, exception.IssueKind);
        }
        catch (TimeoutException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Antigravity local service timed out.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "Open Antigravity IDE to display quota limits.",
                ProviderIssueKind.NotDetected);
        }
        catch (HttpRequestException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Antigravity local service could not be reached.");
        }
        catch (JsonException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "The Antigravity local data format has changed.");
        }
    }

    internal async Task<IReadOnlyList<LocalTokenEntry>> GetTodayTokenEntriesAsync(
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RpcEndpoint> endpoints;
        try
        {
            endpoints = await DiscoverEndpointsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is AntigravityException
            or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            return [];
        }

        foreach (RpcEndpoint endpoint in endpoints)
        {
            try
            {
                using JsonDocument trajectories = await PostRpcAsync(endpoint, "GetAllCascadeTrajectories", "{}", cancellationToken);
                return await ReadTokenEntriesAsync(endpoint, trajectories.RootElement, periodStart, cancellationToken);
            }
            catch (Exception exception) when (exception is AntigravityException
                or HttpRequestException
                or JsonException
                or TaskCanceledException)
            {
            }
        }
        return [];
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<AiUsageSnapshot?> TryReadGroupedQuotaAsync(
        RpcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await PostRpcAsync(
                endpoint,
                "RetrieveUserQuotaSummary",
                "{\"forceRefresh\":true}",
                cancellationToken);
            List<QuotaWindow> windows = ParseGroupedWindows(document.RootElement);
            if (windows.Count == 0)
            {
                return null;
            }

            string plan = await TryReadPlanAsync(endpoint, cancellationToken) ?? "Antigravity";
            return CreateSnapshot(plan, windows, endpoint.SourceDetail);
        }
        catch (AntigravityException exception) when (exception.IssueKind != ProviderIssueKind.AuthenticationRequired)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<AiUsageSnapshot?> TryReadLegacyQuotaAsync(
        RpcEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await PostRpcAsync(endpoint, "GetUserStatus", MetadataBody, cancellationToken);
            JsonElement userStatus = GetObject(document.RootElement, "userStatus");
            List<QuotaWindow> windows = ParseLegacyWindows(userStatus);
            if (windows.Count == 0)
            {
                using JsonDocument fallback = await PostRpcAsync(
                    endpoint,
                    "GetCommandModelConfigs",
                    MetadataBody,
                    cancellationToken);
                windows = ParseLegacyConfigWindows(GetArray(fallback.RootElement, "clientModelConfigs"));
            }
            if (windows.Count == 0)
            {
                return null;
            }
            return CreateSnapshot(ReadPlan(userStatus) ?? "Antigravity", windows, endpoint.SourceDetail);
        }
        catch (AntigravityException exception) when (exception.IssueKind != ProviderIssueKind.AuthenticationRequired)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> TryReadPlanAsync(RpcEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await PostRpcAsync(endpoint, "GetUserStatus", MetadataBody, cancellationToken);
            return ReadPlan(GetObject(document.RootElement, "userStatus"));
        }
        catch (Exception exception) when (exception is AntigravityException
            or HttpRequestException
            or JsonException
            or TaskCanceledException)
        {
            return null;
        }
    }

    private static AiUsageSnapshot CreateSnapshot(
        string plan,
        IReadOnlyList<QuotaWindow> windows,
        string sourceDetail)
    {
        QuotaWindow? session = windows
            .Where(window => window.Kind == "session")
            .OrderByDescending(window => window.UsedPercent)
            .FirstOrDefault();
        QuotaWindow? weekly = windows
            .Where(window => window.Kind == "weekly")
            .OrderByDescending(window => window.UsedPercent)
            .FirstOrDefault();
        if (session is null && weekly is null)
        {
            weekly = windows.OrderByDescending(window => window.UsedPercent).FirstOrDefault();
        }

        return new AiUsageSnapshot(
            "antigravity",
            FormatPlan(plan, sourceDetail),
            session is null ? null : new UsageWindow(session.Label, session.UsedPercent, TimeSpan.FromHours(5), session.ResetsAt),
            weekly is null ? null : new UsageWindow(weekly.Label, weekly.UsedPercent, TimeSpan.FromDays(7), weekly.ResetsAt),
            null,
            null,
            DateTimeOffset.Now,
            true,
            null);
    }

    private async Task<IReadOnlyList<LocalTokenEntry>> ReadTokenEntriesAsync(
        RpcEndpoint endpoint,
        JsonElement root,
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        List<TrajectorySummary> summaries = ParseTrajectorySummaries(root)
            .Where(summary => summary.LastModified is null || summary.LastModified >= periodStart)
            .OrderByDescending(summary => summary.LastModified)
            .Take(250)
            .ToList();
        List<LocalTokenEntry> entries = [];
        foreach (TrajectorySummary summary in summaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string body = JsonSerializer.Serialize(new { cascadeId = summary.Id });
                using JsonDocument metadata = await PostRpcAsync(
                    endpoint,
                    "GetCascadeTrajectoryGeneratorMetadata",
                    body,
                    cancellationToken);
                Dictionary<string, DateTimeOffset> timestamps = await TryReadTrajectoryTimestampsAsync(
                    endpoint,
                    summary.Id,
                    cancellationToken);
                CollectMetadataEntries(
                    metadata.RootElement,
                    summary,
                    timestamps,
                    periodStart,
                    entries);
            }
            catch (Exception exception) when (exception is AntigravityException
                or HttpRequestException
                or JsonException
                or TaskCanceledException)
            {
            }
        }
        return entries;
    }

    private async Task<Dictionary<string, DateTimeOffset>> TryReadTrajectoryTimestampsAsync(
        RpcEndpoint endpoint,
        string cascadeId,
        CancellationToken cancellationToken)
    {
        Dictionary<string, DateTimeOffset> result = new(StringComparer.Ordinal);
        try
        {
            string body = JsonSerializer.Serialize(new { cascadeId });
            using JsonDocument document = await PostRpcAsync(endpoint, "GetCascadeTrajectory", body, cancellationToken);
            JsonElement trajectory = GetObject(document.RootElement, "trajectory");
            foreach (JsonElement step in GetArray(trajectory, "steps"))
            {
                JsonElement metadata = GetObject(step, "metadata");
                JsonElement usage = GetObject(metadata, "modelUsage");
                DateTimeOffset? timestamp = ParseTimestamp(
                    metadata,
                    "createdAt",
                    "startedAt",
                    "completedAt",
                    "finishedGeneratingAt",
                    "viewableAt");
                if (timestamp is null)
                {
                    continue;
                }
                AddTimestamp(result, "response", GetString(usage, "responseId"), timestamp.Value);
                AddTimestamp(result, "message", GetString(usage, "messageId"), timestamp.Value);
            }
        }
        catch (Exception exception) when (exception is AntigravityException
            or HttpRequestException
            or JsonException
            or TaskCanceledException)
        {
        }
        return result;
    }

    private static void CollectMetadataEntries(
        JsonElement root,
        TrajectorySummary summary,
        IReadOnlyDictionary<string, DateTimeOffset> timestamps,
        DateTimeOffset periodStart,
        ICollection<LocalTokenEntry> entries)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        int fallbackIndex = 0;
        foreach (JsonElement row in GetArray(root, "generatorMetadata"))
        {
            JsonElement chat = GetObject(row, "chatModel");
            if (chat.ValueKind != JsonValueKind.Object)
            {
                chat = row;
            }
            string model = FirstNonEmpty(
                GetString(chat, "responseModel"),
                GetString(chat, "model"),
                GetString(chat, "modelDisplayName"),
                "auto");
            DateTimeOffset? chatStarted = ParseTimestamp(GetObject(chat, "chatStartMetadata"), "createdAt");

            List<JsonElement> usageItems = [];
            JsonElement directUsage = GetObject(chat, "usage");
            if (directUsage.ValueKind == JsonValueKind.Object)
            {
                usageItems.Add(directUsage);
            }
            foreach (JsonElement retry in GetArray(chat, "retryInfos"))
            {
                JsonElement usage = GetObject(retry, "usage");
                usageItems.Add(usage.ValueKind == JsonValueKind.Object ? usage : retry);
            }

            foreach (JsonElement usage in usageItems)
            {
                long input = GetInt64(usage, "inputTokens");
                long output = GetInt64(usage, "outputTokens");
                long cacheRead = GetInt64(usage, "cacheReadTokens");
                long reasoning = GetInt64(usage, "thinkingOutputTokens");
                long responseOutput = GetInt64(usage, "responseOutputTokens");
                if (output == 0)
                {
                    output = reasoning + responseOutput;
                }
                if (input + output + cacheRead + reasoning <= 0)
                {
                    continue;
                }

                string responseId = GetString(usage, "responseId");
                string messageId = GetString(usage, "messageId");
                string identity = !string.IsNullOrWhiteSpace(responseId)
                    ? $"response:{responseId}"
                    : !string.IsNullOrWhiteSpace(messageId)
                        ? $"message:{messageId}"
                        : $"fallback:{fallbackIndex++}";
                if (!seen.Add(identity))
                {
                    continue;
                }

                DateTimeOffset timestamp = ParseTimestamp(usage, "createdAt", "timestamp")
                    ?? FindTimestamp(timestamps, responseId, messageId)
                    ?? chatStarted
                    ?? summary.LastModified
                    ?? DateTimeOffset.MinValue;
                if (timestamp < periodStart)
                {
                    continue;
                }
                entries.Add(new LocalTokenEntry(
                    "antigravity",
                    model,
                    Math.Max(0, input),
                    Math.Max(0, output),
                    Math.Max(0, cacheRead),
                    0));
            }
        }
    }

    private async Task<JsonDocument> PostRpcAsync(
        RpcEndpoint endpoint,
        string method,
        string body,
        CancellationToken cancellationToken)
    {
        Uri uri = new(endpoint.BaseUri, $"/{ServicePath}/{method}");
        if (!uri.IsLoopback)
        {
            throw new AntigravityException("Antigravity endpoint was not local.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, uri);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("connect-protocol-version", "1");
        request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", endpoint.CsrfToken);
        request.Headers.UserAgent.ParseAdd("AIUsageMonitor/0.1");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AntigravityException(
                "Antigravity requires a signed-in IDE session.",
                ProviderIssueKind.AuthenticationRequired);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new AntigravityException($"Antigravity local service returned {(int)response.StatusCode}.");
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task<IReadOnlyList<RpcEndpoint>> DiscoverEndpointsAsync(CancellationToken cancellationToken)
    {
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessResult result = await ProcessRunner.RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", ProcessDiscoveryScript],
            TimeSpan.FromSeconds(8),
            cancellationToken: cancellationToken);
        List<ProcessInfo> processes = [];
        foreach (string encoded in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string line = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
                int separator = line.IndexOf('\t');
                if (separator <= 0 || !int.TryParse(line[..separator], out int processId))
                {
                    continue;
                }
                string commandLine = line[(separator + 1)..];
                if (!IsAntigravityLanguageServer(commandLine))
                {
                    continue;
                }
                string csrf = ExtractFlag(commandLine, "--csrf_token");
                string extensionCsrf = ExtractFlag(commandLine, "--extension_server_csrf_token");
                int? extensionPort = ExtractPort(commandLine, "--extension_server_port");
                if (string.IsNullOrWhiteSpace(csrf) && string.IsNullOrWhiteSpace(extensionCsrf))
                {
                    continue;
                }
                processes.Add(new ProcessInfo(processId, csrf, extensionPort, extensionCsrf));
            }
            catch (FormatException)
            {
            }
        }
        if (processes.Count == 0)
        {
            throw new AntigravityException(
                "Open Antigravity IDE to display local usage.",
                ProviderIssueKind.NotDetected);
        }

        List<RpcEndpoint> endpoints = [];
        foreach (ProcessInfo process in processes)
        {
            if (process.ExtensionPort is int extensionPort)
            {
                endpoints.Add(new RpcEndpoint(
                    new Uri($"http://127.0.0.1:{extensionPort}"),
                    FirstNonEmpty(process.ExtensionCsrfToken, process.CsrfToken),
                    "IDE"));
            }
            foreach (int port in await ReadListeningPortsAsync(powershell, process.ProcessId, cancellationToken))
            {
                endpoints.Add(new RpcEndpoint(new Uri($"https://127.0.0.1:{port}"), process.CsrfToken, "IDE"));
                endpoints.Add(new RpcEndpoint(new Uri($"http://127.0.0.1:{port}"), process.CsrfToken, "IDE"));
            }
        }
        return endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.CsrfToken))
            .DistinctBy(endpoint => (endpoint.BaseUri, endpoint.CsrfToken))
            .ToArray();
    }

    private static async Task<IReadOnlyList<int>> ReadListeningPortsAsync(
        string powershell,
        int processId,
        CancellationToken cancellationToken)
    {
        string script = $"Get-NetTCPConnection -OwningProcess {processId} -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort";
        ProcessResult result = await ProcessRunner.RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", script],
            TimeSpan.FromSeconds(5),
            cancellationToken: cancellationToken);
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value.Trim(), out int port) ? port : 0)
            .Where(port => port is > 0 and < 65536)
            .Distinct()
            .ToArray();
    }

    private static List<QuotaWindow> ParseGroupedWindows(JsonElement root)
    {
        JsonElement summary = GetObject(root, "response");
        if (summary.ValueKind != JsonValueKind.Object)
        {
            summary = GetObject(root, "summary");
        }
        if (summary.ValueKind != JsonValueKind.Object)
        {
            summary = root;
        }

        List<QuotaWindow> windows = [];
        foreach (JsonElement group in GetArray(summary, "groups"))
        {
            string groupName = NormalizeGroupName(GetString(group, "displayName"));
            foreach (JsonElement bucket in GetArray(group, "buckets"))
            {
                string? kind = ParseBucketKind(bucket);
                double? remaining = GetRemainingFraction(bucket);
                if (kind is null || remaining is null || GetBoolean(bucket, "disabled"))
                {
                    continue;
                }
                windows.Add(new QuotaWindow(
                    $"{groupName} {(kind == "session" ? "5-hour" : "weekly")}",
                    kind,
                    Math.Clamp((1 - remaining.Value) * 100, 0, 100),
                    ParseTimestamp(bucket, "resetTime")));
            }
        }
        return windows;
    }

    private static List<QuotaWindow> ParseLegacyWindows(JsonElement userStatus)
    {
        JsonElement data = GetObject(userStatus, "cascadeModelConfigData");
        return ParseLegacyConfigWindows(GetArray(data, "clientModelConfigs"));
    }

    private static List<QuotaWindow> ParseLegacyConfigWindows(IEnumerable<JsonElement> configs)
    {
        Dictionary<string, QuotaWindow> pools = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement config in configs)
        {
            JsonElement quota = GetObject(config, "quotaInfo");
            double? remaining = GetDouble(quota, "remainingFraction");
            if (remaining is null)
            {
                continue;
            }
            string label = FirstNonEmpty(GetString(config, "label"), GetNestedString(config, "modelOrAlias", "model"));
            string pool = NormalizePoolName(label);
            QuotaWindow candidate = new(
                pool,
                "weekly",
                Math.Clamp((1 - remaining.Value) * 100, 0, 100),
                ParseTimestamp(quota, "resetTime"));
            if (!pools.TryGetValue(pool, out QuotaWindow? existing) || candidate.UsedPercent > existing.UsedPercent)
            {
                pools[pool] = candidate;
            }
        }
        return pools.Values.ToList();
    }

    private static List<TrajectorySummary> ParseTrajectorySummaries(JsonElement root)
    {
        List<(JsonElement Item, string MapKey)> items = [];
        if (root.TryGetProperty("trajectorySummaries", out JsonElement summaries))
        {
            if (summaries.ValueKind == JsonValueKind.Array)
            {
                items.AddRange(summaries.EnumerateArray().Select(item => (item, string.Empty)));
            }
            else if (summaries.ValueKind == JsonValueKind.Object)
            {
                items.AddRange(summaries.EnumerateObject().Select(property => (property.Value, property.Name)));
            }
        }
        else
        {
            items.AddRange(GetArray(root, "cascadeTrajectories").Select(item => (item, string.Empty)));
        }

        List<TrajectorySummary> result = [];
        foreach ((JsonElement item, string mapKey) in items)
        {
            string id = FirstNonEmpty(
                mapKey,
                GetString(item, "cascadeId"),
                GetString(item, "trajectoryId"),
                GetString(item, "id"),
                GetString(item, "sessionId"));
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(new TrajectorySummary(
                    id,
                    ParseTimestamp(item, "lastModifiedTime", "lastModified", "updatedAt", "modifiedAt")));
            }
        }
        return result;
    }

    private static string? ReadPlan(JsonElement userStatus)
    {
        return FirstNonEmptyOrNull(
            GetNestedString(userStatus, "userTier", "name"),
            GetNestedString(GetObject(userStatus, "planStatus"), "planInfo", "planDisplayName"),
            GetNestedString(GetObject(userStatus, "planStatus"), "planInfo", "planName"));
    }

    private static string FormatPlan(string value, string sourceDetail)
    {
        string plan = Regex.Replace(value, "^(Google\\s+AI|Antigravity)\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        string suffix = sourceDetail.Equals("IDE", StringComparison.OrdinalIgnoreCase) ? " · IDE" : string.Empty;
        return $"{(string.IsNullOrWhiteSpace(plan) ? "Plan" : plan)}{suffix}";
    }

    private static bool IsAntigravityLanguageServer(string commandLine)
    {
        string lower = commandLine.ToLowerInvariant();
        bool languageServer = lower.Contains("language_server", StringComparison.Ordinal)
            || lower.Contains("language-server", StringComparison.Ordinal);
        return languageServer
            && (lower.Contains("antigravity", StringComparison.Ordinal)
                || lower.Contains("--app_data_dir antigravity", StringComparison.Ordinal)
                || lower.Contains("--app_data_dir=antigravity", StringComparison.Ordinal));
    }

    private static string ExtractFlag(string commandLine, string flag)
    {
        Match match = Regex.Match(
            commandLine,
            $"{Regex.Escape(flag)}(?:=|\\s+)(?:\"([^\"]+)\"|(\\S+))",
            RegexOptions.IgnoreCase);
        return match.Success ? FirstNonEmpty(match.Groups[1].Value, match.Groups[2].Value) : string.Empty;
    }

    private static int? ExtractPort(string commandLine, string flag)
    {
        return int.TryParse(ExtractFlag(commandLine, flag), out int port) && port is > 0 and < 65536 ? port : null;
    }

    private static string? ParseBucketKind(JsonElement bucket)
    {
        foreach (string value in new[]
        {
            GetString(bucket, "window"),
            GetString(bucket, "bucketId"),
            GetString(bucket, "displayName")
        })
        {
            string normalized = value.Replace('_', '-').ToLowerInvariant();
            if (normalized.Contains("weekly", StringComparison.Ordinal))
            {
                return "weekly";
            }
            if (normalized.Contains("session", StringComparison.Ordinal)
                || normalized.Contains("5h", StringComparison.Ordinal)
                || normalized.Contains("5-hour", StringComparison.Ordinal)
                || normalized.Contains("five-hour", StringComparison.Ordinal))
            {
                return "session";
            }
        }
        return null;
    }

    private static double? GetRemainingFraction(JsonElement bucket)
    {
        double? direct = GetDouble(bucket, "remainingFraction");
        if (direct is not null)
        {
            return direct;
        }
        JsonElement remaining = GetObject(bucket, "remaining");
        return GetDouble(remaining, "remainingFraction") ?? GetDouble(remaining, "value");
    }

    private static string NormalizeGroupName(string value)
    {
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini";
        }
        if (value.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gpt", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude/GPT";
        }
        return string.IsNullOrWhiteSpace(value) ? "Quota" : value;
    }

    private static string NormalizePoolName(string value)
    {
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            && value.Contains("pro", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini Pro";
        }
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            && value.Contains("flash", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini Flash";
        }
        return "Claude/GPT";
    }

    private static DateTimeOffset? FindTimestamp(
        IReadOnlyDictionary<string, DateTimeOffset> timestamps,
        string responseId,
        string messageId)
    {
        if (!string.IsNullOrWhiteSpace(responseId)
            && timestamps.TryGetValue($"response:{responseId}", out DateTimeOffset responseTimestamp))
        {
            return responseTimestamp;
        }
        if (!string.IsNullOrWhiteSpace(messageId)
            && timestamps.TryGetValue($"message:{messageId}", out DateTimeOffset messageTimestamp))
        {
            return messageTimestamp;
        }
        return null;
    }

    private static void AddTimestamp(
        IDictionary<string, DateTimeOffset> timestamps,
        string prefix,
        string id,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        string key = $"{prefix}:{id}";
        if (!timestamps.TryGetValue(key, out DateTimeOffset existing) || timestamp < existing)
        {
            timestamps[key] = timestamp;
        }
    }

    private static JsonElement GetObject(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
    }

    private static IEnumerable<JsonElement> GetArray(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
    }

    private static string GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return GetString(GetObject(element, objectName), propertyName);
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result))
        {
            return Math.Max(0, result);
        }
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? Math.Max(0, result)
                : 0;
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result))
        {
            return result;
        }
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                ? result
                : null;
    }

    private static bool GetBoolean(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double numeric))
            {
                try
                {
                    return Math.Abs(numeric) < 20_000_000_000
                        ? DateTimeOffset.FromUnixTimeSeconds((long)numeric)
                        : DateTimeOffset.FromUnixTimeMilliseconds((long)numeric);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }
        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string? FirstNonEmptyOrNull(params string?[] values)
    {
        string result = FirstNonEmpty(values);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private sealed record RpcEndpoint(Uri BaseUri, string CsrfToken, string SourceDetail);

    private sealed record ProcessInfo(
        int ProcessId,
        string CsrfToken,
        int? ExtensionPort,
        string ExtensionCsrfToken);

    private sealed record QuotaWindow(
        string Label,
        string Kind,
        double UsedPercent,
        DateTimeOffset? ResetsAt);

    private sealed record TrajectorySummary(string Id, DateTimeOffset? LastModified);

    private sealed class AntigravityException(
        string message,
        ProviderIssueKind issueKind = ProviderIssueKind.TemporaryFailure) : Exception(message)
    {
        public ProviderIssueKind IssueKind { get; } = issueKind;
    }
}
