using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public sealed class KiroUsageProvider : IAiUsageProvider
{
    private static readonly Regex AnsiCsiPattern = new("\\x1B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex AnsiOscPattern = new("\\x1B\\].*?(?:\\x07|\\x1B\\\\)", RegexOptions.Compiled);
    private static readonly Regex PercentPattern = new("█+\\s*(\\d+(?:\\.\\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex CreditsPattern = new("\\((\\d+(?:\\.\\d+)?)\\s+of\\s+(\\d+(?:\\.\\d+)?)\\s+covered", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BonusPattern = new("(?:Bonus credits:.*?)?(\\d+(?:\\.\\d+)?)\\s*/\\s*(\\d+(?:\\.\\d+)?)\\s+credits used", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ResetPattern = new("resets on\\s+(\\d{4}-\\d{2}-\\d{2}|\\d{1,2}/\\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LoginMarkers =
    [
        "not logged in",
        "login required",
        "failed to initialize auth portal",
        "kiro-cli login",
        "oauth error"
    ];

    public string ProviderId => "kiro";

    public async Task<AiUsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        string? command = FindKiroCli();
        if (command is null)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "Install Kiro CLI to display credit limits.",
                ProviderIssueKind.NotDetected);
        }

        try
        {
            string output = await RunUsageCommandAsync(command, useLegacyUi: false, cancellationToken);
            if (IsPlanOnlySummary(output))
            {
                output = await RunUsageCommandAsync(command, useLegacyUi: true, cancellationToken);
            }
            return ParseUsage(output, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, "Kiro CLI timed out.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return AiUsageSnapshot.Unavailable(
                ProviderId,
                "Kiro CLI could not be started.",
                ProviderIssueKind.NotDetected);
        }
        catch (InvalidOperationException exception)
        {
            return AiUsageSnapshot.Unavailable(ProviderId, exception.Message);
        }
    }

    public void Dispose()
    {
    }

    internal static IReadOnlyList<LocalTokenEntry> CollectTodayTokenEntries(
        string userProfile,
        DateTimeOffset periodStart,
        CancellationToken cancellationToken)
    {
        string sessionsRoot = Path.Combine(userProfile, ".kiro", "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        List<LocalTokenEntry> entries = [];
        foreach (string path in EnumerateJsonFiles(sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), "session.json", StringComparison.OrdinalIgnoreCase))
            {
                CollectIdeSession(path, periodStart, entries);
            }
            else
            {
                CollectCliSession(path, periodStart, entries);
            }
        }
        return entries;
    }

    internal static AiUsageSnapshot ParseUsage(string rawOutput, DateTimeOffset now)
    {
        string output = StripAnsi(rawOutput);
        string lowered = output.ToLowerInvariant();
        if (LoginMarkers.Any(lowered.Contains))
        {
            return AiUsageSnapshot.Unavailable(
                "kiro",
                "Run kiro-cli login to display credit limits.",
                ProviderIssueKind.AuthenticationRequired);
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            return AiUsageSnapshot.Unavailable("kiro", "Kiro CLI returned no usage data.");
        }

        Match creditsMatch = CreditsPattern.Match(output);
        Match percentMatch = PercentPattern.Match(output);
        if (!creditsMatch.Success && !percentMatch.Success)
        {
            bool managedPlan = Regex.IsMatch(output, "Plan:\\s*[^\\r\\n]+", RegexOptions.IgnoreCase)
                && (lowered.Contains("managed by admin", StringComparison.Ordinal)
                    || lowered.Contains("managed by organization", StringComparison.Ordinal));
            if (managedPlan)
            {
                return new AiUsageSnapshot(
                    "kiro",
                    ParsePlanLabel(output),
                    null,
                    null,
                    null,
                    null,
                    now,
                    true,
                    "This managed Kiro plan does not expose credit metrics.");
            }
            return AiUsageSnapshot.Unavailable("kiro", "The Kiro usage format was not recognized.");
        }

        double used = creditsMatch.Success ? ParseDouble(creditsMatch.Groups[1].Value) : 0;
        double limit = creditsMatch.Success ? ParseDouble(creditsMatch.Groups[2].Value) : 0;
        double usedPercent = percentMatch.Success
            ? ParseDouble(percentMatch.Groups[1].Value)
            : limit > 0 ? used / limit * 100 : 0;
        DateTimeOffset? resetsAt = ParseResetDate(ResetPattern.Match(output), now);
        UsageWindow credits = new("Credits", Math.Clamp(usedPercent, 0, 100), null, resetsAt);

        UsageWindow? bonus = null;
        if (lowered.Contains("bonus credits", StringComparison.Ordinal))
        {
            Match bonusMatch = BonusPattern.Match(output);
            if (bonusMatch.Success)
            {
                double bonusUsed = ParseDouble(bonusMatch.Groups[1].Value);
                double bonusLimit = ParseDouble(bonusMatch.Groups[2].Value);
                if (bonusLimit > 0)
                {
                    bonus = new UsageWindow("Bonus", Math.Clamp(bonusUsed / bonusLimit * 100, 0, 100), null, null);
                }
            }
        }

        return new AiUsageSnapshot(
            "kiro",
            ParsePlanLabel(output),
            credits,
            bonus,
            null,
            null,
            now,
            true,
            null);
    }

    private static async Task<string> RunUsageCommandAsync(
        string command,
        bool useLegacyUi,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["chat", "--no-interactive"];
        if (useLegacyUi)
        {
            arguments.Add("--agent-engine=v1");
            arguments.Add("--legacy-ui");
        }
        arguments.Add("/usage");

        ProcessResult result = await ProcessRunner.RunAsync(
            command,
            arguments,
            TimeSpan.FromSeconds(20),
            new Dictionary<string, string?> { ["TERM"] = "xterm-256color" },
            cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
    }

    private static string? FindKiroCli()
    {
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] knownPaths =
        [
            Path.Combine(localAppData, "Programs", "Kiro", "kiro-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kiro", "kiro-cli.exe")
        ];
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        IEnumerable<string> pathCandidates = (pathValue ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "kiro-cli.exe"));
        return pathCandidates.Concat(knownPaths).FirstOrDefault(File.Exists);
    }

    private static bool IsPlanOnlySummary(string output)
    {
        string clean = StripAnsi(output);
        return Regex.IsMatch(clean, "Plan:[^\\r\\n]*\\|\\s*\\d+\\s+usage breakdowns?\\s*$", RegexOptions.IgnoreCase)
            && !PercentPattern.IsMatch(clean)
            && !CreditsPattern.IsMatch(clean);
    }

    private static string StripAnsi(string value)
    {
        return AnsiOscPattern.Replace(AnsiCsiPattern.Replace(value ?? string.Empty, string.Empty), string.Empty);
    }

    private static string ParsePlanLabel(string output)
    {
        Match match = Regex.Match(output, "Plan:\\s*([^\\r\\n|]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(
                output,
                "Estimated Usage\\s*\\|[^\\r\\n|]*\\|\\s*([A-Z][A-Z0-9 ]+)",
                RegexOptions.IgnoreCase);
        }
        if (!match.Success)
        {
            match = Regex.Match(output, "\\|\\s*(KIRO\\s+[A-Z0-9 ]+?)\\s*\\|", RegexOptions.IgnoreCase);
        }
        string value = match.Success ? match.Groups[1].Value.Trim() : "Kiro";
        value = Regex.Replace(value, "^Kiro\\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? "Kiro"
            : CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    private static DateTimeOffset? ParseResetDate(Match match, DateTimeOffset now)
    {
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups[1].Value;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly exact))
        {
            return new DateTimeOffset(exact.ToDateTime(TimeOnly.MinValue), now.Offset);
        }
        if (!DateOnly.TryParseExact(value, "M/d", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly partial))
        {
            return null;
        }

        DateTime candidate = new(now.Year, partial.Month, partial.Day);
        if (candidate <= now.Date)
        {
            candidate = candidate.AddYears(1);
        }
        return new DateTimeOffset(candidate, now.Offset);
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0;
    }

    private static IEnumerable<string> EnumerateJsonFiles(string root)
    {
        Stack<string> directories = new();
        directories.Push(root);
        while (directories.Count > 0)
        {
            string directory = directories.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string child in children)
            {
                directories.Push(child);
            }
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.json").ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string file in files)
            {
                yield return file;
            }
        }
    }

    private static void CollectCliSession(
        string path,
        DateTimeOffset periodStart,
        ICollection<LocalTokenEntry> entries)
    {
        try
        {
            using JsonDocument document = ReadJsonDocument(path);
            JsonElement root = document.RootElement;
            JsonElement state = GetObject(root, "session_state");
            JsonElement modelState = GetObject(state, "rts_model_state");
            JsonElement modelInfo = GetObject(modelState, "model_info");
            string model = GetString(modelInfo, "model_id");
            long contextWindow = GetInt64(modelInfo, "context_window_tokens");
            JsonElement metadata = GetObject(state, "conversation_metadata");
            if (!metadata.TryGetProperty("user_turn_metadatas", out JsonElement turns)
                || turns.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            Dictionary<string, MessageContent> content = ReadCliMessageContent(Path.ChangeExtension(path, ".jsonl"));
            foreach (JsonElement turn in turns.EnumerateArray())
            {
                MessageContent combined = CombineMessageContent(turn, content);
                DateTimeOffset timestamp = ParseTimestamp(turn, "end_timestamp")
                    ?? combined.Timestamp
                    ?? File.GetLastWriteTimeUtc(path);
                if (timestamp < periodStart)
                {
                    continue;
                }

                long input = Math.Max(0, GetInt64(turn, "input_token_count"));
                long output = Math.Max(0, GetInt64(turn, "output_token_count"));
                if (input == 0)
                {
                    double percentage = GetDouble(turn, "context_usage_percentage");
                    input = contextWindow > 0 && percentage > 0
                        ? (long)Math.Floor(contextWindow * percentage / 100)
                        : EstimateTokens(combined.PromptCharacters);
                }
                if (output == 0)
                {
                    output = EstimateTokens(combined.AssistantCharacters);
                }
                if (input + output > 0)
                {
                    entries.Add(new LocalTokenEntry("kiro", string.IsNullOrWhiteSpace(model) ? "auto" : model, input, output));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private static void CollectIdeSession(
        string path,
        DateTimeOffset periodStart,
        ICollection<LocalTokenEntry> entries)
    {
        string messagesPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "messages.jsonl");
        if (!File.Exists(messagesPath))
        {
            return;
        }
        try
        {
            using JsonDocument session = ReadJsonDocument(path);
            string model = GetString(session.RootElement, "modelId");
            DateTimeOffset timestamp = ParseTimestamp(session.RootElement, "lastModifiedAt")
                ?? ParseTimestamp(session.RootElement, "createdAt")
                ?? File.GetLastWriteTimeUtc(path);
            if (timestamp < periodStart)
            {
                return;
            }

            long input = 0;
            long output = 0;
            foreach (string line in ReadLinesShared(messagesPath))
            {
                try
                {
                    using JsonDocument message = JsonDocument.Parse(line);
                    JsonElement payload = GetObject(message.RootElement, "payload");
                    string type = GetString(payload, "type");
                    string content = GetString(payload, "content");
                    if (type.Equals("user", StringComparison.OrdinalIgnoreCase))
                    {
                        input += EstimateTokens(content.Length);
                    }
                    else if (type.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        output += EstimateTokens(content.Length);
                    }
                }
                catch (JsonException)
                {
                }
            }
            if (input + output > 0)
            {
                entries.Add(new LocalTokenEntry("kiro", string.IsNullOrWhiteSpace(model) ? "auto" : model, input, output));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private static Dictionary<string, MessageContent> ReadCliMessageContent(string path)
    {
        Dictionary<string, MessageContent> result = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        int pendingPromptCharacters = 0;
        DateTimeOffset? pendingTimestamp = null;
        foreach (string line in ReadLinesShared(path))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string kind = GetString(root, "kind");
                JsonElement data = GetObject(root, "data");
                int characters = CountContentCharacters(data);
                if (kind == "Prompt")
                {
                    pendingPromptCharacters = characters;
                    pendingTimestamp = ParseTimestamp(GetObject(data, "meta"), "timestamp");
                }
                else if (kind == "AssistantMessage")
                {
                    string messageId = GetString(data, "message_id");
                    if (!string.IsNullOrWhiteSpace(messageId))
                    {
                        result[messageId] = new MessageContent(
                            pendingPromptCharacters,
                            characters,
                            pendingTimestamp);
                    }
                    pendingPromptCharacters = 0;
                    pendingTimestamp = null;
                }
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    private static MessageContent CombineMessageContent(
        JsonElement turn,
        IReadOnlyDictionary<string, MessageContent> content)
    {
        MessageContent combined = new(0, 0, null);
        if (!turn.TryGetProperty("message_ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array)
        {
            return combined;
        }
        foreach (JsonElement id in ids.EnumerateArray())
        {
            if (id.ValueKind == JsonValueKind.String
                && content.TryGetValue(id.GetString() ?? string.Empty, out MessageContent? message))
            {
                combined = new MessageContent(
                    combined.PromptCharacters + message.PromptCharacters,
                    combined.AssistantCharacters + message.AssistantCharacters,
                    combined.Timestamp ?? message.Timestamp);
            }
        }
        return combined;
    }

    private static int CountContentCharacters(JsonElement data)
    {
        if (!data.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        int count = 0;
        foreach (JsonElement part in content.EnumerateArray())
        {
            string kind = GetString(part, "kind");
            if (string.IsNullOrEmpty(kind) || kind.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                count += GetString(part, "data").Length;
            }
        }
        return count;
    }

    private static long EstimateTokens(int characters) => Math.Max(0, characters) / 4;

    private static JsonDocument ReadJsonDocument(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is string line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
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
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : 0;
    }

    private static double GetDouble(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return 0;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : 0;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
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
                return Math.Abs(numeric) < 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(numeric * 1000))
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)numeric);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        return null;
    }

    private sealed record MessageContent(
        int PromptCharacters,
        int AssistantCharacters,
        DateTimeOffset? Timestamp);
}
