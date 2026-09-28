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
        await TestMonitoringTroubleshooterAsync();
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
            var secondAlerts = await runner.RunAlertEvaluationAsync(
                batch.Messages,
                Array.Empty<AlertRecord>(),
                Array.Empty<FeedbackRule>());
            TestAssert.Equal(
                PeggyDefaults.CopilotModel,
                alerts.Usage.Model,
                "Live Copilot monitoring used the wrong model.");
            TestAssert.True(
                alerts.Usage.SessionId != secondAlerts.Usage.SessionId &&
                alerts.Usage.SessionId != runner.SessionId &&
                secondAlerts.Usage.SessionId != runner.SessionId,
                "Live monitoring reused a persistent Copilot session.");
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
                secondAlerts.Usage.SessionCredits,
                "Live Copilot session credits regressed between turns.");

            Console.WriteLine(
                "Live Copilot integration passed: isolated monitoring and " +
                $"persistent conversation used {conversation.Usage.Model}. " +
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

    private static async Task TestMonitoringTroubleshooterAsync()
    {
        var failure = new MonitoringFailureDetails
        {
            OccurredUtc = DateTimeOffset.UtcNow,
            RetrievalReferenceUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            Stage = "retrieval",
            Code = "retrieval-operation-failed",
            ExceptionType = "WorkIqRequestException",
            Message = "Synthetic retrieval failure.",
            StatusCode = 503,
            Operation = "fetch /me/mailFolders/inbox/messages",
            RequestId = "synthetic-request"
        };
        var settings = new PeggySettings
        {
            CopilotModel = PeggyDefaults.CopilotModel,
            TeamsScope = TeamsMonitoringScope.Off
        };
        var cursor = new MonitoringCursorState
        {
            InitialScanStartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var cursorBefore = JsonSerializer.Serialize(cursor);
        var progress = new List<TroubleshootingCheckResult>();
        var replayProvider = new MockMessageProvider();
        var troubleshooter = new MonitoringTroubleshooter(
            failure,
            settings,
            cursor,
            "person@example.com",
            failureCount: 3,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>(
            [
                new(
                    PeggyDefaults.CopilotModel,
                    "GPT-5.4",
                    SupportedReasoningEfforts: ["low"],
                    SupportsLongContext: true)
            ]),
            () => replayProvider);
        var report = await troubleshooter.RunAsync(
            new Progress<TroubleshootingCheckResult>(progress.Add));
        TestAssert.True(
            report.Checks is
            [
                { Status: TroubleshootingCheckStatus.Passed },
                { Status: TroubleshootingCheckStatus.NotReached },
                { Status: TroubleshootingCheckStatus.Passed },
                { Status: TroubleshootingCheckStatus.Passed }
            ] &&
            report.Recommendation.ShowRetry &&
            !report.Recommendation.ShowSignIn &&
            report.ToTechnicalText().Contains(
                "synthetic-request",
                StringComparison.Ordinal) &&
            replayProvider.LastGetChangesReferenceUtc ==
                failure.RetrievalReferenceUtc &&
            replayProvider.LastGetChangesLimit == 1 &&
            !ReferenceEquals(
                replayProvider.LastGetChangesCursor,
                cursor) &&
            JsonSerializer.Serialize(cursor) == cursorBefore,
            "The read-only monitoring troubleshooter did not report a " +
            "recovered retrieval safely.");

        var accountFailure = JsonSerializer.Deserialize<
            MonitoringFailureDetails>(JsonSerializer.Serialize(failure))
            ?? throw new InvalidOperationException(
                "Could not clone the synthetic monitoring failure.");
        accountFailure.StatusCode = null;
        var failedTroubleshooter = new MonitoringTroubleshooter(
            accountFailure,
            settings,
            cursor,
            "person@example.com",
            failureCount: 3,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>([]),
            () => throw new InvalidOperationException(
                "Synthetic Microsoft 365 failure."));
        var failed = await failedTroubleshooter.RunAsync();
        TestAssert.True(
            failed.Checks[0].Status ==
                TroubleshootingCheckStatus.Failed &&
            failed.Recommendation.ShowSignIn &&
            !failed.Recommendation.ShowRetry,
            "A Microsoft 365 diagnostic failure did not recommend sign-in.");

        var rateLimitedTroubleshooter = new MonitoringTroubleshooter(
            failure,
            settings,
            cursor,
            "person@example.com",
            failureCount: 3,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>([]),
            () => throw new WorkIqClient.WorkIqRequestException(
                429,
                TimeSpan.FromSeconds(30),
                "Synthetic rate limit."));
        var rateLimited = await rateLimitedTroubleshooter.RunAsync();
        TestAssert.True(
            rateLimited.Checks[0].Failure?.StatusCode == 429 &&
            rateLimited.Recommendation.ShowRetry &&
            !rateLimited.Recommendation.ShowSignIn,
            "A Microsoft 365 rate limit incorrectly recommended sign-in.");

        var unauthorizedTroubleshooter = new MonitoringTroubleshooter(
            failure,
            settings,
            cursor,
            "person@example.com",
            failureCount: 3,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>([]),
            () => throw new WorkIqClient.WorkIqRequestException(
                401,
                retryAfter: null,
                "Synthetic authorization failure."));
        var unauthorized = await unauthorizedTroubleshooter.RunAsync();
        TestAssert.True(
            unauthorized.Checks[0].Failure?.StatusCode == 401 &&
            unauthorized.Checks[2].Status ==
                TroubleshootingCheckStatus.Failed &&
            unauthorized.Recommendation.ShowSignIn &&
            !unauthorized.Recommendation.ShowSettings,
            "A WorkIQ authorization failure was incorrectly attributed to " +
            "the unrelated Copilot model failure.");
    }

    private static async Task TestAlertLifecycleAsync(string directory)
    {
        var manager = new PeggyStateManager(
            new EncryptedStateStore(
                Path.Combine(directory, "alert-state.dat")));
        await manager.InitializeAsync();
        var alerts = new AlertManager(manager);
        var now = new DateTimeOffset(DateTime.Today.AddHours(12)).ToUniversalTime();
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

        var regionalManager = new PeggyStateManager(
            new EncryptedStateStore(
                Path.Combine(directory, "regional-alert-state.dat")));
        await regionalManager.InitializeAsync();
        var regionalAlerts = new AlertManager(regionalManager);
        const string regionalSourceUrl =
            "https://emea.mail.microsoft.com/owa/?ItemID=" +
            "AAMkRegional%3D&exvsurl=1&viewmodel=ReadMessageItem";
        const string canonicalRegionalSourceUrl =
            "https://outlook.office.com/owa/?ItemID=" +
            "AAMkRegional%3D&exvsurl=1&viewmodel=ReadMessageItem";
        var regionalMessage = message with
        {
            Id = "regional-message",
            ThreadId = "regional-thread",
            WebUrl = regionalSourceUrl
        };
        var regionalDecision = decision with
        {
            ThreadId = "regional-thread",
            MessageIds = ["regional-message"]
        };
        var regionalCreated = regionalAlerts.Apply(
            [regionalDecision],
            [regionalMessage],
            now);
        TestAssert.Equal(
            canonicalRegionalSourceUrl,
            regionalCreated.MaterialUpdates.Single().SourceReference,
            "A regional Microsoft Outlook web link was not canonicalized.");
        TestAssert.Throws<InvalidDataException>(
            () => regionalAlerts.Apply(
                [
                    decision with
                    {
                        ThreadId = "lookalike-thread",
                        MessageIds = ["lookalike-message"]
                    }
                ],
                [
                    message with
                    {
                        Id = "lookalike-message",
                        ThreadId = "lookalike-thread",
                        WebUrl =
                            "https://emea.mail.microsoft.com.evil.example/owa/message"
                    }
                ],
                now),
            "A lookalike Outlook host was accepted.");
        TestAssert.Throws<InvalidDataException>(
            () => regionalAlerts.Apply(
                [
                    decision with
                    {
                        ThreadId = "insecure-thread",
                        MessageIds = ["insecure-message"]
                    }
                ],
                [
                    message with
                    {
                        Id = "insecure-message",
                        ThreadId = "insecure-thread",
                        WebUrl =
                            "http://emea.mail.microsoft.com/owa/message"
                    }
                ],
                now),
            "An insecure Outlook source link was accepted.");

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
        manager.State.Alerts.AddRange(
        [
            new AlertRecord
            {
                ThreadId = "dismiss-all-active",
                Status = AlertStatus.Active,
                SourceReference = sourceUrl,
                FirstSeenUtc = now,
                LastActivityUtc = now,
                UpdatedUtc = now
            },
            new AlertRecord
            {
                ThreadId = "dismiss-all-snoozed",
                Status = AlertStatus.Snoozed,
                SnoozedUntilUtc = now.AddHours(4),
                SourceReference = sourceUrl,
                FirstSeenUtc = now,
                LastActivityUtc = now,
                UpdatedUtc = now
            }
        ]);
        TestAssert.True(
            alerts.DismissAllActive(now.AddHours(3)) == 1 &&
            manager.State.Alerts.Single(item =>
                item.ThreadId == "dismiss-all-active").Status ==
                    AlertStatus.Dismissed &&
            manager.State.Alerts.Single(item =>
                item.ThreadId == "dismiss-all-snoozed").Status ==
                    AlertStatus.Snoozed,
            "Dismiss all did not target only active alerts.");
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
            {"decisions":[{"tier":"attention","summary":"Review","reason":"Deadline","sourceReference":"message-1","messageIds":["message-1"]}]}
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

        var teamsSource =
            "https://teams.microsoft.com/l/message/19%3Athread-one/12345";
        var originalMessages = new[]
        {
            new Message(
                "teams-channel-message:opaque-real-message-one",
                MessageSource.Teams,
                "teams-channel-thread:opaque-real-thread-one",
                "Sender one",
                "Subject one",
                "Preview one",
                DateTimeOffset.UtcNow,
                false)
            {
                WebUrl = teamsSource,
                TeamId = "opaque-team-one",
                ChannelId = "opaque-channel-one",
                RootMessageId = "opaque-root-one",
                IsMention = true
            },
            new Message(
                "teams-channel-message:opaque-real-message-two",
                MessageSource.Teams,
                "teams-channel-thread:opaque-real-thread-two",
                "Sender two",
                "Subject two",
                "Preview two",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                false)
            {
                WebUrl =
                    "https://teams.microsoft.com/l/message/19%3Athread-two/67890",
                TeamId = "opaque-team-two",
                ChannelId = "opaque-channel-two",
                RootMessageId = "opaque-root-two"
            }
        };
        var aliasScope = InvokePrivate<object>(
            typeof(CopilotRunner),
            "CreateMonitoringAliasScope",
            (object)originalMessages);
        var aliasedMessages = GetProperty<IReadOnlyList<Message>>(
            aliasScope,
            "Messages");
        TestAssert.True(
            aliasedMessages.Count == 2 &&
            aliasedMessages[0].Id == "message-1" &&
            aliasedMessages[0].ThreadId == "thread-1" &&
            aliasedMessages[1].Id == "message-2" &&
            aliasedMessages[1].ThreadId == "thread-2" &&
            aliasedMessages.All(item =>
                item.WebUrl is null &&
                item.TeamId is null &&
                item.ChannelId is null &&
                item.RootMessageId is null),
            "Monitoring aliases exposed a real identifier to Copilot.");
        var aliasedJson = JsonSerializer.Serialize(aliasedMessages);
        TestAssert.True(
            !originalMessages.Any(original =>
                aliasedJson.Contains(original.Id, StringComparison.Ordinal) ||
                aliasedJson.Contains(original.ThreadId, StringComparison.Ordinal)),
            "Monitoring focus JSON retained opaque source identifiers.");
        var restored = InvokePrivate<IReadOnlyList<AlertDecision>>(
            typeof(CopilotRunner),
            "ParseAndRestoreAlertDecisions",
            """
            {"decisions":[{"recipient":"user","recipientReason":"The current user is assigned the review.","tier":"attention","summary":"Review","reason":"Deadline","sourceReference":"message-1","messageIds":["message-1"]}]}
            """,
            aliasScope);
        TestAssert.True(
            restored.Count == 1 &&
            restored[0].ThreadId == originalMessages[0].ThreadId &&
            restored[0].MessageIds.SequenceEqual([originalMessages[0].Id]) &&
            restored[0].SourceReference == teamsSource,
            "Monitoring aliases did not restore exact local source identifiers.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"tier":"attention","summary":"Bad","reason":"Bad","sourceReference":"","messageIds":["message-1","message-2"]}]}
                """,
                aliasedMessages),
            "Copilot could combine message aliases from different threads.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"tier":"attention","summary":"Bad","reason":"Bad","sourceReference":"message-2","messageIds":["message-1"]}]}
                """,
                aliasedMessages),
            "Copilot could attach a source alias outside the selected messages.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"tier":"attention","summary":"Bad","reason":"Bad","sourceReference":"message-99","messageIds":["message-1"]}]}
                """,
                aliasedMessages),
            "Copilot could return an unknown source alias.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"tier":"attention","summary":"Bad","reason":"Bad","sourceReference":"","messageIds":[]}]}
                """,
                aliasedMessages),
            "Copilot could omit every message alias from an alert.");
        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """{"decisions":[null]}""",
                aliasedMessages),
            "Copilot could return an empty decision item.");

        TestAssert.Throws<InvalidDataException>(
            () => InvokePrivate<IReadOnlyList<AlertDecision>>(
                typeof(CopilotRunner),
                "ParseAlertDecisions",
                """
                {"decisions":[{"tier":"critical","summary":"Bad","reason":"Bad","sourceReference":"","messageIds":["message-outside"]}]}
                """,
                new[] { message }),
            "Copilot could alert on an unscoped message.");
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
            "BuildAlertPrompt",
            PeggyDefaults.MonitoringBatchSize);
        TestAssert.Contains(
            "Never call prepare_action",
            alertPrompt,
            "Monitoring prompt did not forbid actions.");
        TestAssert.Contains(
            $"list_unread exactly once with limit {PeggyDefaults.MonitoringBatchSize}",
            alertPrompt,
            "Monitoring prompt did not enforce the bounded batch size.");
        TestAssert.True(
            alertPrompt.Contains("untrusted data", StringComparison.Ordinal) &&
            alertPrompt.Contains(
                "batch-local aliases",
                StringComparison.Ordinal) &&
            !alertPrompt.Contains("get_thread", StringComparison.Ordinal),
            "Monitoring prompt did not preserve the isolated alias boundary.");
        var operationSessionOne = InvokePrivate<string>(
            typeof(CopilotRunner),
            "CreateOperationSessionId");
        var operationSessionTwo = InvokePrivate<string>(
            typeof(CopilotRunner),
            "CreateOperationSessionId");
        TestAssert.True(
            Guid.TryParse(operationSessionOne, out _) &&
            Guid.TryParse(operationSessionTwo, out _) &&
            operationSessionOne != operationSessionTwo,
            "Monitoring operation session IDs were reused.");
        TestAssert.True(
            PeggyDefaults.GetMonitoringBatchSize(0) == 20 &&
            PeggyDefaults.GetMonitoringBatchSize(1) == 10 &&
            PeggyDefaults.GetMonitoringBatchSize(2) == 5 &&
            PeggyDefaults.GetMonitoringBatchSize(3) == 1 &&
            PeggyDefaults.GetMonitoringBatchSize(100) == 1,
            "Monitoring retries did not reduce to a single poison item.");
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
        var rejectedUsage = new CopilotRunResult(
            string.Empty,
            Guid.NewGuid().ToString(),
            PeggyDefaults.CopilotModel,
            1,
            1,
            10,
            5);
        var rejectedResult = new CopilotClassificationException(
            "invalid-message-alias",
            rejectedUsage,
            new InvalidDataException());
        TestAssert.Contains(
            "smaller isolated batch",
            InvokePrivate<string>(
                typeof(App),
                "GetMonitoringRemediation",
                rejectedResult,
                "classification"),
            "Rejected classifier output did not explain its safe retry.");
        TestAssert.Equal(
            "invalid-message-alias",
            InvokePrivate<string>(
                typeof(App),
                "GetMonitoringFailureCode",
                rejectedResult,
                "classification"),
            "Classifier failure logs lost their safe error category.");
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
                .ToList(),
            Enumerable.Range(0, 15).ToDictionary(
                index => $"thread-{index}",
                index => $"batch-thread-alias-{index}",
                StringComparer.Ordinal));
        TestAssert.True(
            alertPrompt.Length < 5_000 &&
            boundedContext.Contains(
                "batch-thread-alias-0",
                StringComparison.Ordinal) &&
            !boundedContext.Contains(
                "thread-0",
                StringComparison.Ordinal) &&
            !boundedContext.Contains(
                "thread-15",
                StringComparison.Ordinal),
            "Private alert history was not scoped to batch-local aliases.");
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

        var providerEnvironmentType = typeof(CopilotRunner).Assembly.GetType(
            "Peggy.Tray.PeggyProviderEnvironment")
            ?? throw new InvalidOperationException(
                "The shared Peggy provider environment was not found.");
        var providerEnvironment = (Dictionary<string, string>?)(
            providerEnvironmentType.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(
                    null,
                    [
                        new PeggySettings
                        {
                            IncludeActiveCasesFolder = false
                        },
                        "account@example.com",
                        "principal-id"
                    ]))
            ?? throw new InvalidOperationException(
                "The shared Peggy provider environment was not created.");
        TestAssert.Equal(
            bool.FalseString,
            providerEnvironment[
                PeggyDefaults.IncludeActiveCasesFolderEnvironmentVariable],
            "The Active-folder selection was not forwarded to Copilot MCP " +
            "providers.");
        TestAssert.Equal(
            "principal-id",
            providerEnvironment[
                PeggyDefaults.WorkIqPrincipalIdEnvironmentVariable],
            "The verified principal ID was not forwarded to Copilot MCP " +
            "providers.");

        var previousModel = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_MODEL");
        var previousCopilotPath = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_PATH");
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

            var explicitCopilotPath = Path.Combine(directory, "copilot.exe");
            File.WriteAllText(explicitCopilotPath, "test executable");
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_PATH",
                explicitCopilotPath);
            TestAssert.Equal(
                explicitCopilotPath,
                InvokePrivate<string>(
                    typeof(CopilotRunner),
                    "ResolveCopilot"),
                "The explicit Copilot executable override was ignored.");
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_PATH",
                Path.Combine(directory, "missing-copilot.exe"));
            TestAssert.Throws<FileNotFoundException>(
                () => InvokePrivate<string>(
                    typeof(CopilotRunner),
                    "ResolveCopilot"),
                "An invalid explicit Copilot executable was silently ignored.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_MODEL",
                previousModel);
            Environment.SetEnvironmentVariable(
                "PEGGY_COPILOT_PATH",
                previousCopilotPath);
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

    private static T GetProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Property {instance.GetType().Name}.{propertyName} was not found.");
        return (T)(property.GetValue(instance)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().Name}.{propertyName} was null."));
    }
}
