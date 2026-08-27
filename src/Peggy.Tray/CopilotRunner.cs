using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

public sealed record CopilotRunResult(
    string Text,
    string SessionId,
    string Model,
    decimal OperationCredits,
    decimal SessionCredits,
    long InputTokens,
    long OutputTokens);

public sealed record AlertDecision(
    string ThreadId,
    AlertTier Tier,
    string Summary,
    string Reason,
    string SourceReference,
    IReadOnlyList<string> MessageIds);

public sealed record CopilotAlertRunResult(
    IReadOnlyList<AlertDecision> Decisions,
    CopilotRunResult Usage);

public sealed record CopilotConversationRunResult(
    string Text,
    WorkActionRequest? ProposedAction,
    CopilotRunResult Usage);

/// <summary>
/// Runs one resumable, pinned GitHub Copilot CLI session through Peggy's MCP
/// policy gateway.
/// </summary>
public sealed class CopilotRunner : IAsyncDisposable
{
    private static readonly TimeSpan StaleScopeAge = TimeSpan.FromMinutes(10);

    private readonly string _copilotPath;
    private readonly string _mcpServerPath;
    private readonly PeggySettings _settings;
    private readonly string _account;
    private readonly string _model;
    private readonly string _sessionId;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly PersistentCopilotConversation _conversation;
    private decimal _sessionCredits;

    public CopilotRunner(
        PeggySettings? settings = null,
        string? sessionId = null,
        decimal sessionCredits = 0,
        string? account = null)
    {
        PurgeStaleScopeFiles();
        _settings = settings ?? new PeggySettings();
        _account = account?.Trim() ??
            PeggyDefaults.ConfiguredWorkIqAccount;
        _copilotPath = ResolveCopilot();
        _mcpServerPath = ResolveMcpServer();
        _model = ResolveModel(_settings);
        _sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString()
            : sessionId;
        if (!Guid.TryParse(_sessionId, out _))
        {
            throw new InvalidOperationException(
                "The assistant's persisted Copilot session ID is invalid.");
        }
        _sessionCredits = Math.Max(0, sessionCredits);
        _conversation = new PersistentCopilotConversation(
            _settings,
            _model,
            _copilotPath,
            _mcpServerPath,
            _account);
    }

    public string SessionId => _sessionId;

    public decimal SessionCredits => _sessionCredits;

    public async Task<CopilotAlertRunResult> RunAlertEvaluationAsync(
        IReadOnlyCollection<Message> messages,
        IReadOnlyCollection<AlertRecord> unresolvedAlerts,
        IReadOnlyCollection<FeedbackRule> feedbackRules,
        CancellationToken ct = default)
    {
        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "At least one message is required for alert evaluation.",
                nameof(messages));
        }

        var operation = await RunOperationAsync(
            BuildAlertPrompt(),
            messages,
            allowActionProposal: false,
            BuildAlertContext(
                unresolvedAlerts,
                feedbackRules),
            ct);
        var decisions = ParseAlertDecisions(operation.Text, messages);
        return new CopilotAlertRunResult(decisions, operation.Usage);
    }

    public async Task<CopilotConversationRunResult> RunConversationAsync(
        string recognizedText,
        IReadOnlyCollection<AlertRecord> activeAlerts,
        IReadOnlyCollection<Message>? focusMessages = null,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            throw new ArgumentException(
                "A recognized voice turn is required.",
                nameof(recognizedText));
        }

        await _runLock.WaitAsync(ct);
        PersistentConversationOutput operation;
        try
        {
            operation = await _conversation.RunAsync(
                BuildConversationContext(
                    recognizedText,
                    activeAlerts),
                focusMessages,
                onTextDelta,
                ct);
        }
        finally
        {
            _runLock.Release();
        }
        _sessionCredits += operation.OperationCredits;
        return new CopilotConversationRunResult(
            operation.Text,
            operation.ProposedAction,
            new CopilotRunResult(
                operation.Text,
                _sessionId,
                operation.Model,
                operation.OperationCredits,
                _sessionCredits,
                operation.InputTokens,
                operation.OutputTokens));
    }

    public Task WarmConversationAsync(CancellationToken ct = default)
        => _conversation.WarmAsync(ct);

    public async Task<CopilotRunResult> RunDigestAsync(
        IReadOnlyCollection<Message>? focusMessages = null,
        CancellationToken ct = default)
    {
        var operation = await RunOperationAsync(
            BuildLegacyDigestPrompt(
                focusMessages is { Count: > 0 }),
            focusMessages,
            allowActionProposal: false,
            turnContext: null,
            ct);
        return operation.Usage;
    }

    private async Task<OperationOutput> RunOperationAsync(
        string prompt,
        IReadOnlyCollection<Message>? focusMessages,
        bool allowActionProposal,
        string? turnContext,
        CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            return await RunOperationCoreAsync(
                prompt,
                focusMessages,
                allowActionProposal,
                turnContext,
                ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<OperationOutput> RunOperationCoreAsync(
        string prompt,
        IReadOnlyCollection<Message>? focusMessages,
        bool allowActionProposal,
        string? turnContext,
        CancellationToken ct)
    {
        string? focusPath = null;
        string? contextPath = null;
        string? actionPath = null;
        string? configPath = null;
        string? usagePath = null;
        try
        {
            if (focusMessages is { Count: > 0 })
            {
                focusPath = CreateTemporaryPath("peggy-focus");
                File.WriteAllText(
                    focusPath,
                    JsonSerializer.Serialize(focusMessages));
            }

            if (allowActionProposal)
            {
                actionPath = CreateTemporaryPath("peggy-action");
            }
            if (!string.IsNullOrWhiteSpace(turnContext))
            {
                contextPath = CreateTemporaryPath("peggy-context");
                File.WriteAllText(contextPath, turnContext);
            }

            configPath = CreateMcpConfig(
                focusPath,
                contextPath,
                actionPath);
            usagePath = CreateTemporaryPath("peggy-usage");
            var psi = BuildStartInfo(
                prompt,
                configPath,
                usagePath);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start Copilot at '{_copilotPath}'.",
                    ex);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(150));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await Task.WhenAll(stdoutTask, stderrTask);
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                throw new TimeoutException("Copilot took too long to respond.");
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            if (process.ExitCode != 0)
            {
                var detail = NormalizeProcessError(stderr);
                throw new InvalidOperationException(
                    detail.Length == 0
                        ? $"Copilot exited with code {process.ExitCode}."
                        : $"Copilot exited with code {process.ExitCode}: {detail}");
            }
            if (stdout.Length == 0)
            {
                throw new InvalidDataException("Copilot returned an empty response.");
            }

            var response = ParseAssistantMessage(stdout);
            var usage = ReadUsage(usagePath);
            VerifyPinnedModel(response.Model, usage.Model);
            var operationCredits = usage.TotalCredits >= _sessionCredits
                ? usage.TotalCredits - _sessionCredits
                : usage.TotalCredits;
            _sessionCredits = usage.TotalCredits;

            var proposedAction = LoadActionProposal(actionPath);
            var result = new CopilotRunResult(
                response.Text,
                _sessionId,
                _model,
                operationCredits,
                usage.TotalCredits,
                usage.InputTokens,
                usage.OutputTokens);
            return new OperationOutput(response.Text, proposedAction, result);
        }
        finally
        {
            DeleteTemporaryFile(focusPath);
            DeleteTemporaryFile(contextPath);
            DeleteTemporaryFile(actionPath);
            DeleteTemporaryFile(configPath);
            DeleteTemporaryFile(usagePath);
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string prompt,
        string configPath,
        string usagePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _copilotPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--session-id");
        startInfo.ArgumentList.Add(_sessionId);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_model);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--usage-output-file");
        startInfo.ArgumentList.Add(usagePath);
        startInfo.ArgumentList.Add("--stream");
        startInfo.ArgumentList.Add("off");
        startInfo.ArgumentList.Add("--available-tools=peggy");
        startInfo.ArgumentList.Add("--allow-tool=peggy");
        startInfo.ArgumentList.Add("--disable-builtin-mcps");
        startInfo.ArgumentList.Add("--additional-mcp-config");
        startInfo.ArgumentList.Add("@" + configPath);
        startInfo.ArgumentList.Add("--no-ask-user");
        startInfo.ArgumentList.Add("--no-color");
        startInfo.ArgumentList.Add("--no-custom-instructions");
        startInfo.ArgumentList.Add("--no-remote-export");
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add("error");
        return startInfo;
    }

    private string CreateMcpConfig(
        string? focusPath,
        string? contextPath,
        string? actionPath)
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["PEGGY_OUTLOOK_FOLDERS"] = string.Join(
                ";",
                _settings.AdditionalOutlookFolders),
            ["PEGGY_TEAMS_WATCHLIST"] = JsonSerializer.Serialize(
                _settings.TeamsChannelWatchlist)
        };
        if (!string.IsNullOrWhiteSpace(_account))
        {
            environment["PEGGY_WORKIQ_ACCOUNT"] = _account;
        }
        CopyEnvironmentVariable(environment, "PEGGY_MESSAGE_PROVIDER");
        CopyEnvironmentVariable(environment, "PEGGY_WORKIQ_PATH");
        if (focusPath is not null)
        {
            environment["PEGGY_FOCUS_FILE"] = focusPath;
        }
        if (contextPath is not null)
        {
            environment["PEGGY_CONTEXT_FILE"] = contextPath;
        }
        if (actionPath is not null)
        {
            environment["PEGGY_ACTION_FILE"] = actionPath;
        }

        var config = new
        {
            mcpServers = new
            {
                peggy = new
                {
                    type = "local",
                    command = _mcpServerPath,
                    tools = new[] { "*" },
                    env = environment
                }
            }
        };
        var path = CreateTemporaryPath("peggy-mcp-config");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        return path;
    }

    internal static string BuildAlertPrompt()
        =>
            "You are Peggy, a precise work-attention classifier. " +
            "First call get_turn_context exactly once for existing alerts and " +
            $"feedback. Call list_unread with limit " +
            $"{PeggyDefaults.MonitoringBatchSize}. The gateway has scoped it " +
            "to exactly the new or changed items in this monitoring batch. " +
            "Evaluate only items returned in this turn. Group related items by " +
            "their exact threadId so one conversation produces one evolving alert. " +
            "Use get_thread for at most one scoped thread only when critical context " +
            "is missing. You may call list_upcoming_events once only when the user's " +
            "current schedule materially changes urgency. Never call prepare_action. " +
            "Classify each relevant thread as critical, attention, awareness, or none. " +
            "Critical means immediate serious impact or a near deadline; attention " +
            "means a persistent card is warranted; awareness means useful history " +
            "without an interruption. Apply explicit feedback and avoid repeating " +
            "resolved or unchanged situations. Return JSON only in this exact shape: " +
            "{\"decisions\":[{\"threadId\":\"opaque exact ID\"," +
            "\"tier\":\"critical|attention|awareness|none\"," +
            "\"summary\":\"privacy-conscious summary under 300 characters\"," +
            "\"reason\":\"specific reason under 300 characters\"," +
            "\"sourceReference\":\"opaque message ID or empty\"," +
            "\"messageIds\":[\"opaque IDs\"]}]}. Every threadId and messageId " +
            "must come from list_unread. No markdown.";

    private static string BuildAlertContext(
        IReadOnlyCollection<AlertRecord> unresolvedAlerts,
        IReadOnlyCollection<FeedbackRule> feedbackRules)
    {
        var unresolved = unresolvedAlerts
            .Where(alert => alert.Status == AlertStatus.Active)
            .OrderBy(alert => alert.Tier)
            .ThenByDescending(alert => alert.LastActivityUtc)
            .Take(10)
            .Select(alert => new
            {
                alert.ThreadId,
                tier = alert.Tier.ToString().ToLowerInvariant(),
                summary = LimitPromptText(alert.Summary, 160),
                reason = LimitPromptText(alert.Reason, 160),
                alert.LastActivityUtc
            });
        var feedback = feedbackRules
            .OrderByDescending(rule => rule.CreatedUtc)
            .Take(20)
            .Select(rule => new
            {
                rule.ThreadId,
                kind = rule.Kind.ToString(),
                correctedTier =
                    rule.CorrectedTier?.ToString().ToLowerInvariant(),
                summary = LimitPromptText(rule.Summary, 160)
            });
        return JsonSerializer.Serialize(new
        {
            existingUnresolvedAlerts = unresolved,
            explicitFeedback = feedback
        });
    }

    internal static string BuildConversationPrompt()
        =>
            "You are Peggy, a cheerful little bird and concise " +
            "voice-only work assistant. Keep character flavor subtle and never " +
            "use animal sounds, magical sound effects, or excessive role-play. " +
            "Each SDK user message contains private turn-context JSON with the " +
            "spoken turn, local time, and active alerts. Use it directly and never " +
            "repeat the JSON. Answer in natural English. You may call " +
            "ask_workiq for broad or cross-service Microsoft 365 discovery, and use " +
            "the assistant's bounded structured tools when exact IDs or verification " +
            "are needed. " +
            "If the user " +
            "asks for a supported work action, call prepare_action exactly once with " +
            "all exact details, then explain that the action is ready for approval. " +
            "prepare_action does not execute anything. Do not claim a write succeeded. " +
            "Never expose internal IDs in spoken text. For ordinary questions, " +
            "answer in one short sentence, normally under 20 words, unless " +
            "the user explicitly asks for detail. Put the direct answer first. " +
            "Use plain spoken language without markdown, headings, or lists.";

    private static string BuildConversationContext(
        string recognizedText,
        IReadOnlyCollection<AlertRecord> activeAlerts)
    {
        var alerts = activeAlerts
            .Where(alert => alert.Status == AlertStatus.Active)
            .OrderBy(alert => alert.Tier)
            .ThenByDescending(alert => alert.LastActivityUtc)
            .Take(10)
            .Select(alert => new
            {
                alert.Id,
                alert.ThreadId,
                tier = alert.Tier.ToString().ToLowerInvariant(),
                summary = LimitPromptText(alert.Summary, 160),
                reason = LimitPromptText(alert.Reason, 160),
                alert.SourceReference
            });
        return JsonSerializer.Serialize(new
        {
            localTime = DateTimeOffset.Now,
            userSaid = recognizedText,
            activeAlerts = alerts
        });
    }

    private static string BuildLegacyDigestPrompt(bool hasFocus)
    {
        var focus = hasFocus
            ? "The gateway has scoped list_unread to exactly this monitoring batch. "
            : "Review up to 50 current unread items. ";
        return
            "You are Peggy, a cheerful assistant bird. You must call " +
            "list_unread with " +
            "limit 50 to inspect Outlook and Microsoft Teams. " + focus +
            "Use get_thread at most once when critical details are missing. Evaluate only " +
            "this turn's results. If nothing warrants attention, output exactly " +
            "NO_ATTENTION. Otherwise output at most two short sentences. Do not call " +
            "prepare_action or perform any write. Output only the response text.";
    }

    private static IReadOnlyList<AlertDecision> ParseAlertDecisions(
        string content,
        IReadOnlyCollection<Message> messages)
    {
        var json = StripCodeFence(content);
        AlertEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AlertEnvelope>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
                ?? throw new InvalidDataException(
                    "Copilot returned an empty alert decision.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Copilot returned an invalid alert decision.", ex);
        }

        var allowedThreads = messages
            .Select(message => message.ThreadId)
            .ToHashSet(StringComparer.Ordinal);
        var allowedMessages = messages
            .Select(message => message.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (envelope.Decisions is null)
        {
            throw new InvalidDataException(
                "Copilot's alert decision omitted the decisions array.");
        }
        var decisions = new List<AlertDecision>();
        foreach (var item in envelope.Decisions)
        {
            if (!allowedThreads.Contains(item.ThreadId))
            {
                throw new InvalidDataException(
                    "Copilot returned an alert for a thread outside this batch.");
            }

            if (item.MessageIds.Any(id => !allowedMessages.Contains(id)))
            {
                throw new InvalidDataException(
                    "Copilot returned an alert with a message outside this batch.");
            }

            var tier = item.Tier.Trim().ToLowerInvariant() switch
            {
                "critical" => AlertTier.Critical,
                "attention" => AlertTier.Attention,
                "awareness" => AlertTier.Awareness,
                "none" => (AlertTier?)null,
                _ => throw new InvalidDataException(
                    $"Copilot returned unsupported alert tier '{item.Tier}'.")
            };
            if (tier is null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.Summary) ||
                item.Summary.Length > 300 ||
                item.Reason.Length > 300)
            {
                throw new InvalidDataException(
                    "Copilot returned an invalid alert summary or reason.");
            }

            decisions.Add(new AlertDecision(
                item.ThreadId,
                tier.Value,
                item.Summary.Trim(),
                item.Reason.Trim(),
                item.SourceReference?.Trim() ?? string.Empty,
                item.MessageIds.Distinct(StringComparer.Ordinal).ToList()));
        }

        return decisions;
    }

    private static string LimitPromptText(
        string value,
        int maximum)
        => value.Length <= maximum
            ? value
            : value[..(maximum - 3)] + "...";

    internal static WorkActionRequest? LoadActionProposal(string? actionPath)
    {
        if (actionPath is null || !File.Exists(actionPath))
        {
            return null;
        }

        var contents = File.ReadAllText(actionPath);
        if (string.IsNullOrWhiteSpace(contents))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<WorkActionRequest>(contents)
                ?? throw new InvalidDataException(
                    "The assistant gateway returned an empty action proposal.");
            return WorkActionPolicy.NormalizeAndValidate(request);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The assistant gateway returned an invalid action proposal.", ex);
        }
    }

    private void VerifyPinnedModel(string responseModel, string usageModel)
    {
        if (!string.Equals(
                responseModel,
                _model,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                usageModel,
                _model,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Copilot did not use the assistant's pinned model '{_model}'.");
        }
    }

    private static (string Text, string Model) ParseAssistantMessage(
        string jsonLines)
    {
        string? text = null;
        string? model = null;
        using var reader = new StringReader(jsonLines);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "Copilot returned an invalid JSON event stream.", ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) ||
                    !string.Equals(
                        type.GetString(),
                        "assistant.message",
                        StringComparison.Ordinal) ||
                    !root.TryGetProperty("data", out var data))
                {
                    continue;
                }

                if (data.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(content.GetString()))
                {
                    text = content.GetString();
                }
                if (data.TryGetProperty("model", out var modelElement) &&
                    modelElement.ValueKind == JsonValueKind.String)
                {
                    model = modelElement.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text) ||
            string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidDataException(
                "Copilot's JSON stream did not contain a final assistant message.");
        }

        return (text.Trim(), model);
    }

    private static CopilotUsageSnapshot ReadUsage(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Copilot did not write usage telemetry.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty(
                "totalPremiumRequestCost",
                out var creditsElement) ||
            !creditsElement.TryGetDecimal(out var totalCredits) ||
            !root.TryGetProperty("currentModel", out var modelElement) ||
            modelElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty(
                "lastCallInputTokens",
                out var inputElement) ||
            !inputElement.TryGetInt64(out var inputTokens) ||
            !root.TryGetProperty(
                "lastCallOutputTokens",
                out var outputElement) ||
            !outputElement.TryGetInt64(out var outputTokens))
        {
            throw new InvalidDataException(
                "Copilot wrote an unsupported usage telemetry shape.");
        }

        return new CopilotUsageSnapshot(
            totalCredits,
            modelElement.GetString() ?? string.Empty,
            inputTokens,
            outputTokens);
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewLine < 0 || closingFence <= firstNewLine)
        {
            throw new InvalidDataException(
                "Copilot returned an incomplete JSON code fence.");
        }

        return trimmed[(firstNewLine + 1)..closingFence].Trim();
    }

    private static string ResolveModel(PeggySettings settings)
    {
        var configured = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_MODEL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrWhiteSpace(settings.CopilotModel)
            ? PeggyDefaults.CopilotModel
            : settings.CopilotModel.Trim();
    }

    private static string CreateTemporaryPath(string prefix)
        => Path.Combine(
            Path.GetTempPath(),
            $"{prefix}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");

    private static void CopyEnvironmentVariable(
        IDictionary<string, string> destination,
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            destination[name] = value;
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PurgeStaleScopeFiles()
    {
        var cutoff = DateTime.UtcNow - StaleScopeAge;
        foreach (var pattern in new[]
                 {
                     "peggy-focus-*.json",
                     "peggy-context-*.json",
                     "peggy-action-*.json",
                     "peggy-sdk-focus-*.json*",
                     "peggy-sdk-action-*.json*",
                     "peggy-mcp-config-*.json",
                     "peggy-usage-*.json"
                 })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                    Path.GetTempPath(),
                    pattern)
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    if (ShouldDeleteScopeFile(file, cutoff))
                    {
                        File.Delete(file);
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool ShouldDeleteScopeFile(
        string path,
        DateTime cutoffUtc)
    {
        var ownerProcessId = GetScopeOwnerProcessId(path);
        if (ownerProcessId is null)
        {
            return File.GetLastWriteTimeUtc(path) < cutoffUtc;
        }

        try
        {
            using var process = Process.GetProcessById(ownerProcessId.Value);
            return process.HasExited ||
                   File.GetLastWriteTimeUtc(path) <
                   process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int? GetScopeOwnerProcessId(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var prefix in new[]
                 {
                     "peggy-focus-",
                     "peggy-context-",
                     "peggy-action-",
                     "peggy-mcp-config-",
                     "peggy-usage-",
                     "peggy-sdk-focus-",
                     "peggy-sdk-action-"
                 })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = name[prefix.Length..];
            var separator = remainder.IndexOf('-');
            return separator > 0 &&
                   int.TryParse(remainder[..separator], out var processId)
                ? processId
                : null;
        }

        return null;
    }

    private static string NormalizeProcessError(string stderr)
    {
        var detail = string.Join(
            " ",
            stderr.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        return detail.Length <= 300
            ? detail
            : detail[..300] + "...";
    }

    private static string ResolveCopilot()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(
                    directory.Trim().Trim('"'),
                    "copilot.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return "copilot.exe";
    }

    private static string ResolveMcpServer()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar);
        var configuration = baseDirectory.Contains(
            @"\Release\",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var packaged = Path.Combine(
            baseDirectory,
            "Peggy.Mcp",
            "peggy-mcp.exe");
        if (IsRunnable(packaged))
        {
            return packaged;
        }

        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0;
             depth < 6 && directory is not null;
             depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Peggy.Mcp",
                "bin",
                configuration,
                "net10.0",
                "peggy-mcp.exe");
            if (IsRunnable(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(baseDirectory, "peggy-mcp.exe");
    }

    private static bool IsRunnable(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(executablePath)!;
        var name = Path.GetFileNameWithoutExtension(executablePath);
        return File.Exists(Path.Combine(
            directory,
            name + ".runtimeconfig.json"));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conversation.DisposeAsync();
        _runLock.Dispose();
    }

    private sealed record OperationOutput(
        string Text,
        WorkActionRequest? ProposedAction,
        CopilotRunResult Usage);

    private sealed record CopilotUsageSnapshot(
        decimal TotalCredits,
        string Model,
        long InputTokens,
        long OutputTokens);

    private sealed class AlertEnvelope
    {
        [JsonPropertyName("decisions")]
        public List<AlertDecisionPayload>? Decisions { get; init; }
    }

    private sealed class AlertDecisionPayload
    {
        [JsonPropertyName("threadId")]
        public string ThreadId { get; init; } = string.Empty;

        [JsonPropertyName("tier")]
        public string Tier { get; init; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; init; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = string.Empty;

        [JsonPropertyName("sourceReference")]
        public string? SourceReference { get; init; }

        [JsonPropertyName("messageIds")]
        public List<string> MessageIds { get; init; } = [];
    }
}
