using System.IO;
using System.Reflection;
using System.Text.Json;
using Peggy.Core;
using Peggy.Mcp;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class NotificationRefreshSmokeTests
{
    private static readonly CopilotRunResult Usage = new(
        "review", "synthetic-session", "synthetic-model", 1, 2, 3, 4);

    internal static async Task RunAsync(string directory)
    {
        var incoming = Incoming();
        var candidate = Candidate(incoming);
        var reply = incoming with
        {
            Id = "sent-reply", From = "Signed-in user", Preview = "The requested answer is attached. Done.",
            ReceivedAt = incoming.ReceivedAt.AddSeconds(1), IsRead = true, IsFromMe = true
        };
        var calls = 0;
        var logs = new List<string>();
        var credits = new List<(CopilotRunResult Usage, bool Rejected)>();
        Task<CopilotNotificationReviewResult> Suppress(
            AlertDecision original, IReadOnlyList<Message> context, CancellationToken ct)
        {
            calls++;
            TestAssert.True(context.Any(message => message.Id == reply.Id && message.IsFromMe),
                "Reassessment did not receive the user's sent reply.");
            return Task.FromResult(Review(NotificationVerdict.Suppress, reply.Id));
        }
        var unchanged = Create(Complete(incoming), Suppress, logs, credits);
        TestAssert.Equal(candidate, await unchanged.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "Unchanged conversation should preserve the original alert.");
        TestAssert.Equal(0, calls, "Unchanged context incurred a second Copilot operation.");
        var readOnly = Create(Complete(incoming with { IsRead = true }), Suppress);
        await readOnly.RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
        TestAssert.Equal(0, calls, "Read status was mistaken for a substantive reply.");

        var refreshed = Create(Complete(incoming, reply), Suppress, logs, credits);
        TestAssert.True(await refreshed.RefreshAsync(
                candidate, [incoming], incoming.ReceivedAt, CancellationToken.None) is null &&
            credits.Count == 1 && !credits[0].Rejected && credits[0].Usage.OperationCredits == 1,
            "Confirmed, evidenced resolution was not suppressed or its credits were not recorded.");
        TestAssert.True(logs.All(log => !log.Contains(reply.Preview, StringComparison.Ordinal) &&
            !log.Contains(incoming.Subject, StringComparison.Ordinal)),
            "Notification diagnostics leaked message content.");

        foreach (var verdict in new[] { NotificationVerdict.Keep, NotificationVerdict.Uncertain })
        {
            var partial = reply with { Preview = "I will investigate and reply tomorrow." };
            var reviewer = Create(Complete(incoming, partial), (_, _, _) =>
                Task.FromResult(Review(verdict, partial.Id)));
            var retained = await reviewer.RefreshAsync(
                candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
            TestAssert.True(retained is not null && retained.MessageIds.SequenceEqual(candidate.MessageIds) &&
                retained.SourceReference == candidate.SourceReference &&
                (verdict != NotificationVerdict.Uncertain || retained == candidate),
                "Partial/uncertain resolution removed an alert or changed its verified source.");
        }

        var oldReply = reply with { ReceivedAt = incoming.ReceivedAt.AddDays(-1) };
        await Create(Complete(oldReply, incoming), Suppress).RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
        TestAssert.Equal(1, calls, "An older reply unnecessarily replaced current-request evaluation.");

        var invalidContexts = new[]
        {
            new AlertThreadRefreshResult(null, false),
            Complete(incoming, reply) with { IsComplete = false, Warning = "Truncated" },
            Complete(reply),
            Complete(incoming, incoming, reply),
            Complete(incoming, reply with { ThreadId = "unrelated-chat" }),
            Complete(incoming, reply) with { Thread = new MessageThread(
                "wrong-thread", MessageSource.Email, "Wrong", [incoming, reply]) },
            Complete(incoming, reply with { FullText = new string('x', 65_000) }),
            Complete([incoming, ..Enumerable.Range(0, 101).Select(index =>
                reply with { Id = $"extra-{index}" })])
        };
        foreach (var context in invalidContexts)
        {
            var retained = await Create(context, (_, _, _) =>
                throw new NotSupportedException("Incomplete context must not be reassessed."))
                .RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
            TestAssert.Equal(candidate, retained, "An incomplete or mismatched refresh suppressed an alert.");
        }
        var noEvidence = Create(Complete(incoming, reply), (_, _, _) =>
            Task.FromResult(Review(NotificationVerdict.Suppress, incoming.Id)));
        TestAssert.Equal(candidate, await noEvidence.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "The original request was accepted as fresh resolution evidence.");
        TestAssert.Equal(candidate, await noEvidence.RefreshAsync(
            candidate, [], incoming.ReceivedAt, CancellationToken.None),
            "Missing baseline after restart made the original request look like fresh resolution.");
        var laterRequest = incoming with { Id = "follow-up", ReceivedAt = reply.ReceivedAt.AddSeconds(1) };
        var combined = candidate with { MessageIds = [incoming.Id, laterRequest.Id] };
        TestAssert.Equal(combined, await Create(Complete(incoming, reply, laterRequest), Suppress)
            .RefreshAsync(combined, [incoming, laterRequest], incoming.ReceivedAt, CancellationToken.None),
            "An earlier sent reply was accepted as resolving a subsequent request.");
        var edited = incoming with { Preview = "Request withdrawn; no response needed." };
        TestAssert.True(await Create(Complete(edited), (_, _, _) =>
                Task.FromResult(Review(NotificationVerdict.Suppress, incoming.Id)))
            .RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None) is null,
            "An explicit edit withdrawing the original request was not re-evaluated.");
        var enrichedReply = reply with
        {
            FullText = reply.Preview + " Still waiting for the remaining approval."
        };
        var enriched = Create(Complete(incoming, enrichedReply), (_, context, _) =>
        {
            TestAssert.True(context.Single(message => message.Id == reply.Id).FullText!
                .EndsWith("remaining approval.", StringComparison.Ordinal),
                "Review did not receive outstanding work beyond the preview.");
            return Task.FromResult(Review(NotificationVerdict.Keep, reply.Id));
        });
        TestAssert.True(await enriched.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None) is not null,
            "A partial reply with remaining work was suppressed.");

        foreach (var error in new Exception[]
        {
            new IOException("Synthetic read failure"),
            new TimeoutException("Synthetic timeout"),
            new InvalidDataException("Synthetic invalid output")
        })
        {
            var retained = await Create(Complete(incoming, reply), (_, _, _) => throw error)
                .RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
            TestAssert.Equal(candidate, retained, "A failed reassessment lost the pending alert.");
        }
        var failedRead = Provider((_, _, _) =>
            throw new InvalidOperationException("Synthetic authentication failure."));
        var failedRefresher = new AlertNotificationRefresher(failedRead, Suppress,
            (_, _) => throw new InvalidOperationException("No usage expected."), logs.Add);
        TestAssert.Equal(candidate, await failedRefresher.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "A refresh failure silently dropped an alert.");
        var invalidReview = Create(Complete(incoming, reply), (_, _, _) =>
            throw new CopilotClassificationException("invalid", Usage, new InvalidDataException()),
            logs, credits);
        await invalidReview.RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None);
        TestAssert.True(credits.Count == 2 && credits[1].Rejected,
            "Invalid model output lost already incurred review credits.");
        await TestAssert.ThrowsAsync<NotSupportedException>(() =>
            Create(Complete(incoming, reply), (_, _, _) => throw new NotSupportedException("Programming defect."))
                .RefreshAsync(candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "Unexpected programming defects were swallowed by the conservative failure path.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            refreshed.RefreshAsync(candidate, [incoming], incoming.ReceivedAt, cancelled.Token),
            "Shutdown cancellation was converted into a notification.");
        var neverRead = Provider((_, _, _) => throw new NotSupportedException("Budget exhausted."));
        var expired = new AlertNotificationRefresher(
            neverRead, Suppress, (_, _) => Task.CompletedTask, logs.Add, TimeSpan.Zero);
        TestAssert.Equal(candidate, await expired.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "Exhausted batch budget did not preserve remaining candidates.");
        var slow = Provider(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable.");
        });
        var timed = new AlertNotificationRefresher(slow, Suppress, (_, _) => Task.CompletedTask, logs.Add,
            TimeSpan.FromMilliseconds(25));
        TestAssert.Equal(candidate, await timed.RefreshAsync(
            candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
            "Refresh deadline did not retain the alert.");

        TestParsing(incoming, reply);
        var focusPath = Path.Combine(directory, "notification-refresh-focus.json");
        var originalFocus = Environment.GetEnvironmentVariable("PEGGY_FOCUS_FILE");
        try
        {
            await File.WriteAllTextAsync(focusPath, JsonSerializer.Serialize(new[] { incoming, reply }));
            Environment.SetEnvironmentVariable("PEGGY_FOCUS_FILE", focusPath);
            var focused = await MessageTools.ListUnread(neverRead, limit: 100);
            TestAssert.True(focused.Count == 2 && focused.Any(message => message.IsFromMe && message.IsRead),
                "The bounded MCP focus filtered out read/sent replies needed for refresh.");
        }
        finally { Environment.SetEnvironmentVariable("PEGGY_FOCUS_FILE", originalFocus); }
    }

    private static void TestParsing(Message incoming, Message reply)
    {
        var valid = """{"verdict":"suppress","tier":"attention","summary":"Handled","reason":"Answered","evidenceMessageIds":["sent-reply"]}""";
        TestAssert.Equal(NotificationVerdict.Suppress,
            CopilotRunner.ParseNotificationReview(valid, [incoming, reply]).Verdict,
            "A valid, explicit notification review was rejected.");
        foreach (var bad in new[]
        {
            "{}", "null", """{"decisions":[]}""", "not JSON",
            valid.Replace("\"suppress\"", "\"none\"", StringComparison.Ordinal),
            valid.Replace("\"sent-reply\"", "\"foreign-alias\"", StringComparison.Ordinal),
            valid.Replace("[\"sent-reply\"]", "[]", StringComparison.Ordinal),
            valid.Replace("\"attention\"", "\"unknown\"", StringComparison.Ordinal),
            valid.Replace("\"Answered\"", "\"\"", StringComparison.Ordinal)
        })
        {
            TestAssert.Throws<InvalidDataException>(
                () => CopilotRunner.ParseNotificationReview(bad, [incoming, reply]),
                "Missing, invalid or ungrounded model output could suppress an alert.");
        }
        var prompt = CopilotRunner.BuildNotificationReviewPrompt(100);
        foreach (var required in new[]
        {
            "untrusted data", "list_unread exactly once with limit 100",
            "Never call prepare_action", "partial", "uncertain", "IsFromMe", "older reply"
        })
        {
            TestAssert.Contains(required, prompt, "Notification review prompt lost a safety constraint.");
        }
        var fullReply = reply with { FullText = reply.Preview + " Still need approval." };
        var aliases = typeof(CopilotRunner).GetMethod("CreateMonitoringAliasScope",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [new[] { incoming, fullReply }])!;
        var scoped = (IReadOnlyList<Message>)aliases.GetType().GetProperty("Messages")!.GetValue(aliases)!;
        TestAssert.True(scoped[1].IsFromMe && scoped[1].WebUrl is null &&
            scoped[1].Id != reply.Id && scoped[1].ThreadId != reply.ThreadId &&
            scoped[1].Preview == fullReply.FullText && scoped[1].FullText is null,
            "Alias conversion lost full text/sender attribution or leaked the real source identifiers.");
    }

    internal static void RunDesktop(App app)
    {
        var root = Path.Combine(Path.GetTempPath(), $"peggy-refresh-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var names = new[] { "_state", "_alertManager", "_monitoringGeneration", "_accountSwitching",
            "_quitting", "_alertCleanupVersion", "_savedAlertCleanupVersion" };
        var oldFields = names.ToDictionary(name => name, name => Field(name).GetValue(app));
        var oldLog = Environment.GetEnvironmentVariable("PEGGY_LOG_PATH");
        Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", Path.Combine(root, "test.log"));
        var state = new PeggyStateManager(new EncryptedStateStore(Path.Combine(root, "state.dat")));
        var manager = new AlertManager(state);
        Field("_state").SetValue(app, state);
        Field("_alertManager").SetValue(app, manager);
        Field("_monitoringGeneration").SetValue(app, 41);
        Field("_accountSwitching").SetValue(app, false);
        Field("_quitting").SetValue(app, false);
        Field("_alertCleanupVersion").SetValue(app, 0L);
        Field("_savedAlertCleanupVersion").SetValue(app, 0L);
        try
        {
            var incoming = Incoming();
            var candidate = Candidate(incoming);
            var reply = incoming with
            {
                Id = "sent-reply", Preview = "Completed.", IsFromMe = true,
                ReceivedAt = incoming.ReceivedAt.AddSeconds(1)
            };
            var refresher = Create(Complete(incoming, reply), (_, _, _) =>
                Task.FromResult(Review(NotificationVerdict.Suppress, reply.Id)));
            var reviewed = Invoke<Task<IReadOnlyList<AlertDecision>>>(
                app, "RefreshPendingAlertDecisionsAsync",
                new List<AlertDecision> { candidate }, new[] { incoming }, refresher, 41,
                Invoke<object>(app, "CaptureAlertRevisions", new List<AlertDecision> { candidate }));
            SetupStepIndicatorTests.Complete(reviewed);
            TestAssert.True(reviewed.Result.Count == 0 && state.State.Alerts.Count == 0,
                "A suppressed new candidate became visible before review finished.");

            AlertRecord Snooze()
            {
                var alert = manager.Apply([candidate], [incoming], DateTimeOffset.UtcNow)
                    .MaterialUpdates.Single();
                manager.Snooze(alert.Id, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddSeconds(-2));
                return alert;
            }
            var snoozed = Snooze();
            var due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync", refresher, 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Count == 0 &&
                snoozed.Status == AlertStatus.Dismissed && snoozed.SnoozedUntilUtc is null,
                "A handled snoozed alert was reactivated before its reply refresh.");
            var save = state.SaveAsync();
            SetupStepIndicatorTests.Complete(save);
            var reload = new EncryptedStateStore(Path.Combine(root, "state.dat")).LoadAsync();
            SetupStepIndicatorTests.Complete(reload);
            TestAssert.Equal(AlertStatus.Dismissed, reload.Result.Alerts.Single().Status,
                "Suppressed due snooze was not durable.");

            snoozed = Snooze();
            var incomplete = Create(new AlertThreadRefreshResult(null, false), (_, _, _) =>
                throw new NotSupportedException("No review for unreadable context."));
            due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync", incomplete, 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Single().Id == snoozed.Id &&
                snoozed.Status == AlertStatus.Active,
                "An unavailable refresh prevented an otherwise due notification.");
            state.State.Settings.ReceiveAwarenessAlerts = false;
            manager.Snooze(snoozed.Id, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddSeconds(-2));
            var downgrade = Create(Complete(incoming, reply), (_, _, _) =>
            {
                var result = Review(NotificationVerdict.Keep, reply.Id);
                return Task.FromResult(result with { Review = result.Review with { Tier = AlertTier.Awareness } });
            });
            due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync", downgrade, 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Count == 0 && manager.GetActiveAlerts().Count == 0 &&
                snoozed.Tier == AlertTier.Awareness,
                "A snoozed Attention alert downgraded during review bypassed the disabled Awareness tier.");
            state.State.Settings.ReceiveAwarenessAlerts = true;
            snoozed.Tier = AlertTier.Attention;
            manager.Snooze(snoozed.Id, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddSeconds(-2));
            var duringReview = Create(Complete(incoming, reply), (_, _, _) =>
            {
                manager.Resolve(snoozed.Id, AlertStatus.Resolved, DateTimeOffset.UtcNow);
                return Task.FromResult(Review(NotificationVerdict.Keep, reply.Id));
            });
            due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync", duringReview, 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Count == 0 && snoozed.Status == AlertStatus.Resolved,
                "Review resurrected an alert the user resolved while the refresh was running.");

            foreach (var action in new[] { "dismiss", "snooze", "resolve" })
            {
                state.State.Alerts.Clear();
                var active = manager.Apply([candidate], [incoming], DateTimeOffset.UtcNow).MaterialUpdates.Single();
                var revision = Invoke<object>(app, "CaptureAlertRevisions", new List<AlertDecision> { candidate });
                var userAction = Create(Complete(incoming, reply), (_, _, _) =>
                {
                    if (action == "snooze")
                        manager.Snooze(active.Id, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);
                    else
                        manager.Resolve(active.Id, action == "resolve" ? AlertStatus.Resolved : AlertStatus.Dismissed,
                            DateTimeOffset.UtcNow);
                    return Task.FromResult(Review(NotificationVerdict.Keep, reply.Id));
                });
                reviewed = Invoke<Task<IReadOnlyList<AlertDecision>>>(
                    app, "RefreshPendingAlertDecisionsAsync", new List<AlertDecision> { candidate },
                    new[] { incoming }, userAction, 41, revision);
                SetupStepIndicatorTests.Complete(reviewed);
                TestAssert.Equal(0, reviewed.Result.Count, $"Pending refresh undid the user's {action}.");
            }

            var recorder = Invoke<Func<CopilotRunResult, bool, Task>>(app, "CreateNotificationUsageRecorder");
            Field("_monitoringGeneration").SetValue(app, 42);
            Field("_accountSwitching").SetValue(app, true);
            SetupStepIndicatorTests.Complete(recorder(Usage, false));
            SetupStepIndicatorTests.Complete(recorder(Usage, true));
            reload = new EncryptedStateStore(Path.Combine(root, "state.dat")).LoadAsync();
            SetupStepIndicatorTests.Complete(reload);
            TestAssert.True(reload.Result.CreditHistory.Count == 2 &&
                reload.Result.CreditHistory.Sum(entry => entry.Credits) == 2 &&
                reload.Result.CreditHistory[1].Operation == "notification-refresh-rejected",
                "Pausing or draining for account switch lost already incurred review charges.");
            Field("_monitoringGeneration").SetValue(app, 41);
            Field("_accountSwitching").SetValue(app, false);

            var changingSession = Create(Complete(incoming, reply), (_, _, _) =>
            {
                Field("_monitoringGeneration").SetValue(app, 42);
                return Task.FromResult(Review(NotificationVerdict.Keep, reply.Id));
            });
            reviewed = Invoke<Task<IReadOnlyList<AlertDecision>>>(
                app, "RefreshPendingAlertDecisionsAsync", new List<AlertDecision> { candidate },
                new[] { incoming }, changingSession, 41,
                Invoke<object>(app, "CaptureAlertRevisions", new List<AlertDecision> { candidate }));
            SetupStepIndicatorTests.Complete(reviewed);
            TestAssert.Equal(0, reviewed.Result.Count, "A stale account/session published its reviewed candidate.");
        }
        finally
        {
            foreach (var pair in oldFields) Field(pair.Key).SetValue(app, pair.Value);
            Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", oldLog);
            Directory.Delete(root, true);
        }
    }

    private static Message Incoming() => new("incoming", MessageSource.Email, "email-thread",
        "Customer", "Synthetic request", "Please provide the requested answer.",
        DateTimeOffset.UtcNow, false)
    {
        WebUrl = "https://outlook.office.com/mail/deeplink/read/incoming"
    };
    private static AlertDecision Candidate(Message message) => new(message.ThreadId, AlertTier.Attention,
        "Response needed", "Customer needs an answer", message.WebUrl!, [message.Id]);
    private static AlertThreadRefreshResult Complete(params Message[] messages) => new(
        new MessageThread("email-thread", MessageSource.Email, "Synthetic request", messages), true);
    private static CopilotNotificationReviewResult Review(NotificationVerdict verdict, string evidence)
        => new(new(verdict, AlertTier.Attention, "Remaining follow-up", "Fresh context", [evidence]), Usage);
    private static AlertNotificationRefresher Create(AlertThreadRefreshResult response,
        Func<AlertDecision, IReadOnlyList<Message>, CancellationToken, Task<CopilotNotificationReviewResult>> review,
        List<string>? logs = null, List<(CopilotRunResult, bool)>? credits = null)
        => new(Provider((_, _, _) => Task.FromResult(response)), review,
            (usage, rejected) =>
            {
                credits?.Add((usage, rejected));
                return Task.CompletedTask;
            }, text => logs?.Add(text));
    private static IMessageProvider Provider(
        Func<string, DateTimeOffset, CancellationToken, Task<AlertThreadRefreshResult>> read)
    {
        var provider = DispatchProxy.Create<IMessageProvider, NotificationRefreshProviderProxy>();
        ((NotificationRefreshProviderProxy)provider).Read = read;
        return provider;
    }
    private static FieldInfo Field(string name) => typeof(App).GetField(
        name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static T Invoke<T>(App app, string name, params object[] arguments)
        => (T)typeof(App).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, arguments)!;
}

internal class NotificationRefreshProviderProxy : DispatchProxy
{
    public Func<string, DateTimeOffset, CancellationToken, Task<AlertThreadRefreshResult>> Read { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == nameof(IMessageProvider.RefreshThreadForAlertAsync)
            ? Read((string)args![0]!, (DateTimeOffset)args[1]!, (CancellationToken)args[2]!)
            : throw new NotSupportedException($"Unexpected provider operation: {method?.Name}");
}
