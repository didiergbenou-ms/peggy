using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;
using Forms = System.Windows.Forms;

namespace Peggy.Core.Tests;

internal static class DailyAlertSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var midnight = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.FromHours(2));
        var previous = midnight.AddTicks(-1);
        TestAssert.True(AlertDayPolicy.IsPreviousDay(previous, midnight, zone) &&
            !AlertDayPolicy.IsPreviousDay(midnight, midnight, zone),
            "Alerts must roll over at local midnight, not UTC midnight or after 24 hours.");
        TestAssert.True(!AlertDayPolicy.IsPreviousDay(midnight, midnight.AddDays(-1), zone),
            "A backward clock change must not clear future-dated alerts.");

        foreach (var offset in new[] { -8, 14 })
        {
            var custom = TimeZoneInfo.CreateCustomTimeZone(
                $"Test offset {offset}", TimeSpan.FromHours(offset), "Test", "Test");
            var boundary = new DateTimeOffset(2026, 9, 23, 0, 0, 0, custom.BaseUtcOffset);
            TestAssert.True(AlertDayPolicy.IsPreviousDay(boundary.AddSeconds(-1), boundary, custom) &&
                !AlertDayPolicy.IsPreviousDay(boundary, boundary.AddHours(1), custom),
                "Clearing used UTC dates instead of the configured local time zone.");
        }
        var springStart = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.FromHours(1));
        var springEnd = new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.FromHours(2));
        var fallStart = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2));
        var fallEnd = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1));
        TestAssert.True(springEnd - springStart == TimeSpan.FromHours(23) &&
            fallEnd - fallStart == TimeSpan.FromHours(25) &&
            AlertDayPolicy.IsPreviousDay(springStart, springEnd, zone) &&
            AlertDayPolicy.IsPreviousDay(fallStart, fallEnd, zone) &&
            !AlertDayPolicy.IsPreviousDay(fallStart, fallEnd.AddMinutes(-1), zone),
            "Day clearing must respect both short and long DST days.");

        var state = new PeggyStateDocument();
        foreach (var tier in Enum.GetValues<AlertTier>())
        {
            state.Alerts.Add(Alert(previous, tier));
            state.Alerts.Add(Alert(previous, tier, AlertStatus.Snoozed));
        }
        var today = Alert(midnight, AlertTier.Attention);
        today.FirstSeenUtc = previous.AddDays(-1);
        state.Alerts.Add(today);
        var resolved = Alert(previous, AlertTier.Critical, AlertStatus.Resolved);
        state.Alerts.Add(resolved);
        state.Settings.MonitoringPaused = true;
        state.MonitoringCursor.OutlookWatermarkUtc = previous;
        state.SeenMessages.Add(new() { MessageId = "already-seen", SeenUtc = previous });
        state.FeedbackRules.Add(new() { Summary = "Keep feedback", CreatedUtc = previous });
        state.AuditLog.Add(new() { TimestampUtc = previous });
        state.CreditHistory.Add(new() { TimestampUtc = previous, Credits = 1 });
        var retainedState = PreservedState(state);
        TestAssert.Equal(6, AlertDayPolicy.ClearPreviousDays(state, midnight, zone),
            "Daily clearing did not cover active/snoozed alerts of all three tiers.");
        TestAssert.True(state.Alerts.Take(6).All(alert =>
                alert.Status == AlertStatus.Dismissed &&
                alert.SnoozedUntilUtc is null && alert.UpdatedUtc == midnight) &&
            today.Status == AlertStatus.Active && resolved.Status == AlertStatus.Resolved &&
            resolved.UpdatedUtc == previous && state.Alerts.Count == 8,
            "Clearing must retain local history and today's renewed activity, without claiming resolution.");
        TestAssert.Equal(retainedState, PreservedState(state),
            "Clearing alerts changed unrelated settings, cursor, seen messages, feedback, audit or credits.");
        TestAssert.Equal(0, AlertDayPolicy.ClearPreviousDays(state, midnight, zone),
            "Day clearing was not idempotent.");

        var localMidnight = new DateTimeOffset(DateTime.Today).ToUniversalTime();
        var store = new EncryptedStateStore(Path.Combine(directory, "daily-alerts.dat"));
        var stored = new PeggyStateDocument();
        stored.Alerts.Add(Alert(localMidnight.AddSeconds(-1), AlertTier.Critical));
        stored.Alerts.Add(Alert(localMidnight, AlertTier.Awareness));
        stored.SeenMessages.Add(new() { MessageId = "old-source-message", SeenUtc = DateTimeOffset.UtcNow });
        await store.SaveAsync(stored);
        var manager = new PeggyStateManager(store);
        await manager.InitializeAsync();
        var reloaded = await store.LoadAsync();
        TestAssert.True(manager.State.Alerts[0].Status == AlertStatus.Dismissed &&
            reloaded.Alerts[0].Status == AlertStatus.Dismissed &&
            reloaded.Alerts[1].Status == AlertStatus.Active &&
            reloaded.SeenMessages.Single().MessageId == "old-source-message",
            "Starting Peggy did not persist prior-day clearing while preserving today's alerts and deduplication.");

        var alerts = new AlertManager(manager);
        var snoozed = Alert(localMidnight.AddMinutes(-5), AlertTier.Attention, AlertStatus.Snoozed);
        snoozed.SnoozedUntilUtc = localMidnight.AddMinutes(15);
        manager.State.Alerts.Add(snoozed);
        TestAssert.Equal(0, alerts.ReactivateDue(localMidnight.AddHours(1)).Count,
            "A yesterday snooze reappeared after the day was cleared.");
        var oldMessage = new Message("yesterday-message", MessageSource.Email, "same-thread",
            "Person", "Request", "Please reply", localMidnight.AddSeconds(-1), false)
        {
            WebUrl = "https://outlook.office.com/mail/deeplink/read/yesterday-message"
        };
        var decision = new AlertDecision("same-thread", AlertTier.Critical, "Reply needed", "A request",
            oldMessage.WebUrl, [oldMessage.Id]);
        TestAssert.Equal(0, alerts.Apply([decision], [oldMessage], localMidnight,
                observedAtUtc: localMidnight.AddSeconds(-1)).MaterialUpdates.Count,
            "An evaluation that completed after midnight recreated a previous-day alert.");
        var newlyDiscovered = alerts.Apply([decision], [oldMessage], localMidnight,
            observedAtUtc: localMidnight).MaterialUpdates.Single();
        TestAssert.True(newlyDiscovered.Status == AlertStatus.Active &&
            !AlertDayPolicy.IsPreviousDay(newlyDiscovered, localMidnight) &&
            alerts.ClearPreviousDays(localMidnight.AddMinutes(1)) == 0,
            "Daily clearing discarded an older message newly detected today.");
        var newMessage = oldMessage with { Id = "today-message", ReceivedAt = localMidnight.AddMinutes(1) };
        var newDecision = decision with { MessageIds = [newMessage.Id] };
        TestAssert.Equal(1, alerts.Apply([newDecision], [newMessage], localMidnight.AddMinutes(2),
                observedAtUtc: localMidnight.AddSeconds(-1))
                .MaterialUpdates.Count,
            "New activity today was lost just because retrieval began before midnight.");

        var noTransition = new MonitoringScheduleStatus(false, null, MonitoringScheduleTransition.None);
        TestAssert.Equal(TimeSpan.FromSeconds(10), App.GetNextScheduleEvaluationDelay(
                noTransition, new DateTime(2026, 9, 22, 23, 59, 50)),
            "All-day monitoring did not schedule a midnight evaluation.");
        var sleeping = new MonitoringScheduleStatus(true, new DateTime(2026, 9, 23, 7, 0, 0),
            MonitoringScheduleTransition.Wake);
        TestAssert.Equal(TimeSpan.FromSeconds(10), App.GetNextScheduleEvaluationDelay(
                sleeping, new DateTime(2026, 9, 22, 23, 59, 50)),
            "Scheduled sleep deferred clearing until the morning wake time.");
        TestAssert.Equal(TimeSpan.FromMinutes(1), App.GetNextScheduleEvaluationDelay(
                noTransition, new DateTime(2026, 9, 22, 12, 0, 0)),
            "Normal schedule checks no longer honor their bounded interval.");
    }

    internal static void RunDesktop(App app)
    {
        var root = Path.Combine(Path.GetTempPath(), $"peggy-daily-alert-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fields = new[] { "_state", "_alertManager", "_bubble", "_alertCenter",
            "_mascot", "_conversationWindow", "_conversation", "_tray", "_alertsMenu",
            "_creditsMenu", "_scheduleTimer", "_scheduledSleeping",
            "_alertCleanupVersion", "_savedAlertCleanupVersion" };
        var original = fields.ToDictionary(name => name, name => Field(name).GetValue(app));
        var oldLog = Environment.GetEnvironmentVariable("PEGGY_LOG_PATH");
        Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", Path.Combine(root, "test.log"));
        var statePath = Path.Combine(root, "state.dat");
        var state = new PeggyStateManager(new EncryptedStateStore(statePath));
        var alerts = new AlertManager(state);
        var bubble = new SpeechBubbleWindow();
        var center = new AlertCenterWindow();
        var mascot = new MascotWindow();
        var window = new ConversationWindow();
        var conversation = new ConversationController(window, mascot, state, alerts,
            () => throw new InvalidOperationException("No Copilot expected during daily clearing."),
            () => throw new InvalidOperationException("No WorkIQ expected during daily clearing."),
            _ => [], () => false, CancellationToken.None);
        using var tray = new Forms.NotifyIcon();
        using var alertsMenu = new Forms.ToolStripMenuItem();
        using var creditsMenu = new Forms.ToolStripMenuItem();
        var timer = new DispatcherTimer();
        Set(app, "_state", state);
        Set(app, "_alertManager", alerts);
        Set(app, "_bubble", bubble);
        Set(app, "_alertCenter", center);
        Set(app, "_mascot", mascot);
        Set(app, "_conversationWindow", window);
        Set(app, "_conversation", conversation);
        Set(app, "_tray", tray);
        Set(app, "_alertsMenu", alertsMenu);
        Set(app, "_creditsMenu", creditsMenu);
        Set(app, "_scheduleTimer", timer);
        Set(app, "_alertCleanupVersion", 0L);
        Set(app, "_savedAlertCleanupVersion", 0L);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var midnight = new DateTimeOffset(DateTime.Today).ToUniversalTime();
            var old = Alert(midnight.AddMinutes(-5), AlertTier.Critical);
            var today = Alert(now, AlertTier.Awareness);
            state.State.Alerts.AddRange([old, today]);
            state.State.Settings.MonitoringPaused = true;
            state.State.Settings.MonitoringScheduleEnabled = false;
            Set(app, "_scheduledSleeping", false);
            center.UpdateAlerts(state.State.Alerts, false);
            center.ShowCriticalBanner(old, false);
            window.ShowCritical(old, false);
            bubble.Say(old.Summary, new Point(420, 420), 60, () => throw new Exception("Stale bubble activated."));
            Complete(Invoke(app, "EvaluateMonitoringScheduleAsync", false, false, false));
            TestAssert.True(!bubble.IsVisible &&
                ((Border)center.FindName("CriticalBanner")).Visibility == Visibility.Collapsed &&
                old.Status == AlertStatus.Dismissed && today.Status == AlertStatus.Active &&
                state.State.Settings.MonitoringPaused,
                "Daily clearing depended on polling or left a stale bubble/banner while paused.");
            TestAssert.Equal("0 active, 1 awareness", ((TextBlock)center.FindName("StatusText")).Text,
                "Alert Center counts were not refreshed after clearing.");
            TestAssert.True(alertsMenu.Text?.Contains("0 active, 1 awareness", StringComparison.Ordinal) == true,
                "Tray alert count did not reflect the new day.");
            TestAssert.True(((SpeechBubbleWindow)Field("_bubble").GetValue(app)!)
                .FindName("BubbleBodyButton") is Button { IsEnabled: false },
                "Clearing left yesterday's bubble action usable.");

            state.State.Settings.ReceiveAwarenessAlerts = false;
            typeof(App).GetMethod("RefreshUi", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(app, [false]);
            TestAssert.True(((TextBlock)center.FindName("StatusText")).Text == "No active alerts" &&
                alertsMenu.Text?.Contains("0 active, 0 awareness", StringComparison.Ordinal) == true &&
                today.Status == AlertStatus.Active && !bubble.IsVisible,
                "Disabling Awareness left stale counts, deleted history or opened a notification.");
            state.State.Settings.ReceiveAwarenessAlerts = true;
            typeof(App).GetMethod("RefreshUi", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(app, [false]);
            TestAssert.True(((TextBlock)center.FindName("StatusText")).Text == "0 active, 1 awareness" &&
                !bubble.IsVisible, "Re-enabling Awareness failed to refresh counts or replayed a popup.");

            var asleep = Alert(midnight.AddDays(-3), AlertTier.Attention, AlertStatus.Snoozed);
            state.State.Alerts.Add(asleep);
            var minute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            state.State.Settings.MonitoringScheduleEnabled = true;
            state.State.Settings.MonitoringWakeMinute = (minute + 60) % 1440;
            state.State.Settings.MonitoringSleepMinute = (minute + 120) % 1440;
            Set(app, "_scheduledSleeping", true);
            Complete(Invoke(app, "EvaluateMonitoringScheduleAsync", false, false, false));
            TestAssert.True(asleep.Status == AlertStatus.Dismissed &&
                (bool)Field("_scheduledSleeping").GetValue(app)!,
                "A sleeping Peggy did not clear old snoozed alerts.");

            // Failed persistence must remain retryable even after the in-memory alert is cleared.
            File.Delete(statePath);
            Directory.CreateDirectory(statePath);
            state.State.Alerts.Add(Alert(midnight.AddMinutes(-1), AlertTier.Critical));
            TestAssert.Throws<InvalidOperationException>(
                () => Complete(Invoke(app, "ClearPreviousDayAlertsAsync", now)),
                "A failed daily state save was swallowed.");
            Directory.Delete(statePath);
            Complete(Invoke(app, "ClearPreviousDayAlertsAsync", now));
            var reload = new EncryptedStateStore(statePath).LoadAsync();
            Complete(reload);
            var persisted = reload.Result;
            TestAssert.True(persisted.Alerts.Where(alert => alert.Id != today.Id)
                    .All(alert => alert.Status == AlertStatus.Dismissed),
                "A later timer evaluation did not retry the failed daily cleanup save.");
        }
        finally
        {
            timer.Stop();
            bubble.Close();
            center.Close();
            window.Close();
            mascot.Close();
            Complete(conversation.DisposeAsync().AsTask());
            foreach (var pair in original) Field(pair.Key).SetValue(app, pair.Value);
            Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", oldLog);
            Directory.Delete(root, recursive: true);
        }
    }

    private static AlertRecord Alert(DateTimeOffset activity, AlertTier tier, AlertStatus status = AlertStatus.Active) =>
        new()
        {
            Tier = tier, Status = status, Summary = "Synthetic daily alert",
            FirstSeenUtc = activity, LastActivityUtc = activity, UpdatedUtc = activity,
            SnoozedUntilUtc = status == AlertStatus.Snoozed ? activity.AddHours(3) : null
        };

    private static string PreservedState(PeggyStateDocument state) => JsonSerializer.Serialize(new
    {
        state.Settings, state.MonitoringCursor, state.SeenMessages,
        state.FeedbackRules, state.AuditLog, state.CreditHistory
    });

    private static FieldInfo Field(string name) =>
        typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static void Set(App app, string name, object value) => Field(name).SetValue(app, value);
    private static Task Invoke(App app, string name, params object[] arguments) =>
        (Task)typeof(App).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(app, arguments)!;
    private static void Complete(Task task) => SetupStepIndicatorTests.Complete(task);
}
