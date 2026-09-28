using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AwarenessAlertSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        TestAssert.True(new PeggySettings().ReceiveAwarenessAlerts,
            "Fresh installs must preserve Awareness alerts by default.");
        var legacy = EncryptedStateStore.DeserializeState(
            Encoding.UTF8.GetBytes("""{"version":1,"settings":{}}"""), now);
        TestAssert.True(legacy.Settings.ReceiveAwarenessAlerts,
            "Existing profiles missing the new field must keep Awareness alerts.");
        var path = Path.Combine(directory, "awareness-preference.dat");
        var store = new EncryptedStateStore(path);
        var state = new PeggyStateManager(store);
        await state.InitializeAsync();
        var manager = new AlertManager(state);
        var messages = Enum.GetValues<AlertTier>().Select(tier => Message(tier.ToString(), now)).ToArray();
        var decisions = messages.Select((message, index) => Decision(message, (AlertTier)index)).ToArray();
        manager.Apply(decisions, messages, now);
        var awareness = state.State.Alerts.Single(alert => alert.Tier == AlertTier.Awareness);
        var history = JsonSerializer.Serialize(state.State.Alerts);
        var cursor = JsonSerializer.Serialize(state.State.MonitoringCursor);
        await SavePreference(state, false);
        TestAssert.True(!state.State.Settings.ReceiveAwarenessAlerts &&
            !(await store.LoadAsync()).Settings.ReceiveAwarenessAlerts &&
            JsonSerializer.Serialize(state.State.Alerts) == history &&
            JsonSerializer.Serialize(state.State.MonitoringCursor) == cursor &&
            manager.GetActiveAlerts().Count == 2,
            "Saving the preference lost persistence, changed history/cursors or failed to hide Awareness.");

        var clone = (PeggySettings)typeof(App).GetMethod(
            "CloneSettings", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [state.State.Settings])!;
        TestAssert.True(!clone.ReceiveAwarenessAlerts &&
            !PeggyStateManager.CreateSettingsForAccountSwitch(clone).ReceiveAwarenessAlerts,
            "Settings cloning or account switching reset the Awareness preference.");
        foreach (var source in Enum.GetValues<MessageSource>())
        {
            var incoming = Message($"disabled-{source}", now) with { Source = source };
            var result = manager.Apply([Decision(incoming, AlertTier.Awareness)], [incoming], now);
            TestAssert.True(result.MaterialUpdates.Count == 0 &&
                !state.State.Alerts.Any(alert => alert.ThreadId == incoming.ThreadId),
                "Disabled Awareness created a new alert.");
        }
        TestAssert.Equal(2, manager.DismissAllActive(now), "Dismiss all included hidden Awareness history.");
        TestAssert.Equal(AlertStatus.Active, awareness.Status, "Hiding Awareness dismissed its retained record.");
        await SavePreference(state, true);
        TestAssert.True(manager.GetActiveAlerts().Single().Id == awareness.Id &&
            state.State.Alerts.Count == 3,
            "Re-enabling lost existing history or replayed skipped Awareness messages.");
        await SavePreference(state, false);
        manager.Snooze(awareness.Id, TimeSpan.FromSeconds(1), now.AddMinutes(-1));
        TestAssert.Equal(0, manager.GetDueForNotification(now).Count,
            "An opted-out Awareness snooze was refreshed/reactivated.");

        var important = Message("still-important", now);
        var critical = manager.Apply([Decision(important, AlertTier.Critical)], [important], now)
            .MaterialUpdates.Single();
        TestAssert.True(manager.IsTierEnabled(AlertTier.Critical) &&
            manager.IsTierEnabled(AlertTier.Attention), "Awareness preference disabled a higher tier.");
        var downgrade = manager.Apply([Decision(important, AlertTier.Awareness)], [important], now);
        TestAssert.True(downgrade.MaterialUpdates.Count == 0 && critical.Tier == AlertTier.Awareness &&
            !manager.GetActiveAlerts().Any(alert => alert.Id == critical.Id),
            "Filtering Awareness left a stale higher-priority alert after a real downgrade.");
        var groupActive = Message("group-active", now);
        var groupHidden = groupActive with { Id = "group-hidden", ReceivedAt = now.AddSeconds(1) };
        var group = manager.Apply(
            [Decision(groupActive, AlertTier.Attention), Decision(groupHidden, AlertTier.Awareness)],
            [groupActive, groupHidden], now).MaterialUpdates.Single();
        TestAssert.True(group.Tier == AlertTier.Attention && group.MessageIds.SequenceEqual([groupActive.Id]),
            "Combining recipient decisions made a disabled Awareness message anchor a visible alert.");

        var always = Message("always-alert", now);
        state.State.FeedbackRules.Add(new()
        {
            ThreadId = always.ThreadId, Kind = FeedbackKind.AlwaysAlert, CreatedUtc = now
        });
        var awarenessDecision = Decision(always, AlertTier.Awareness);
        TestAssert.True(manager.ShouldReceiveDecision(awarenessDecision) &&
            manager.Apply([awarenessDecision], [always], now).MaterialUpdates.Single().Tier == AlertTier.Attention,
            "Awareness filtering overrode explicit promotion to Attention.");
        var less = Message("less-like-this", now);
        state.State.FeedbackRules.Add(new()
        {
            ThreadId = less.ThreadId, Kind = FeedbackKind.LessLikeThis, CreatedUtc = now
        });
        var criticalDecision = Decision(less, AlertTier.Critical);
        TestAssert.True(!manager.ShouldReceiveDecision(criticalDecision) &&
            manager.Apply([criticalDecision], [less], now).MaterialUpdates.Count == 0,
            "Awareness filtering ignored a feedback-induced downgrade.");
        manager.Snooze(state.State.Alerts.Single(alert => alert.ThreadId == always.ThreadId).Id,
            TimeSpan.FromSeconds(1), now.AddMinutes(-1));
        TestAssert.Equal(1, manager.GetDueForNotification(now).Count,
            "Disabling Awareness prevented an Attention snooze.");

        File.Delete(path);
        Directory.CreateDirectory(path);
        await TestAssert.ThrowsAsync<InvalidOperationException>(() => SavePreference(state, true),
            "A failed preference save was hidden.");
        TestAssert.True(!state.State.Settings.ReceiveAwarenessAlerts,
            "A failed save changed the live Awareness setting.");
        Directory.Delete(path);
        await SavePreference(state, true);
        TestAssert.True((await store.LoadAsync()).Settings.ReceiveAwarenessAlerts,
            "A retry did not persist the enabled Awareness preference.");
    }

    internal static void RunDesktop(App app)
    {
        foreach (var initial in new[] { false, true })
        {
            var original = new PeggySettings { ReceiveAwarenessAlerts = initial };
            var window = new SettingsWindow(original, new MockMessageProvider());
            var check = (CheckBox)window.FindName("AwarenessAlertsCheck");
            TestAssert.True(check.IsChecked == initial &&
                AutomationProperties.GetName(check) == "Receive Awareness alerts",
                "Settings did not load the accessible Awareness checkbox.");
            check.IsChecked = !initial;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() =>
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [window, new RoutedEventArgs()])));
            TestAssert.True(window.ShowDialog() == true &&
                window.SavedSettings?.ReceiveAwarenessAlerts == !initial &&
                original.ReceiveAwarenessAlerts == initial,
                "Saving Awareness mutated the unsaved input or failed to capture the checkbox.");
        }
        var unchanged = new PeggySettings();
        var cancelled = new SettingsWindow(unchanged, new MockMessageProvider());
        ((CheckBox)cancelled.FindName("AwarenessAlertsCheck")).IsChecked = false;
        cancelled.Close();
        TestAssert.True(cancelled.SavedSettings is null && unchanged.ReceiveAwarenessAlerts,
            "Cancelling Settings changed the preference.");

        var fields = new[] { "_state", "_alertManager", "_monitoringGeneration", "_accountSwitching", "_quitting" };
        var originals = fields.ToDictionary(name => name, name => Field(name).GetValue(app));
        try
        {
            var state = new PeggyStateManager();
            state.State.Settings.ReceiveAwarenessAlerts = false;
            var manager = new AlertManager(state);
            Field("_state").SetValue(app, state);
            Field("_alertManager").SetValue(app, manager);
            Field("_monitoringGeneration").SetValue(app, 71);
            Field("_accountSwitching").SetValue(app, false);
            Field("_quitting").SetValue(app, false);
            var message = Message("no-refresh", DateTimeOffset.UtcNow);
            var decision = Decision(message, AlertTier.Awareness);
            var provider = DispatchProxy.Create<IMessageProvider, NotificationRefreshProviderProxy>();
            ((NotificationRefreshProviderProxy)provider).Read = (_, _, _) =>
                throw new NotSupportedException("Opted-out Awareness must not perform WorkIQ refresh.");
            var refresher = new AlertNotificationRefresher(provider, _ => { });
            var snapshot = typeof(App).GetMethod("CaptureAlertRevisions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(app, [new[] { decision }])!;
            var task = (Task<IReadOnlyList<AlertDecision>>)typeof(App).GetMethod(
                "RefreshPendingAlertDecisionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(app, [new[] { decision }, new[] { message }, refresher, 71, snapshot])!;
            SetupStepIndicatorTests.Complete(task);
            TestAssert.Equal(0, manager.Apply(task.Result.ToArray(), [message], DateTimeOffset.UtcNow)
                .MaterialUpdates.Count, "An opted-out pending decision bypassed the final alert gate.");
        }
        finally
        {
            foreach (var item in originals) Field(item.Key).SetValue(app, item.Value);
        }
    }

    private static Task SavePreference(PeggyStateManager state, bool enabled)
        => state.ApplySettingsAsync(new PeggySettings { ReceiveAwarenessAlerts = enabled },
            false, false, false, state.State.CopilotSessionId, state.State.CopilotSessionCredits,
            DateTimeOffset.UtcNow);

    private static Message Message(string id, DateTimeOffset now)
        => new(id, MessageSource.Email, $"thread-{id}", "Synthetic sender", "Synthetic subject",
            "Synthetic request", now, false)
        { WebUrl = "https://outlook.office.com/mail/deeplink/read/synthetic" };

    private static AlertDecision Decision(Message message, AlertTier tier)
        => new(message.ThreadId, tier, "Synthetic alert", "Synthetic reason", message.WebUrl!, [message.Id]);

    private static FieldInfo Field(string name) =>
        typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
}
