using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class MonitoringIntervalSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        TestAssert.Equal(5, new PeggySettings().MonitoringIntervalMinutes,
            "Fresh installations changed their polling interval.");
        var legacy = EncryptedStateStore.DeserializeState(
            Encoding.UTF8.GetBytes("""{"version":1,"settings":{}}"""),
            DateTimeOffset.UtcNow);
        TestAssert.Equal(5, legacy.Settings.MonitoringIntervalMinutes,
            "An existing profile without an interval did not keep five minutes.");

        foreach (var interval in new[] { 1, 5, 7, 60 })
        {
            var state = new PeggyStateDocument
            {
                Settings = new PeggySettings { MonitoringIntervalMinutes = interval }
            };
            var store = new EncryptedStateStore(Path.Combine(
                directory, $"poll-interval-{interval}.dat"));
            await store.SaveAsync(state);
            var reloaded = await store.LoadAsync();
            TestAssert.Equal(interval, reloaded.Settings.MonitoringIntervalMinutes,
                "The configured interval did not survive an encrypted-state roundtrip.");
            TestAssert.Equal(TimeSpan.FromMinutes(interval),
                MonitoringSchedule.GetPollingInterval(reloaded.Settings),
                "The configured whole-minute interval was converted incorrectly.");
            TestAssert.Equal(interval,
                PeggyStateManager.CreateSettingsForAccountSwitch(reloaded.Settings)
                    .MonitoringIntervalMinutes,
                "Account switching discarded the polling preference.");
            var clone = typeof(App).GetMethod(
                "CloneSettings", BindingFlags.NonPublic | BindingFlags.Static)!;
            var copied = (PeggySettings)clone.Invoke(null, [reloaded.Settings])!;
            TestAssert.Equal(interval, copied.MonitoringIntervalMinutes,
                "App settings cloning discarded the polling preference.");
        }

        foreach (var invalid in new[] { int.MinValue, -1, 0, 61, int.MaxValue })
        {
            var settings = new PeggySettings { MonitoringIntervalMinutes = invalid };
            TestAssert.Throws<ArgumentOutOfRangeException>(
                () => MonitoringSchedule.GetPollingInterval(settings),
                "An unsafe timer interval was accepted at runtime.");
            var state = new PeggyStateDocument { Settings = settings };
            EncryptedStateStore.NormalizeAndPrune(state, DateTimeOffset.UtcNow);
            TestAssert.Equal(5, settings.MonitoringIntervalMinutes,
                "Invalid persisted polling configuration did not recover to the default.");
        }
        foreach (var input in new[] { "1", "7", "60", " 15 " })
        {
            TestAssert.True(MonitoringSchedule.TryParseIntervalMinutes(input, out _),
                $"A supported polling interval was rejected: {input}");
        }
        foreach (var input in new string?[] { null, "", " ", "0", "-1", "61", "1.5", "1,5", "+5", "many", "999999999999" })
        {
            TestAssert.True(!MonitoringSchedule.TryParseIntervalMinutes(input, out _),
                $"An invalid polling interval was accepted: {input}");
        }

        var manager = new PeggyStateManager(new EncryptedStateStore(
            Path.Combine(directory, "polling-settings-save.dat")));
        await manager.InitializeAsync();
        var cursorBefore = JsonSerializer.Serialize(manager.State.MonitoringCursor);
        await manager.ApplySettingsAsync(
            new PeggySettings
            {
                MonitoringIntervalMinutes = 7,
                MonitoringPaused = true,
                MonitoringScheduleEnabled = false
            },
            resetOutlookMonitoring: false,
            resetTeamsMonitoring: false,
            resetCopilotSession: false,
            manager.State.CopilotSessionId,
            manager.State.CopilotSessionCredits,
            DateTimeOffset.UtcNow);
        TestAssert.True(
            manager.State.Settings.MonitoringIntervalMinutes == 7 &&
            manager.State.Settings.MonitoringPaused &&
            JsonSerializer.Serialize(manager.State.MonitoringCursor) == cursorBefore,
            "Changing cadence reset source progress or bypassed paused state.");
        TestAssert.Equal(5, PeggyDefaults.StartupLookbackMinutes,
            "The polling preference changed startup/wake lookback.");
    }

    internal static void RunDesktop()
    {
        var appTimer = typeof(App).GetField(
            "_poll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Application.Current) as DispatcherTimer;
        TestAssert.True(appTimer is not null && !appTimer.IsEnabled,
            "The polling timer must exist before Settings can be opened during startup.");

        var timer = new DispatcherTimer();
        var settings = new PeggySettings();
        App.ApplyPollingInterval(timer, settings);
        TestAssert.True(timer.Interval == TimeSpan.FromMinutes(5) && !timer.IsEnabled,
            "Applying the initial interval unexpectedly started a stopped timer.");
        timer.Start();
        try
        {
            settings.MonitoringIntervalMinutes = 1;
            App.ApplyPollingInterval(timer, settings);
            TestAssert.True(timer.Interval == TimeSpan.FromMinutes(1) && timer.IsEnabled,
                "An enabled timer did not immediately accept the shorter interval.");
            settings.MonitoringIntervalMinutes = 60;
            App.ApplyPollingInterval(timer, settings);
            TestAssert.True(timer.Interval == TimeSpan.FromMinutes(60) && timer.IsEnabled,
                "An enabled timer did not immediately accept the longer interval.");
        }
        finally
        {
            timer.Stop();
        }
        App.ApplyPollingInterval(timer, new PeggySettings { MonitoringIntervalMinutes = 7 });
        TestAssert.True(!timer.IsEnabled && timer.Interval == TimeSpan.FromMinutes(7),
            "Editing a polling interval restarted a stopped timer.");

        var invalidWindow = new SettingsWindow(
            new PeggySettings { MonitoringIntervalMinutes = 7, MonitoringScheduleEnabled = false },
            new MockMessageProvider());
        try
        {
            var input = (TextBox)invalidWindow.FindName("MonitoringIntervalText");
            TestAssert.True(
                input.Text == "7" && input.IsEnabled &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)),
                "The interval control failed to load or depends on the sleep schedule.");
            foreach (var text in new[] { "", "0", "61", "1.5", "invalid" })
            {
                input.Text = text;
                InvokeSave(invalidWindow);
                TestAssert.True(
                    invalidWindow.SavedSettings is null &&
                    ((TextBlock)invalidWindow.FindName("MonitoringIntervalError")).Visibility ==
                        Visibility.Visible,
                    "Invalid interval input was saved or failed without visible feedback.");
            }
        }
        finally
        {
            invalidWindow.Close();
        }
        foreach (var interval in new[] { 1, 7, 60 })
        {
            var original = new PeggySettings();
            var window = new SettingsWindow(original, new MockMessageProvider());
            var input = (TextBox)window.FindName("MonitoringIntervalText");
            TestAssert.Equal("5", input.Text, "The Settings UI default is not five minutes.");
            input.Text = interval.ToString(System.Globalization.CultureInfo.InvariantCulture);
            window.Loaded += (_, _) =>
                window.Dispatcher.BeginInvoke(new Action(() => InvokeSave(window)));
            TestAssert.True(
                window.ShowDialog() == true &&
                window.SavedSettings?.MonitoringIntervalMinutes == interval &&
                original.MonitoringIntervalMinutes == 5,
                "Settings did not save the interval atomically without mutating its input.");
        }
    }

    private static void InvokeSave(SettingsWindow window)
    {
        var save = typeof(SettingsWindow).GetMethod(
            "Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
        save.Invoke(window, [window, new RoutedEventArgs()]);
    }
}
