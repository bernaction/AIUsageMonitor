using System.Globalization;
using System.IO;
using System.Text.Json;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class LocalTokenUsageService
{
    private readonly AntigravityUsageProvider _antigravityUsageProvider;

    private static readonly IReadOnlyDictionary<string, ModelPricing> ExactPricing =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-6-astra"] = new(10m, 50m, 1m, 12.5m),
            ["gpt-5.6-sol"] = new(4m, 20m, 0.4m, 5m),
            ["gpt-5.6-terra"] = new(2m, 12m, 0.2m, 2.5m),
            ["gpt-5.6-luna"] = new(0.1m, 0.6m, 0.01m, 0.125m),
            ["gpt-reserve"] = new(0.1m, 0.6m, 0.01m, 0.125m),
            ["gpt-5.5"] = new(5m, 30m, 0.5m, 6.25m),
            ["gpt-5.4"] = new(2.5m, 15m, 0.25m, 3.125m),
            ["gpt-5.2-codex"] = new(1.75m, 14m, 0.175m, 2.1875m),
            ["gpt-5.1"] = new(1.25m, 10m, 0.125m, 1.5625m),
            ["gpt-5"] = new(1.25m, 10m, 0.125m, 1.5625m),
            ["claude-sonnet-5"] = new(2m, 10m, 0.2m, 2.5m),
            ["claude-sonnet-4-6"] = new(3m, 15m, 0.3m, 3.75m),
            ["claude-sonnet-4-5"] = new(3m, 15m, 0.3m, 3.75m),
            ["claude-sonnet-4"] = new(3m, 15m, 0.3m, 3.75m),
            ["claude-3-7-sonnet"] = new(3m, 15m, 0.3m, 3.75m),
            ["claude-3-5-sonnet"] = new(3m, 15m, 0.3m, 3.75m),
            ["claude-3-5-haiku"] = new(0.8m, 4m, 0.08m, 1m)
        };

    public LocalTokenUsageService(AntigravityUsageProvider antigravityUsageProvider)
    {
        _antigravityUsageProvider = antigravityUsageProvider;
    }

    public async Task<TokenUsageSnapshot> GetTodayUsageAsync(
        IReadOnlySet<string>? selectedProviderIds = null,
        CancellationToken cancellationToken = default)
    {
        DateTime localToday = DateTime.Today;
        DateTimeOffset periodStart = new(localToday, TimeZoneInfo.Local.GetUtcOffset(localToday));
        DateTime periodStartUtc = periodStart.UtcDateTime;
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Task<(ProviderAccumulator Codex, ProviderAccumulator Claude, ProviderAccumulator Kiro)> fileTask = Task.Run(() =>
        {
            ProviderAccumulator codex = IsSelected("codex", selectedProviderIds)
                ? CollectCodexUsage(
                    ResolveCodexSessionsPath(userProfile),
                    periodStart,
                    periodStartUtc,
                    cancellationToken)
                : new ProviderAccumulator();
            ProviderAccumulator claude = IsSelected("claude", selectedProviderIds)
                ? CollectClaudeUsage(
                    Path.Combine(userProfile, ".claude", "projects"),
                    periodStart,
                    periodStartUtc,
                    cancellationToken)
                : new ProviderAccumulator();
            ProviderAccumulator kiro = IsSelected("kiro", selectedProviderIds)
                ? CollectEntries(KiroUsageProvider.CollectTodayTokenEntries(userProfile, periodStart, cancellationToken))
                : new ProviderAccumulator();
            return (codex, claude, kiro);
        }, cancellationToken);
        Task<IReadOnlyList<LocalTokenEntry>> antigravityTask = IsSelected("antigravity", selectedProviderIds)
            ? _antigravityUsageProvider.GetTodayTokenEntriesAsync(periodStart, cancellationToken)
            : Task.FromResult<IReadOnlyList<LocalTokenEntry>>([]);

        (ProviderAccumulator codex, ProviderAccumulator claude, ProviderAccumulator kiro) = await fileTask;
        ProviderAccumulator antigravity = CollectEntries(await antigravityTask);

        ProviderTokenUsage[] providers =
        [
            codex.ToSnapshot("codex"),
            claude.ToSnapshot("claude"),
            kiro.ToSnapshot("kiro"),
            antigravity.ToSnapshot("antigravity")
        ];

        return new TokenUsageSnapshot(
            providers.Sum(provider => provider.TotalTokens),
            providers.Sum(provider => provider.EstimatedCostUsd),
            providers.Sum(provider => provider.UnpricedTokens),
            providers,
            DateTimeOffset.Now);
    }

    private static bool IsSelected(string providerId, IReadOnlySet<string>? selectedProviderIds)
    {
        return selectedProviderIds is null || selectedProviderIds.Contains(providerId);
    }

    private static ProviderAccumulator CollectEntries(IEnumerable<LocalTokenEntry> entries)
    {
        ProviderAccumulator accumulator = new();
        foreach (LocalTokenEntry entry in entries)
        {
            accumulator.Add(entry);
        }
        return accumulator;
    }

    private static ProviderAccumulator CollectCodexUsage(
        string root,
        DateTimeOffset periodStart,
        DateTime periodStartUtc,
        CancellationToken cancellationToken)
    {
        ProviderAccumulator total = new();
        foreach (string file in EnumerateRecentJsonLines(root, periodStartUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<UsageEntry> currentFormatEntries = [];
            List<UsageEntry> legacyEntries = [];
            string currentModel = string.Empty;

            foreach (string line in ReadLinesShared(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("token", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("turn_context", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement rootElement = document.RootElement;
                    string type = GetString(rootElement, "type");
                    JsonElement payload = GetObject(rootElement, "payload");

                    if (type == "turn_context")
                    {
                        currentModel = FirstNonEmpty(
                            GetString(payload, "model"),
                            GetNestedString(payload, "state", "model"),
                            currentModel);
                        continue;
                    }

                    string recordModel = FirstNonEmpty(
                        GetString(payload, "model"),
                        GetNestedString(payload, "state", "model"),
                        GetNestedString(payload, "thread_settings", "model"),
                        currentModel);
                    DateTimeOffset? timestamp = ParseTimestamp(rootElement, payload);
                    if (timestamp is null || timestamp < periodStart)
                    {
                        continue;
                    }

                    if (type == "token_usage_record")
                    {
                        JsonElement usage = GetObject(payload, "usage");
                        TokenCounts counts = ReadCodexCounts(usage);
                        if (counts.TotalTokens > 0)
                        {
                            currentFormatEntries.Add(new UsageEntry(recordModel, counts));
                        }
                        continue;
                    }

                    if (type == "event_msg" && GetString(payload, "type") == "token_count")
                    {
                        JsonElement info = GetObject(payload, "info");
                        TokenCounts counts = ReadCodexCounts(GetObject(info, "last_token_usage"));
                        if (counts.TotalTokens > 0)
                        {
                            legacyEntries.Add(new UsageEntry(recordModel, counts));
                        }
                    }
                }
                catch (JsonException)
                {
                    // A partially-written active line will be retried on the next refresh.
                }
            }

            foreach (UsageEntry entry in currentFormatEntries.Count > 0 ? currentFormatEntries : legacyEntries)
            {
                total.Add(entry.Counts, entry.Model, inputIncludesCache: true);
            }
        }

        return total;
    }

    private static ProviderAccumulator CollectClaudeUsage(
        string root,
        DateTimeOffset periodStart,
        DateTime periodStartUtc,
        CancellationToken cancellationToken)
    {
        Dictionary<string, UsageEntry> uniqueMessages = new(StringComparer.Ordinal);
        foreach (string file in EnumerateRecentJsonLines(root, periodStartUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string line in ReadLinesShared(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!line.Contains("\"usage\"", StringComparison.Ordinal)
                    || !line.Contains("\"assistant\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement rootElement = document.RootElement;
                    if (GetString(rootElement, "type") != "assistant"
                        || ParseTimestamp(rootElement) is not DateTimeOffset timestamp
                        || timestamp < periodStart)
                    {
                        continue;
                    }

                    JsonElement message = GetObject(rootElement, "message");
                    JsonElement usage = GetObject(message, "usage");
                    TokenCounts counts = ReadClaudeCounts(usage);
                    if (counts.TotalTokens <= 0)
                    {
                        continue;
                    }

                    string messageId = FirstNonEmpty(
                        GetString(message, "id"),
                        GetString(rootElement, "uuid"),
                        $"{file}:{timestamp:O}:{uniqueMessages.Count}");
                    UsageEntry candidate = new(GetString(message, "model"), counts);
                    if (!uniqueMessages.TryGetValue(messageId, out UsageEntry existing)
                        || candidate.Counts.TotalTokens > existing.Counts.TotalTokens)
                    {
                        uniqueMessages[messageId] = candidate;
                    }
                }
                catch (JsonException)
                {
                    // A partially-written active line will be retried on the next refresh.
                }
            }
        }

        ProviderAccumulator total = new();
        foreach (UsageEntry entry in uniqueMessages.Values)
        {
            total.Add(entry.Counts, entry.Model, inputIncludesCache: false);
        }
        return total;
    }

    private static string ResolveCodexSessionsPath(string userProfile)
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(userProfile, ".codex")
            : configuredHome;
        return Path.Combine(codexHome, "sessions");
    }

    private static IEnumerable<string> EnumerateRecentJsonLines(string root, DateTime periodStartUtc)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (lastWriteUtc >= periodStartUtc)
                {
                    yield return file;
                }
            }

            foreach (string child in directories)
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<string> ReadLinesShared(string file)
    {
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            reader = new StreamReader(stream);
            stream = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                yield return line;
            }
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }

    private static TokenCounts ReadCodexCounts(JsonElement usage)
    {
        return new TokenCounts(
            GetInt64(usage, "total_tokens"),
            GetInt64(usage, "input_tokens"),
            GetInt64(usage, "output_tokens"),
            GetInt64(usage, "cached_input_tokens"),
            GetInt64(usage, "cache_write_input_tokens"));
    }

    private static TokenCounts ReadClaudeCounts(JsonElement usage)
    {
        long input = GetInt64(usage, "input_tokens");
        long output = GetInt64(usage, "output_tokens");
        long cacheRead = GetInt64(usage, "cache_read_input_tokens");
        long cacheWrite = GetInt64(usage, "cache_creation_input_tokens");
        return new TokenCounts(input + output + cacheRead + cacheWrite, input, output, cacheRead, cacheWrite);
    }

    private static DateTimeOffset? ParseTimestamp(params JsonElement[] elements)
    {
        foreach (JsonElement element in elements)
        {
            string value = GetString(element, "timestamp");
            if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset timestamp))
            {
                return timestamp;
            }
        }
        return null;
    }

    private static JsonElement GetObject(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : default;
    }

    private static string GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return GetString(GetObject(element, objectName), propertyName);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return Math.Max(0, number);
        }
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? Math.Max(0, number)
                : 0;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static ModelPricing? ResolvePricing(string model)
    {
        string normalized = model.Trim().ToLowerInvariant();
        if (ExactPricing.TryGetValue(normalized, out ModelPricing? exact))
        {
            return exact;
        }

        foreach ((string id, ModelPricing pricing) in ExactPricing.OrderByDescending(entry => entry.Key.Length))
        {
            if (normalized.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            {
                return pricing;
            }
        }
        return null;
    }

    private sealed class ProviderAccumulator
    {
        public long TotalTokens { get; private set; }

        public decimal EstimatedCostUsd { get; private set; }

        public long UnpricedTokens { get; private set; }

        public void Add(TokenCounts counts, string model, bool inputIncludesCache)
        {
            TotalTokens += counts.TotalTokens;
            ModelPricing? pricing = ResolvePricing(model);
            if (pricing is null)
            {
                UnpricedTokens += counts.TotalTokens;
                return;
            }

            long plainInput = inputIncludesCache
                ? Math.Max(0, counts.InputTokens - counts.CacheReadTokens - counts.CacheWriteTokens)
                : counts.InputTokens;
            EstimatedCostUsd += pricing.Estimate(
                plainInput,
                counts.OutputTokens,
                counts.CacheReadTokens,
                counts.CacheWriteTokens);
        }

        public void Add(LocalTokenEntry entry)
        {
            TokenCounts counts = new(
                entry.TotalTokens,
                Math.Max(0, entry.InputTokens),
                Math.Max(0, entry.OutputTokens) + Math.Max(0, entry.ReasoningTokens),
                Math.Max(0, entry.CacheReadTokens),
                Math.Max(0, entry.CacheWriteTokens));
            Add(counts, entry.Model, inputIncludesCache: false);
        }

        public ProviderTokenUsage ToSnapshot(string providerId)
        {
            return new ProviderTokenUsage(providerId, TotalTokens, EstimatedCostUsd, UnpricedTokens);
        }
    }

    private sealed record ModelPricing(
        decimal InputPerMillion,
        decimal OutputPerMillion,
        decimal CacheReadPerMillion,
        decimal CacheWritePerMillion)
    {
        public decimal Estimate(long input, long output, long cacheRead, long cacheWrite)
        {
            const decimal million = 1_000_000m;
            return (input * InputPerMillion
                + output * OutputPerMillion
                + cacheRead * CacheReadPerMillion
                + cacheWrite * CacheWritePerMillion) / million;
        }
    }

    private readonly record struct TokenCounts(
        long TotalTokens,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens);

    private readonly record struct UsageEntry(string Model, TokenCounts Counts);
}
