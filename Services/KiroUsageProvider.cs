using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsageMonitor.Models;

namespace AIUsageMonitor.Services;

public enum KiroUsageSource
{
    Automatic,
    Cli,
    Ide
}

public sealed class KiroUsageProvider : IAiUsageProvider
{
    private const string IdeStateKey = "kiro.kiroAgent";
    private const int MaximumLogBytes = 2 * 1024 * 1024;
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
        return await GetUsageAsync(KiroUsageSource.Automatic, cancellationToken);
    }

    public async Task<AiUsageSnapshot> GetUsageAsync(
        KiroUsageSource source,
        CancellationToken cancellationToken = default)
    {
        AiUsageSnapshot? cliSnapshot = null;
        if (source is KiroUsageSource.Automatic or KiroUsageSource.Cli)
        {
            cliSnapshot = await TryGetCliUsageAsync(cancellationToken);
            if (cliSnapshot.IsAvailable || source == KiroUsageSource.Cli)
            {
                return cliSnapshot;
            }
        }

        if (source is KiroUsageSource.Automatic or KiroUsageSource.Ide)
        {
            AiUsageSnapshot? ideSnapshot = await Task.Run(
                () => TryGetIdeUsage(cancellationToken),
                cancellationToken);
            if (ideSnapshot is not null)
            {
                return ideSnapshot;
            }
            if (source == KiroUsageSource.Ide)
            {
                return AiUsageSnapshot.Unavailable(
                    ProviderId,
                    "Open Kiro IDE and sign in to display credit limits.",
                    ProviderIssueKind.NotDetected);
            }
        }

        return cliSnapshot ?? AiUsageSnapshot.Unavailable(
            ProviderId,
            "Install and sign in to Kiro IDE or Kiro CLI to display credit limits.",
            ProviderIssueKind.NotDetected);
    }

    private static async Task<AiUsageSnapshot> TryGetCliUsageAsync(CancellationToken cancellationToken)
    {
        string? command = FindKiroCli();
        if (command is null)
        {
            return AiUsageSnapshot.Unavailable(
                "kiro",
                "Install Kiro CLI or select Kiro IDE as the source.",
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
            return AiUsageSnapshot.Unavailable("kiro", "Kiro CLI timed out.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return AiUsageSnapshot.Unavailable(
                "kiro",
                "Kiro CLI could not be started.",
                ProviderIssueKind.NotDetected);
        }
        catch (InvalidOperationException exception)
        {
            return AiUsageSnapshot.Unavailable("kiro", exception.Message);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return AiUsageSnapshot.Unavailable("kiro", "Kiro CLI could not be accessed.");
        }
    }

    public void Dispose()
    {
    }

    internal static IReadOnlyList<LocalTokenEntry> CollectTodayTokenEntries(
        string userProfile,
        DateTimeOffset periodStart,
        KiroUsageSource source,
        CancellationToken cancellationToken)
    {
        List<LocalTokenEntry> entries = [];
        string configuredHome = Environment.GetEnvironmentVariable("KIRO_HOME") ?? string.Empty;
        string kiroHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(userProfile, ".kiro")
            : configuredHome;
        string sessionsRoot = Path.Combine(kiroHome, "sessions");
        if (Directory.Exists(sessionsRoot))
        {
            foreach (string path in EnumerateRecentJsonFiles(sessionsRoot, periodStart.UtcDateTime, source))
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
        }
        return entries;
    }

    internal static AiUsageSnapshot? ParseIdeUsageState(
        string rawState,
        string? planLabel,
        DateTimeOffset now)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawState);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("kiro.resourceNotifications.usageState", out JsonElement usageState))
            {
                root = usageState;
            }
            return ParseIdeUsageRoot(root, planLabel, now, "usageBreakdowns");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static AiUsageSnapshot? ParseIdeUsageResponse(string rawResponse, DateTimeOffset now)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawResponse);
            JsonElement root = document.RootElement;
            string plan = GetString(GetObject(root, "subscriptionInfo"), "subscriptionTitle");
            return ParseIdeUsageRoot(root, plan, now, "usageBreakdownList");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AiUsageSnapshot? TryGetIdeUsage(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string ideRoot = Path.Combine(appData, "Kiro");
        string globalStorage = Path.Combine(ideRoot, "User", "globalStorage");
        string statePath = Path.Combine(globalStorage, "state.vscdb");

        IdeLogSnapshot? logged = TryReadLatestIdeLog(Path.Combine(ideRoot, "logs"), cancellationToken);
        string? cachedJson = WindowsSqliteReader.ReadItemValue(statePath, IdeStateKey);
        AiUsageSnapshot? cached = string.IsNullOrWhiteSpace(cachedJson)
            ? null
            : ParseIdeUsageState(cachedJson, logged?.PlanLabel, DateTimeOffset.Now);
        if (cached is not null)
        {
            return cached;
        }
        if (logged is not null)
        {
            return ParseIdeUsageResponse(logged.ResponseJson, DateTimeOffset.Now);
        }
        if (Directory.Exists(ideRoot))
        {
            return AiUsageSnapshot.Unavailable(
                "kiro",
                "Open Kiro IDE and its usage view once, then try again.",
                ProviderIssueKind.AuthenticationRequired);
        }
        return null;
    }

    private static AiUsageSnapshot? ParseIdeUsageRoot(
        JsonElement root,
        string? planLabel,
        DateTimeOffset now,
        string breakdownsProperty)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(breakdownsProperty, out JsonElement breakdowns)
            || breakdowns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement primary = default;
        foreach (JsonElement candidate in breakdowns.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            primary = candidate;
            string type = FirstNonEmpty(GetString(candidate, "resourceType"), GetString(candidate, "type"));
            if (type.Equals("CREDIT", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        if (primary.ValueKind != JsonValueKind.Object
            || !TryCreateIdeWindow(primary, "Credits", out UsageWindow? parsedCredits))
        {
            return null;
        }

        UsageWindow credits = parsedCredits!;
        DateTimeOffset? reset = ParseIdeTimestamp(primary, "nextDateReset")
            ?? ParseIdeTimestamp(primary, "resetDate")
            ?? ParseIdeTimestamp(root, "nextDateReset");
        credits = credits with { ResetsAt = reset };

        UsageWindow? bonus = null;
        JsonElement freeTrial = FirstObject(primary, "freeTrialInfo", "freeTrialUsage");
        if (IsActivePool(freeTrial))
        {
            _ = TryCreateIdeWindow(freeTrial, "Bonus", out bonus);
        }
        if (bonus is null
            && primary.TryGetProperty("bonuses", out JsonElement bonuses)
            && bonuses.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in bonuses.EnumerateArray())
            {
                if (IsActivePool(candidate) && TryCreateIdeWindow(candidate, "Bonus", out bonus))
                {
                    break;
                }
            }
        }

        string normalizedPlan = NormalizeIdePlanLabel(planLabel);
        return new AiUsageSnapshot(
            "kiro",
            normalizedPlan,
            credits,
            bonus,
            null,
            null,
            now,
            true,
            "Usage read from the local Kiro IDE cache.");
    }

    private static bool TryCreateIdeWindow(JsonElement value, string label, out UsageWindow? window)
    {
        window = null;
        double? used = FirstNumber(value, "currentUsageWithPrecision", "currentUsage");
        double? limit = FirstNumber(value, "usageLimitWithPrecision", "usageLimit");
        if (used is null || limit is null || limit <= 0)
        {
            return false;
        }

        DateTimeOffset? expiry = ParseIdeTimestamp(value, "freeTrialExpiry")
            ?? ParseIdeTimestamp(value, "expiresAt")
            ?? ParseIdeTimestamp(value, "expiryDate");
        window = new UsageWindow(label, Math.Clamp(used.Value / limit.Value * 100, 0, 100), null, expiry);
        return true;
    }

    private static bool IsActivePool(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string status = FirstNonEmpty(GetString(value, "freeTrialStatus"), GetString(value, "status"));
        return string.IsNullOrWhiteSpace(status)
            || status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
            || status.Equals("EXHAUSTED", StringComparison.OrdinalIgnoreCase);
    }

    private static IdeLogSnapshot? TryReadLatestIdeLog(string logsRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logsRoot))
        {
            return null;
        }

        IEnumerable<string> logPaths = EnumerateDirectories(logsRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .SelectMany(sessionDirectory => EnumerateDirectories(sessionDirectory))
            .Where(path => Path.GetFileName(path).StartsWith("window", StringComparison.OrdinalIgnoreCase))
            .Select(windowDirectory => Path.Combine(
                windowDirectory,
                "exthost",
                "kiro.kiroAgent",
                "q-client.log"))
            .Where(File.Exists)
            .OrderByDescending(TryGetLastWriteTimeUtc)
            .Take(24);
        foreach (string logPath in logPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? tail = TryReadFileTail(logPath, MaximumLogBytes);
            if (string.IsNullOrWhiteSpace(tail))
            {
                continue;
            }

            string[] lines = tail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string line = lines[index];
                if (!line.Contains("GetUsageLimitsCommand", StringComparison.Ordinal))
                {
                    continue;
                }
                int jsonStart = line.IndexOf('{');
                if (jsonStart < 0)
                {
                    continue;
                }
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line[jsonStart..]);
                    JsonElement root = document.RootElement;
                    if (!GetString(root, "commandName").Equals("GetUsageLimitsCommand", StringComparison.Ordinal)
                        || !root.TryGetProperty("output", out JsonElement output)
                        || output.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    string plan = GetString(GetObject(output, "subscriptionInfo"), "subscriptionTitle");
                    return new IdeLogSnapshot(output.GetRawText(), plan);
                }
                catch (JsonException)
                {
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryReadFileTail(string path, int maximumBytes)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes)
            {
                stream.Seek(-maximumBytes, SeekOrigin.End);
            }
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (stream.Position > 0)
            {
                _ = reader.ReadLine();
            }
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonElement FirstObject(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement value = GetObject(element, name);
            if (value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        return default;
    }

    private static double? FirstNumber(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out double number)
                && double.IsFinite(number))
            {
                return number;
            }
            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                && double.IsFinite(number))
            {
                return number;
            }
        }
        return null;
    }

    private static DateTimeOffset? ParseIdeTimestamp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                ? parsed
                : null;
    }

    private static string NormalizeIdePlanLabel(string? value)
    {
        string plan = Regex.Replace(value?.Trim() ?? string.Empty, "^Kiro\\s+", string.Empty, RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(plan)
            ? "Kiro IDE"
            : CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(plan.ToLowerInvariant());
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
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

    private static IEnumerable<string> EnumerateRecentJsonFiles(
        string root,
        DateTime periodStartUtc,
        KiroUsageSource source)
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
                string relative = Path.GetRelativePath(root, child);
                bool isCliDirectory = relative.Equals("cli", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith($"cli{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
                if ((source == KiroUsageSource.Cli && !isCliDirectory)
                    || (source == KiroUsageSource.Ide && isCliDirectory))
                {
                    continue;
                }
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
                string relative = Path.GetRelativePath(root, file);
                bool isCliFile = relative.StartsWith($"cli{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
                if ((source == KiroUsageSource.Cli && !isCliFile)
                    || (source == KiroUsageSource.Ide && isCliFile))
                {
                    continue;
                }

                string companionPath = string.Equals(Path.GetFileName(file), "session.json", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, "messages.jsonl")
                    : Path.ChangeExtension(file, ".jsonl");
                DateTime newestWrite = TryGetLastWriteTimeUtc(file);
                if (File.Exists(companionPath))
                {
                    newestWrite = new DateTime(Math.Max(newestWrite.Ticks, TryGetLastWriteTimeUtc(companionPath).Ticks), DateTimeKind.Utc);
                }
                if (newestWrite >= periodStartUtc)
                {
                    yield return file;
                }
            }
        }
    }

    private static DateTime TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
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

    private sealed record IdeLogSnapshot(string ResponseJson, string PlanLabel);
}
