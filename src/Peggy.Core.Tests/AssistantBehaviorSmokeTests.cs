using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Peggy.Core;
using Peggy.Mcp;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AssistantBehaviorSmokeTests
{
    public static async Task RunAsync(string directory)
    {
        await TestAlertLifecycleAsync(directory);
        await TestMcpToolsAsync(directory);
        TestCopilotParsing(directory);
        TestSingleInstanceGuard();
        TestPrivacyGuard();
    }

    public static async Task RunLiveCopilotAsync()
    {
        var previousProvider = Environment.GetEnvironmentVariable(
            "PEGGY_MESSAGE_PROVIDER");
        var previousModel = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_MODEL");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_MESSAGE_PROVIDER",
                "mock");
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_MODEL",
                PeggyDefaults.CopilotModel);
            var provider = new MockMessageProvider();
            var now = DateTimeOffset.UtcNow;
            var batch = await provider.GetChangesAsync(
                new MonitoringCursorState(),
                100,
                now);
            TestAssert.True(
                batch.Messages.Count > 0,
                "The live Copilot test has no focused messages.");

            await using var runner = new CopilotRunner(
                new PeggySettings
                {
                    CopilotModel = PeggyDefaults.CopilotModel
                },
                Guid.NewGuid().ToString(),
                sessionCredits: 0);
            await runner.WarmConversationAsync();
            var alerts = await runner.RunAlertEvaluationAsync(
                batch.Messages,
                Array.Empty<AlertRecord>(),
                Array.Empty<FeedbackRule>());
            TestAssert.Equal(
                PeggyDefaults.CopilotModel,
                alerts.Usage.Model,
                "Live Copilot monitoring used the wrong model.");
            TestAssert.True(
                alerts.Decisions.All(decision =>
                    batch.Messages.Any(message =>
                        message.ThreadId == decision.ThreadId) &&
                    decision.MessageIds.All(id =>
                        batch.Messages.Any(message => message.Id == id))),
                "Live Copilot monitoring escaped the focused batch.");

            var deltas = new List<string>();
            var conversation = await runner.RunConversationAsync(
                "Prepare an Outlook email draft to person@example.com with " +
                "subject 'Peggy integration test' and body 'Draft only'. " +
                "Do not send it.",
                Array.Empty<AlertRecord>(),
                batch.Messages,
                deltas.Add);
            TestAssert.True(
                !string.IsNullOrWhiteSpace(conversation.Text),
                "Live Copilot conversation returned no response.");
            TestAssert.True(
                conversation.ProposedAction is
                {
                    Kind: WorkActionKind.CreateEmailDraft
                } proposal &&
                proposal.Recipients.SequenceEqual(
                    ["person@example.com"],
                    StringComparer.OrdinalIgnoreCase) &&
                proposal.Subject == "Peggy integration test" &&
                proposal.Body == "Draft only",
                "Live Copilot did not stage the exact proposal-only draft.");
            TestAssert.True(
                deltas.Count > 0 &&
                string.Concat(deltas).Length > 0,
                "Live persistent Copilot conversation produced no streaming deltas.");
            var followUpWatch = Stopwatch.StartNew();
            TimeSpan? followUpFirstDelta = null;
            var followUp = await runner.RunConversationAsync(
                "In one short sentence, what action did I just ask you to " +
                "prepare? Do not prepare another action.",
                Array.Empty<AlertRecord>(),
                onTextDelta: _ =>
                    followUpFirstDelta ??= followUpWatch.Elapsed);
            followUpWatch.Stop();
            TestAssert.True(
                followUp.ProposedAction is null &&
                followUp.Text.Contains(
                    "draft",
                    StringComparison.OrdinalIgnoreCase),
                "The persistent Copilot session did not retain conversation context.");
            TestAssert.True(
                followUp.Usage.SessionCredits >=
                alerts.Usage.SessionCredits,
                "Live Copilot session credits regressed between turns.");

            Console.WriteLine(
                "Live Copilot integration passed: monitoring and proposal-only " +
                $"conversation used {conversation.Usage.Model} in one session. " +
                $"Warm follow-up first delta: " +
                $"{followUpFirstDelta?.TotalSeconds:F2}s; complete: " +
                $"{followUpWatch.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_MESSAGE_PROVIDER",
                previousProvider);
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_MODEL",
                previousModel);
        }
    }

    private static async Task TestAlertLifecycleAsync(string directory)
    {
        var manager = new PeggyStateManager(
            new EncryptedStateStore(
                Path.Combine(directory, "alert-state.dat")));
        await manager.InitializeAsync();
        var alerts = new AlertManager(manager);
        var now = DateTimeOffset.UtcNow;
        const string sourceUrl =
            "https://outlook.office.com/mail/deeplink/read/message-1";
        var message = new Message(
            "message-1",
            MessageSource.Email,
            "thread-1",
            "Sender",
            "Subject",
            "Preview",
            now,
            false)
        {
            WebUrl = sourceUrl
        };
        var decision = new AlertDecision(
            "thread-1",
            AlertTier.Attention,
            "Review this item",
            "It has a deadline.",
            "opaque-copilot-reference",
            ["message-1"]);

        var created = alerts.Apply([decision], [message], now);
        TestAssert.Equal(
            1,
            created.MaterialUpdates.Count,
            "A new alert was not surfaced.");
        var alert = created.MaterialUpdates.Single();
        TestAssert.Equal(
            AlertTier.Attention,
            alert.Tier,
            "A new alert received the wrong tier.");
        TestAssert.Equal(
            sourceUrl,
            alert.SourceReference,
            "A message URL did not replace an opaque Copilot source reference.");

        var unchanged = alerts.Apply(
            [decision],
            [message with { WebUrl = null }],
            now.AddMinutes(1));
        TestAssert.Equal(
            0,
            unchanged.MaterialUpdates.Count,
            "An unchanged alert was surfaced again.");
        TestAssert.Equal(
            sourceUrl,
            alert.SourceReference,
            "An existing source URL was replaced by an opaque reference.");

        var beforeFeedbackHash = alert.MaterialHash;
        alerts.AddFeedback(
            alert.Id,
            FeedbackKind.LessLikeThis,
            now.AddMinutes(2));
        TestAssert.True(
            alert.Tier == AlertTier.Awareness &&
            alert.MaterialHash != beforeFeedbackHash,
            "Less-like-this feedback did not update alert material state.");
        var feedbackStable = alerts.Apply(
            [decision],
            [message],
            now.AddMinutes(3));
        TestAssert.Equal(
            0,
            feedbackStable.MaterialUpdates.Count,
            "Feedback caused an unchanged alert to resurface.");

        alerts.AddFeedback(
            alert.Id,
            FeedbackKind.AlwaysAlert,
            now.AddMinutes(4));
        TestAssert.Equal(
            AlertTier.Attention,
            alert.Tier,
            "Always-alert feedback did not promote awareness.");

        alerts.CorrectTier(
            alert.Id,
            AlertTier.Critical,
            now.AddMinutes(5));
        TestAssert.Equal(
            AlertTier.Critical,
            alert.Tier,
            "Tier correction was not applied.");
        TestAssert.True(
            manager.State.FeedbackRules.Any(rule =>
                rule.Kind == FeedbackKind.CorrectedTier),
            "Tier correction feedback was not retained.");

        alerts.Snooze(alert.Id, TimeSpan.FromHours(1), now);
        TestAssert.Equal(
            0,
            alerts.GetActiveAlerts().Count,
            "A snoozed alert remained active.");
        TestAssert.Equal(
            0,
            alerts.ReactivateDue(now.AddMinutes(59)).Count,
            "An alert reactivated before its snooze expired.");
        TestAssert.Equal(
            1,
            alerts.ReactivateDue(now.AddHours(1)).Count,
            "A due snoozed alert did not reactivate.");

        alerts.Resolve(alert.Id, AlertStatus.Resolved, now.AddHours(2));
        TestAssert.Equal(
            AlertStatus.Resolved,
            alert.Status,
            "Alert resolution failed.");
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => alerts.Resolve(
                alert.Id,
                AlertStatus.Active,
                now.AddHours(2)),
            "Alert resolution accepted an invalid target status.");
        TestAssert.Throws<InvalidOperationException>(
            () => alerts.Apply(
                [
                    new AlertDecision(
                        "outside-thread",
                        AlertTier.Attention,
                        "Invalid",
                        "Invalid",
                        string.Empty,
                        ["outside-message"])
                ],
                [message],
                now),
            "An alert decision outside the monitored batch was accepted.");
        manager.State.Alerts.Add(new AlertRecord
        {
            ThreadId = "legacy-chat",
            Source = MessageSource.Teams,
            Tier = AlertTier.Attention,
            Summary = "Legacy generic chat alert",
            Reason = "No exact message source.",
            SourceReference =
                "https://teams.microsoft.com/l/chat/old-chat/conversations",
            FirstSeenUtc = now,
            LastActivityUtc = now,
            UpdatedUtc = now
        });
        TestAssert.Equal(
            1,
            alerts.DismissAlertsWithoutVerifiedSources(
                now.AddHours(3)),
            "Legacy alerts without exact source messages were not dismissed.");
    }

    private static async Task TestMcpToolsAsync(string directory)
    {
        var provider = new MockMessageProvider();
        var email = await MessageTools.ListUnread(
            provider,
            source: "email",
            limit: 1);
        TestAssert.Equal(
            1,
            email.Count,
            "MCP unread source filtering or limits failed.");
        TestAssert.Equal(
            MessageSource.Email,
            email[0].Source,
            "MCP unread returned the wrong source.");
        TestAssert.True(
            await MessageTools.GetThread(provider, "t-100") is
                { Messages.Count: 2 },
            "MCP thread retrieval failed.");
        TestAssert.Equal(
            1,
            (await MessageTools.ListUpcomingEvents(
                provider,
                days: 7,
                limit: 20)).Count,
            "MCP calendar listing failed.");
        TestAssert.Equal(
            1,
            (await MessageTools.ListTasks(
                provider,
                includeCompleted: false,
                limit: 20)).Count,
            "MCP task listing failed.");
        TestAssert.Contains(
            "Mock WorkIQ answer",
            await MessageTools.AskWorkIq(
                provider,
                "What needs attention?"),
            "MCP WorkIQ prompting was not available.");

        var focusPath = Path.Combine(directory, "focus.json");
        var focused =
        new[]
        {
            new Message(
                "focused",
                MessageSource.Teams,
                "focused-thread",
                "Person",
                "Focused",
                "Only this item",
                DateTimeOffset.UtcNow,
                false)
        };
        await File.WriteAllTextAsync(
            focusPath,
            JsonSerializer.Serialize(focused));
        var previousFocusPath = Environment.GetEnvironmentVariable(
            "PEGGY_FOCUS_FILE");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FOCUS_FILE",
                focusPath);
            var scoped = await MessageTools.ListUnread(
                provider,
                source: null,
                limit: 50);
            TestAssert.Equal(
                "focused",
                scoped.Single().Id,
                "MCP focus scoping leaked unrelated messages.");
            TestAssert.True(
                File.Exists(focusPath),
                "Persistent MCP focus data was removed before later tools could use it.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(
                () => MessageTools.GetThread(provider, "t-100"),
                "MCP focused mode exposed an unrelated thread.");
            _ = await MessageTools.GetThread(
                provider,
                "focused-thread");
            await File.WriteAllTextAsync(
                focusPath,
                JsonSerializer.Serialize(Array.Empty<Message>()));
            TestAssert.Equal(
                0,
                (await MessageTools.ListUnread(
                    provider,
                    source: null,
                    limit: 50)).Count,
                "Persistent MCP focus data was not refreshed between turns.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FOCUS_FILE",
                previousFocusPath);
        }

        var actionPath = Path.Combine(directory, "action.json");
        var previousActionPath = Environment.GetEnvironmentVariable(
            "PEGGY_ACTION_FILE");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_ACTION_FILE",
                actionPath);
            var action = MessageTools.PrepareAction(
                "create_email_draft",
                subject: "Status",
                body: "Exact body",
                recipients: ["person@example.com"]);
            TestAssert.Equal(
                WorkActionKind.CreateEmailDraft,
                action.Kind,
                "MCP action kind parsing failed.");
            TestAssert.True(
                File.Exists(actionPath) &&
                File.ReadAllText(actionPath).Contains(
                    "Exact body",
                    StringComparison.Ordinal),
                "MCP action proposal was not staged.");
            TestAssert.Throws<InvalidOperationException>(
                () => MessageTools.PrepareAction(
                    "create_task",
                    subject: "Second action"),
                "MCP allowed two actions in one turn.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_ACTION_FILE",
                previousActionPath);
            File.Delete(actionPath);
        }

        var contextPath = Path.Combine(directory, "context.json");
        await File.WriteAllTextAsync(
            contextPath,
            """{"userSaid":"private turn"}""");
        var previousContextPath = Environment.GetEnvironmentVariable(
            "PEGGY_CONTEXT_FILE");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_CONTEXT_FILE",
                contextPath);
            var context = MessageTools.GetTurnContext();
            TestAssert.True(
                context.Contains(
                    "private turn",
                    StringComparison.Ordinal) &&
                File.Exists(contextPath),
                "MCP private context was not available to the persistent session.");
            await File.WriteAllTextAsync(
                contextPath,
                """{"userSaid":"next private turn"}""");
            TestAssert.Contains(
                "next private turn",
                MessageTools.GetTurnContext(),
                "Persistent MCP context was not refreshed between turns.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_CONTEXT_FILE",
                previousContextPath);
        }

        TestAssert.Throws<InvalidOperationException>(
            () => MessageTools.PrepareAction("unsupported"),
            "MCP accepted an unsupported action kind.");
        TestAssert.Throws<InvalidOperationException>(
            () => MessageTools.PrepareAction(
                "create_calendar_event",
                subject: "Review",
                startUtc: "not-a-time",
                endUtc: "2026-08-19T10:00:00Z"),
            "MCP accepted an invalid ISO timestamp.");
    }

    private static void TestCopilotParsing(string directory)
    {
        var message = new Message(
            "message-1",
            MessageSource.Email,
            "thread-1",
            "Sender",
            "Subject",
            "Preview",
            DateTimeOffset.UtcNow,
            false);
        var validJson =
            """
            {"decisions":[{"threadId":"thread-1","tier":"attention","summary":"Review","reason":"Deadline","sourceReference":"message-1","messageIds":["message-1"]}]}
            """;
        var parsed = InvokePrivate<IReadOnlyList<AlertDecision>>(
            typeof(CopilotRunner),
            "ParseAlertDecisions",
            validJson,
            new[] { message });
        TestAssert.Equal(
            1,
            parsed.Count,
            "Copilot alert decision parsing failed.");

        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"threadId":"outside","tier":"critical","summary":"Bad","reason":"Bad","sourceReference":"","messageIds":["message-1"]}]}
                """,
                new[] { message }),
            "Copilot could alert on an unscoped thread.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                "{}",
                new[] { message }),
            "Copilot output without a decisions array was accepted.");
        TestAssert.Equal(
            0,
            InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """{"decisions":[]}""",
                new[] { message }).Count,
            "An explicit empty decisions array was rejected.");

        var fenced = InvokePrivate<string>(
            typeof(CopilotRunner),
            "StripCodeFence",
            "```json\n{\"ok\":true}\n```");
        TestAssert.Equal(
            "{\"ok\":true}",
            fenced,
            "Copilot JSON code-fence removal failed.");

        var assistant = InvokePrivate<(string Text, string Model)>(
            typeof(CopilotRunner),
            "ParseAssistantMessage",
            """
            {"type":"tool.execution_complete","data":{}}
            {"type":"assistant.message","data":{"content":"Hello from Peggy","model":"gpt-5.4"}}
            """);
        TestAssert.Equal(
            "Hello from Peggy",
            assistant.Text,
            "Copilot assistant event parsing failed.");
        TestAssert.Equal(
            "gpt-5.4",
            assistant.Model,
            "Copilot model parsing failed.");

        var usagePath = Path.Combine(directory, "usage.json");
        File.WriteAllText(
            usagePath,
            """
            {"totalPremiumRequestCost":2.5,"currentModel":"gpt-5.4","lastCallInputTokens":123,"lastCallOutputTokens":45}
            """);
        var usage = InvokePrivate<object>(
            typeof(CopilotRunner),
            "ReadUsage",
            usagePath);
        TestAssert.True(
            usage.ToString()?.Contains("2.5", StringComparison.Ordinal) == true &&
            usage.ToString()?.Contains("gpt-5.4", StringComparison.Ordinal) == true,
            "Copilot usage telemetry parsing failed.");

        var alertPrompt = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildAlertPrompt");
        TestAssert.Contains(
            "Never call prepare_action",
            alertPrompt,
            "Monitoring prompt did not forbid actions.");
        TestAssert.Contains(
            $"list_unread with limit {PeggyDefaults.MonitoringBatchSize}",
            alertPrompt,
            "Monitoring prompt did not enforce the bounded batch size.");
        TestAssert.Contains(
            "Copilot timed out",
            InvokePrivate<string>(
                typeof(App),
                "GetMonitoringRemediation",
                new TimeoutException(),
                "classification"),
            "A classifier timeout did not identify the failing stage.");
        TestAssert.Contains(
            "WorkIQ timed out",
            InvokePrivate<string>(
                typeof(App),
                "GetMonitoringRemediation",
                new TimeoutException(),
                "retrieval"),
            "A retrieval timeout did not identify the failing stage.");
        var largeAlerts = Enumerable.Range(0, 100)
            .Select(index => new AlertRecord
            {
                ThreadId = $"thread-{index}",
                Tier = AlertTier.Attention,
                Summary = new string('s', 300),
                Reason = new string('r', 300),
                LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-index)
            })
            .ToList();
        var boundedContext = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildAlertContext",
            largeAlerts,
            Enumerable.Range(0, 100)
                .Select(index => new FeedbackRule
                {
                    ThreadId = $"thread-{index}",
                    Kind = FeedbackKind.AlwaysAlert,
                    Summary = new string('f', 300),
                    CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-index)
                })
                .ToList());
        TestAssert.True(
            alertPrompt.Length < 5_000 &&
            !alertPrompt.Contains(
                "thread-0",
                StringComparison.Ordinal) &&
            boundedContext.Contains(
                "thread-0",
                StringComparison.Ordinal) &&
            !boundedContext.Contains(
                "thread-99",
                StringComparison.Ordinal),
            "Private alert history was not bounded behind MCP context.");
        var voicePrompt = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildConversationPrompt");
        TestAssert.True(
            voicePrompt.Contains(
                "You are Peggy",
                StringComparison.Ordinal) &&
            voicePrompt.Contains(
                "one short sentence",
                StringComparison.Ordinal) &&
            voicePrompt.Contains(
                "under 20 words",
                StringComparison.Ordinal),
            "Voice responses were not constrained for conversational latency.");
        var streamedSentences = new List<string>();
        var sentenceBuffer = new StreamingSentenceBuffer(
            streamedSentences.Add);
        sentenceBuffer.Append("First answer. Second");
        sentenceBuffer.Append(" answer!");
        sentenceBuffer.Complete(
            "First answer. Second answer! Final fragment");
        TestAssert.True(
            streamedSentences.SequenceEqual(
            [
                "First answer.",
                "Second answer!",
                "Final fragment"
            ]),
            "Copilot streaming text was not split into speakable sentences.");
        var conversationContext = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildConversationContext",
            "private spoken request",
            largeAlerts);
        var conversationPrompt = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildConversationPrompt");
        TestAssert.True(
            !conversationPrompt.Contains(
                "private spoken request",
                StringComparison.Ordinal) &&
            conversationContext.Contains(
                "private spoken request",
                StringComparison.Ordinal),
            "Private voice content leaked into the Copilot command line.");
        TestAssert.Contains(
            "prepare_action does not execute anything",
            conversationPrompt,
            "Conversation prompt did not preserve proposal-only actions.");
        var digestPrompt = InvokePrivate<string>(
            typeof(CopilotRunner),
            "BuildLegacyDigestPrompt",
            true);
        TestAssert.Contains(
            "NO_ATTENTION",
            digestPrompt,
            "Legacy digest prompt lost its no-attention contract.");
        var staleScopeFiles = new[]
        {
            Path.Combine(
                Path.GetTempPath(),
                $"peggy-sdk-focus-{Guid.NewGuid():N}.json"),
            Path.Combine(
                Path.GetTempPath(),
                $"peggy-sdk-action-{Guid.NewGuid():N}.json")
        };
        staleScopeFiles[1] += "." + Guid.NewGuid().ToString("N");
        var orphanedScopeFile = Path.Combine(
            Path.GetTempPath(),
            $"peggy-sdk-focus-2147483646-{Guid.NewGuid():N}.json");
        var orphanedOneShotFile = Path.Combine(
            Path.GetTempPath(),
            $"peggy-context-2147483646-{Guid.NewGuid():N}.json");
        var liveScopeFile = Path.Combine(
            Path.GetTempPath(),
            $"peggy-sdk-action-{Environment.ProcessId}-" +
            $"{Guid.NewGuid():N}.json");
        var reusedPidScopeFile = Path.Combine(
            Path.GetTempPath(),
            $"peggy-focus-{Environment.ProcessId}-" +
            $"{Guid.NewGuid():N}.json");
        var scopeFiles = staleScopeFiles
            .Append(orphanedScopeFile)
            .Append(orphanedOneShotFile)
            .Append(liveScopeFile)
            .Append(reusedPidScopeFile)
            .ToList();
        try
        {
            foreach (var path in scopeFiles)
            {
                File.WriteAllText(path, "private test scope");
            }
            foreach (var path in staleScopeFiles)
            {
                File.SetLastWriteTimeUtc(
                    path,
                    DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(20)));
            }
            File.SetLastWriteTimeUtc(
                reusedPidScopeFile,
                Process.GetCurrentProcess().StartTime.ToUniversalTime()
                    .Subtract(TimeSpan.FromSeconds(1)));
            typeof(CopilotRunner).GetMethod(
                    "PurgeStaleScopeFiles",
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null);
            TestAssert.True(
                staleScopeFiles.All(path => !File.Exists(path)) &&
                !File.Exists(orphanedScopeFile) &&
                !File.Exists(orphanedOneShotFile) &&
                !File.Exists(reusedPidScopeFile) &&
                File.Exists(liveScopeFile),
                "Crash recovery retained orphaned SDK scope files or deleted " +
                "a live process scope.");
        }
        finally
        {
            foreach (var path in scopeFiles)
            {
                File.Delete(path);
            }
        }

        TestAssert.True(
            InvokePrivate<bool>(
                typeof(ConversationController),
                "IsNaturalApproval",
                "That's   correct!") &&
            InvokePrivate<bool>(
                typeof(ConversationController),
                "IsNaturalRejection",
                "DON'T.") &&
            InvokePrivate<bool>(
                typeof(ConversationController),
                "IsGoodbye",
                "Good   bye!"),
            "Natural voice confirmation phrases were not normalized reliably.");

        var previousModel = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_MODEL");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_MODEL",
                "environment-model");
            var resolvedModel = InvokePrivate<string>(
                typeof(CopilotRunner),
                "ResolveModel",
                new PeggySettings
                {
                    CopilotModel = "settings-model"
                });
            TestAssert.Equal(
                "environment-model",
                resolvedModel,
                "The documented Copilot model environment override was ignored.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_MODEL",
                previousModel);
        }
    }

    private static void TestSingleInstanceGuard()
    {
        var name = $@"Local\Peggy.Tests.{Guid.NewGuid():N}";
        using var first = new SingleInstanceGuard(name);
        TestAssert.True(
            first.TryAcquire(),
            "The first Peggy instance could not acquire its guard.");

        var secondAcquired = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondAcquired = second.TryAcquire();
        });
        thread.Start();
        thread.Join();
        TestAssert.True(
            !secondAcquired,
            "A concurrent Peggy instance bypassed the single-instance guard.");
    }

    private static void TestPrivacyGuard()
    {
        var privacy = new PrivacyGuard();
        privacy.SetSessionLocked(true);
        TestAssert.True(
            privacy.ShouldMask,
            "Privacy masking was disabled while the session was locked.");
        privacy.SetSessionLocked(false);
        _ = privacy.ShouldMask;
    }

    private static T InvokePrivate<T>(
        Type type,
        string methodName,
        params object?[] arguments)
    {
        var method = type.GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Private method {type.Name}.{methodName} was not found.");
        try
        {
            return (T)(method.Invoke(null, arguments)
                ?? throw new InvalidOperationException(
                    $"{type.Name}.{methodName} returned null."));
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
