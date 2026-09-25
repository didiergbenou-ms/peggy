using System.IO;
using System.Reflection;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class NotificationRefreshSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        foreach (var source in Enum.GetValues<MessageSource>())
        foreach (var tier in Enum.GetValues<AlertTier>())
        {
            var incoming = Incoming() with { Source = source };
            var candidate = Candidate(incoming) with { Tier = tier };
            foreach (var replyText in new[] { "Done", "I will investigate", "OK", "", "Still needs work" })
            {
                var reply = Reply(incoming) with { Preview = replyText };
                var logs = new List<string>();
                TestAssert.True(await Create(Complete(incoming, reply), logs).RefreshAsync(
                    candidate, [incoming], incoming.ReceivedAt, CancellationToken.None) is null,
                    "An already-sent reply did not suppress an alert irrespective of substance/tier/source.");
                TestAssert.Contains("reason=already-replied", logs.Single(),
                    "Reply suppression was not explicitly logged.");
                TestAssert.True(await Create(Complete(incoming, reply)).RefreshAsync(
                    candidate, [], incoming.ReceivedAt, CancellationToken.None) is null,
                    "Reply suppression unnecessarily depended on an in-memory baseline after restart.");
            }
            foreach (var context in new[]
            {
                Complete(incoming),
                Complete(incoming with { IsRead = true }),
                Complete(incoming, Reply(incoming) with { IsFromMe = false }),
                Complete(incoming, Reply(incoming) with { SentAtUtc = incoming.ReceivedAt }),
                Complete(incoming, Reply(incoming) with { SentAtUtc = incoming.ReceivedAt.AddMinutes(-1) }),
                Complete(incoming, Reply(incoming) with { SentAtUtc = null }),
                Complete(incoming, Reply(incoming) with
                {
                    SentAtUtc = incoming.ReceivedAt.AddMinutes(-1),
                    ReceivedAt = incoming.ReceivedAt.AddMinutes(1)
                })
            })
            {
                TestAssert.Equal(candidate, await Create(context).RefreshAsync(
                    candidate, [incoming], incoming.ReceivedAt, CancellationToken.None),
                    "No reply, another person's reply, read status or a later edit of an old reply hid the alert.");
            }
        }
        var request = Incoming() with { Source = MessageSource.Teams };
        var original = Candidate(request);
        TestAssert.True(await Create(Complete(request with { IsLikedByMe = true })).RefreshAsync(
            original, [request], request.ReceivedAt, CancellationToken.None) is null,
            "The signed-in user's like on the triggering Teams message did not suppress its alert.");
        var email = request with { Source = MessageSource.Email, IsLikedByMe = true };
        var emailCandidate = Candidate(email);
        TestAssert.Equal(emailCandidate, await Create(Complete(email)).RefreshAsync(
            emailCandidate, [email], email.ReceivedAt, CancellationToken.None),
            "An email was suppressed by the Teams-only like rule.");

        var next = request with { Id = "second-request", ReceivedAt = request.ReceivedAt.AddMinutes(2) };
        var grouped = original with { MessageIds = [request.Id, next.Id] };
        TestAssert.Equal(grouped, await Create(Complete(request with { IsLikedByMe = true }, next))
            .RefreshAsync(grouped, [request, next], request.ReceivedAt, CancellationToken.None),
            "Liking only one triggering message suppressed an unacknowledged request.");
        TestAssert.Equal(grouped, await Create(Complete(request, Reply(request), next))
            .RefreshAsync(grouped, [request, next], request.ReceivedAt, CancellationToken.None),
            "A reply before a later triggering request suppressed that newer request.");
        TestAssert.True(await Create(Complete(request, Reply(request), next with { IsLikedByMe = true }))
            .RefreshAsync(grouped, [request, next], request.ReceivedAt, CancellationToken.None) is null,
            "A reply plus a like covering all triggering messages failed to suppress.");
        TestAssert.Equal(original, await Create(Complete(request, next with { IsLikedByMe = true }))
            .RefreshAsync(original, [request], request.ReceivedAt, CancellationToken.None),
            "A like on a different message suppressed the triggering message.");

        foreach (var context in new[]
        {
            new AlertThreadRefreshResult(null, false),
            Complete(request, Reply(request)) with { IsComplete = false, Warning = "Partial page" },
            Complete(Reply(request)),
            Complete(request, request, Reply(request)),
            Complete(request, Reply(request) with { ThreadId = "other-thread" }),
            Complete(request, Reply(request) with { Source = MessageSource.Email }),
            Complete(request with { IsFromMe = true }, Reply(request)),
            Complete(request, Reply(request)) with { Thread = new MessageThread(
                "wrong-thread", MessageSource.Teams, "", [request, Reply(request)]) }
        })
        {
            TestAssert.Equal(original, await Create(context).RefreshAsync(
                original, [request], request.ReceivedAt, CancellationToken.None),
                "Missing, duplicate, incomplete or mismatched source metadata suppressed an alert.");
        }
        foreach (var error in new Exception[]
        {
            new IOException("Synthetic read failure"), new InvalidOperationException("Authentication failure"),
            new InvalidDataException("Invalid metadata"), new TimeoutException("Timeout")
        })
        {
            var check = new AlertNotificationRefresher(Provider((_, _, _) => throw error), _ => { });
            TestAssert.Equal(original, await check.RefreshAsync(
                original, [request], request.ReceivedAt, CancellationToken.None),
                "A source read failure dropped the pending alert.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            Create(Complete(request, Reply(request))).RefreshAsync(original, [request], request.ReceivedAt, cancelled.Token),
            "Shutdown cancellation was converted into an alert result.");
        var expired = new AlertNotificationRefresher(
            Provider((_, _, _) => throw new NotSupportedException("Expired budget must not fetch.")),
            _ => { }, TimeSpan.Zero);
        TestAssert.Equal(original, await expired.RefreshAsync(
            original, [request], request.ReceivedAt, CancellationToken.None), "Expired budget dropped the alert.");
        var slow = new AlertNotificationRefresher(Provider(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new NotSupportedException("Unreachable.");
        }), _ => { }, TimeSpan.FromMilliseconds(25));
        TestAssert.Equal(original, await slow.RefreshAsync(
            original, [request], request.ReceivedAt, CancellationToken.None),
            "A read timeout dropped the alert.");
        var longThread = Enumerable.Range(0, 120).Select(index =>
            request with { Id = $"context-{index}", Preview = new string('x', 1000) }).ToArray();
        TestAssert.True(await Create(Complete([request, Reply(request), ..longThread]))
            .RefreshAsync(original, [request], request.ReceivedAt, CancellationToken.None) is null,
            "Former LLM text/context limits incorrectly blocked a valid reply check.");
        var messages = new List<string>();
        await Create(Complete(request, Reply(request)), messages)
            .RefreshAsync(original, [request], request.ReceivedAt, CancellationToken.None);
        TestAssert.True(messages.All(line => !line.Contains(request.Preview) && !line.Contains(request.Id)),
            "Acknowledgment logs exposed source content or identifiers.");
        TestAssert.True(typeof(CopilotRunner).GetMethod("RunNotificationReviewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "The obsolete extra LLM operation remains available.");
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
            var refresher = Create(Complete(incoming, Reply(incoming)));
            var reviewed = ReviewPending(app, candidate, incoming, refresher);
            TestAssert.True(reviewed.Count == 0 && state.State.Alerts.Count == 0 &&
                state.State.CreditHistory.Count == 0, "Replied-to candidate created an alert or spent LLM credits.");
            AlertRecord Snooze()
            {
                var alert = manager.Apply([candidate], [incoming], DateTimeOffset.UtcNow).MaterialUpdates.Single();
                manager.Snooze(alert.Id, TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow.AddSeconds(-2));
                return alert;
            }
            var snoozed = Snooze();
            var due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync", refresher, 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Count == 0 &&
                snoozed.Status == AlertStatus.Dismissed && snoozed.SnoozedUntilUtc is null,
                "Acknowledged snooze was re-notified or marked resolved instead of dismissed.");
            SetupStepIndicatorTests.Complete(state.SaveAsync());
            var reload = new EncryptedStateStore(Path.Combine(root, "state.dat")).LoadAsync();
            SetupStepIndicatorTests.Complete(reload);
            TestAssert.Equal(AlertStatus.Dismissed, reload.Result.Alerts.Single().Status,
                "Snooze suppression did not persist.");
            snoozed = Snooze();
            due = Invoke<Task<AlertApplyResult?>>(app, "RefreshDueAlertsAsync",
                Create(new AlertThreadRefreshResult(null, false)), 41);
            SetupStepIndicatorTests.Complete(due);
            TestAssert.True(due.Result?.MaterialUpdates.Single().Id == snoozed.Id &&
                snoozed.Status == AlertStatus.Active, "Failed refresh lost a due reminder.");

            foreach (var action in new[] { "dismiss", "snooze", "resolve", "session-change", "account-switch" })
            {
                state.State.Alerts.Clear();
                var active = manager.Apply([candidate], [incoming], DateTimeOffset.UtcNow).MaterialUpdates.Single();
                var duringRead = new AlertNotificationRefresher(Provider((_, _, _) =>
                {
                    switch (action)
                    {
                        case "snooze": manager.Snooze(active.Id, TimeSpan.FromHours(1), DateTimeOffset.UtcNow); break;
                        case "session-change": Field("_monitoringGeneration").SetValue(app, 42); break;
                        case "account-switch": Field("_accountSwitching").SetValue(app, true); break;
                        default: manager.Resolve(active.Id, action == "resolve" ? AlertStatus.Resolved : AlertStatus.Dismissed,
                            DateTimeOffset.UtcNow); break;
                    }
                    return Task.FromResult(Complete(incoming));
                }), _ => { });
                TestAssert.Equal(0, ReviewPending(app, candidate, incoming, duringRead).Count,
                    $"Reply check ignored a concurrent {action}.");
                Field("_monitoringGeneration").SetValue(app, 41);
                Field("_accountSwitching").SetValue(app, false);
            }
            TestAssert.Equal(0, state.State.CreditHistory.Count,
                "WorkIQ-only acknowledgment checks incurred Copilot review credits.");
        }
        finally
        {
            foreach (var pair in oldFields) Field(pair.Key).SetValue(app, pair.Value);
            Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", oldLog);
            Directory.Delete(root, true);
        }
    }

    private static IReadOnlyList<AlertDecision> ReviewPending(
        App app, AlertDecision candidate, Message incoming, AlertNotificationRefresher refresher)
    {
        var snapshot = Invoke<object>(app, "CaptureAlertRevisions", new List<AlertDecision> { candidate });
        var task = Invoke<Task<IReadOnlyList<AlertDecision>>>(app, "RefreshPendingAlertDecisionsAsync",
            new List<AlertDecision> { candidate }, new[] { incoming }, refresher, 41, snapshot);
        SetupStepIndicatorTests.Complete(task);
        return task.Result;
    }
    private static Message Incoming() => new("incoming", MessageSource.Email, "source-thread",
        "Sender", "Synthetic subject", "Please review the request.", DateTimeOffset.UtcNow, false)
    { WebUrl = "https://outlook.office.com/mail/deeplink/read/incoming" };
    private static Message Reply(Message incoming) => incoming with
    {
        Id = "own-reply", IsFromMe = true, SentAtUtc = incoming.ReceivedAt.AddSeconds(1),
        ReceivedAt = incoming.ReceivedAt.AddSeconds(1)
    };
    private static AlertDecision Candidate(Message message) => new(message.ThreadId, AlertTier.Attention,
        "Response needed", "A request", message.WebUrl!, [message.Id]);
    private static AlertThreadRefreshResult Complete(params Message[] messages) => new(
        new MessageThread("source-thread", messages.First().Source, "", messages), true);
    private static AlertNotificationRefresher Create(AlertThreadRefreshResult response, List<string>? logs = null)
        => new(Provider((_, _, _) => Task.FromResult(response)), text => logs?.Add(text));
    private static IMessageProvider Provider(Func<string, DateTimeOffset, CancellationToken, Task<AlertThreadRefreshResult>> read)
    {
        var provider = DispatchProxy.Create<IMessageProvider, NotificationRefreshProviderProxy>();
        ((NotificationRefreshProviderProxy)provider).Read = read;
        return provider;
    }
    private static FieldInfo Field(string name) =>
        typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
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
