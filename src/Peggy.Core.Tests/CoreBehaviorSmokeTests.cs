using System.IO;
using System.Reflection;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Core.Tests;

internal static class CoreBehaviorSmokeTests
{
    public static async Task RunAsync(string directory)
    {
        await TestStateLifecycleAsync(directory);
        await TestAtomicSettingsStateAsync(directory);
        await TestStateValidationAsync(directory);
        TestMonitoringSchedule();
        TestOpaqueIdentifiers();
        TestProviderFactory();
        TestAccountIdentityResolution();
        await TestStandaloneWorkIqSetupAsync(directory);
        TestConfiguredStatePath(directory);
        TestWorkIqTransportClassification();
        await TestWorkIqRetryPolicyAsync(directory);
        await TestStructuredTeamsMonitoringAsync(directory);
        await TestStructuredSearchTransportRetryAsync(directory);
        await TestStructuredTeamsCrossPathIdentityAsync(directory);
        await TestChannelDeltaContinuationAsync(directory);
        await TestLiveActionTransportAsync();
        TestActionPolicy();
        await TestMockProviderAsync();
    }

    private static async Task TestStateLifecycleAsync(string directory)
    {
        var path = Path.Combine(directory, "lifecycle-state.dat");
        var before = DateTimeOffset.UtcNow.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes);
        var manager = new PeggyStateManager(new EncryptedStateStore(path));
        await manager.InitializeAsync();
        var after = DateTimeOffset.UtcNow.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes);

        TestAssert.True(
            manager.State.MonitoringCursor.InitialScanStartedUtc >= before &&
            manager.State.MonitoringCursor.InitialScanStartedUtc <= after,
            "State initialization did not establish the five-minute window.");
        TestAssert.True(
            !manager.EnsureDailySession(),
            "A same-day Copilot session was unnecessarily replaced.");

        var oldSession = manager.State.CopilotSessionId;
        manager.State.CopilotSessionDate =
            DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        manager.State.CopilotSessionCredits = 9;
        TestAssert.True(
            manager.EnsureDailySession(),
            "A previous-day Copilot session was not replaced.");
        TestAssert.True(
            manager.State.CopilotSessionId != oldSession &&
            manager.State.CopilotSessionCredits == 0,
            "Daily Copilot session state did not reset atomically.");

        var now = DateTimeOffset.UtcNow;
        manager.RecordCredit("test", "model", 1.25m, 1.25m, now);
        TestAssert.Equal(
            1.25m,
            manager.GetTodayCredits(),
            "Today's AI credit total is incorrect.");
        TestAssert.Equal(
            1.25m,
            manager.State.CopilotSessionCredits,
            "Session AI credits were not updated.");

        TestAssert.True(
            !manager.RecordMonitoringFailure() &&
            !manager.RecordMonitoringFailure(),
            "Monitoring was interrupted before its failure threshold.");
        TestAssert.True(
            manager.RecordMonitoringFailure() &&
            manager.State.MonitoringInterrupted,
            "Monitoring did not transition to interrupted at the threshold.");
        TestAssert.True(
            manager.RecordMonitoringSuccess(now) &&
            manager.State.ConsecutiveMonitoringFailures == 0 &&
            !manager.State.MonitoringInterrupted,
            "Monitoring success did not restore health.");

        manager.MarkMessagesSeen(["one", "one", "two", ""], now);
        manager.MarkMessagesSeen(["one"], now.AddMinutes(1));
        TestAssert.Equal(
            2,
            manager.GetSeenMessageIds().Count,
            "Seen-message markers were not deduplicated.");
        TestAssert.Equal(
            now.AddMinutes(1),
            manager.State.SeenMessages.Single(item => item.MessageId == "one").SeenUtc,
            "An existing seen-message marker was not refreshed.");

        manager.RecordAudit(
            WorkActionKind.MarkMessageRead,
            ["target", "target", ""],
            ApprovalMethod.Click,
            "verified",
            true,
            now);
        TestAssert.Equal(
            1,
            manager.State.AuditLog[^1].TargetIds.Count,
            "Audit targets were not deduplicated.");

        await manager.SaveAsync();
        var reloaded = await new EncryptedStateStore(path).LoadAsync();
        TestAssert.Equal(
            manager.State.CopilotSessionId,
            reloaded.CopilotSessionId,
            "State manager changes did not persist.");
        TestAssert.Equal(
            2,
            reloaded.SeenMessages.Count,
            "Seen-message state did not round-trip.");

        var wakeBefore = DateTimeOffset.UtcNow.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes);
        manager.PrepareMonitoringForWake(DateTimeOffset.UtcNow);
        var wakeAfter = DateTimeOffset.UtcNow.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes);
        TestAssert.True(
            manager.State.MonitoringCursor.InitialScanStartedUtc >= wakeBefore &&
            manager.State.MonitoringCursor.InitialScanStartedUtc <= wakeAfter,
            "Scheduled wake did not rebase monitoring to the five-minute window.");
    }

    private static async Task TestAtomicSettingsStateAsync(string directory)
    {
        var path = Path.Combine(directory, "settings-state.dat");
        var manager = new PeggyStateManager(new EncryptedStateStore(path));
        await manager.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        manager.State.Alerts.Add(new AlertRecord
        {
            ThreadId = "settings-thread",
            Summary = "Original alert",
            FirstSeenUtc = now,
            LastActivityUtc = now,
            UpdatedUtc = now
        });
        manager.State.MonitoringCursor.ParticipatedChannelThreads.Add(
            new ParticipatedChannelThread
            {
                TeamId = "team",
                ChannelId = "channel",
                RootMessageId = "root",
                LastActivityUtc = now
            });
        var original = manager.State;
        var originalSessionId = original.CopilotSessionId;
        await manager.ApplySettingsAsync(
            new PeggySettings
            {
                CopilotModel = "replacement-model",
                MonitoringPaused = true
            },
            resetMonitoring: true,
            resetCopilotSession: true,
            Guid.NewGuid().ToString(),
            copilotSessionCredits: 0,
            now);
        TestAssert.True(
            !ReferenceEquals(manager.State, original) &&
            manager.State.Settings.CopilotModel == "replacement-model" &&
            manager.State.Alerts[0].Summary == "Original alert" &&
            manager.State.CopilotSessionId != originalSessionId &&
            manager.State.CopilotSessionCredits == 0 &&
            manager.State.MonitoringCursor.ParticipatedChannelThreads.Count == 1 &&
            manager.State.Settings.MonitoringPaused,
            "A persisted settings candidate was not committed atomically. " +
            $"sameState={ReferenceEquals(manager.State, original)}, " +
            $"model={manager.State.Settings.CopilotModel}, " +
            $"alerts={manager.State.Alerts.Count}, " +
            $"sessionChanged={manager.State.CopilotSessionId != originalSessionId}, " +
            $"credits={manager.State.CopilotSessionCredits}, " +
            $"threads={manager.State.MonitoringCursor.ParticipatedChannelThreads.Count}, " +
            $"paused={manager.State.Settings.MonitoringPaused}.");

        var blockedPath = Path.Combine(directory, "blocked-settings-state");
        Directory.CreateDirectory(blockedPath);
        var failingManager = new PeggyStateManager(
            new EncryptedStateStore(blockedPath));
        var liveState = failingManager.State;
        await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => failingManager.ApplySettingsAsync(
                new PeggySettings
                {
                    CopilotModel = "must-not-commit"
                },
                resetMonitoring: false,
                resetCopilotSession: false,
                liveState.CopilotSessionId,
                liveState.CopilotSessionCredits,
                DateTimeOffset.UtcNow),
            "A failed settings save unexpectedly succeeded.");
        TestAssert.True(
            ReferenceEquals(failingManager.State, liveState) &&
            failingManager.State.Settings.CopilotModel != "must-not-commit",
            "A failed settings save replaced live state.");
    }

    private static async Task TestStateValidationAsync(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new PeggyStateDocument
        {
            Settings = null!,
            MonitoringCursor = null!,
            Alerts = null!,
            FeedbackRules = null!,
            SeenMessages = null!,
            CreditHistory = null!,
            AuditLog =
            [
                new AuditLogEntry
                {
                    TimestampUtc = now.AddDays(-31)
                },
                new AuditLogEntry
                {
                    TimestampUtc = now.AddDays(-29)
                }
            ]
        };
        EncryptedStateStore.NormalizeAndPrune(state, now);
        var normalizedSettings = state.Settings
            ?? throw new InvalidOperationException(
                "State normalization did not create settings.");
        TestAssert.True(
            state.MonitoringCursor is not null &&
            state.Alerts is not null &&
            state.FeedbackRules is not null &&
            state.SeenMessages is not null &&
            state.CreditHistory is not null &&
            normalizedSettings.SpeechThreads == 8 &&
            normalizedSettings.SpeechVoiceId == 0 &&
            normalizedSettings.SpeechSpeed == 1.15F &&
            normalizedSettings.SpeechTuningVersion == 3 &&
            normalizedSettings.MonitoringScheduleEnabled &&
            normalizedSettings.MonitoringWakeMinute ==
                PeggyDefaults.DefaultWakeMinute &&
            normalizedSettings.MonitoringSleepMinute ==
                PeggyDefaults.DefaultSleepMinute,
            "State normalization left nullable collections unset.");
        normalizedSettings.SpeechThreads = 1;
        EncryptedStateStore.NormalizeAndPrune(state, now);
        TestAssert.Equal(
            1,
            normalizedSettings.SpeechThreads,
            "A post-migration speech thread override was not preserved.");
        TestAssert.Equal(
            1,
            state.AuditLog.Count,
            "State retention did not prune expired audit entries.");

        TestAssert.Throws<InvalidDataException>(
            () => EncryptedStateStore.NormalizeAndPrune(
                new PeggyStateDocument { PinnedAccount = "other@example.com" },
                now),
            "State account pinning was not enforced.");

        var corruptPath = Path.Combine(directory, "corrupt-state.dat");
        await File.WriteAllBytesAsync(corruptPath, [1, 2, 3, 4]);
        await TestAssert.ThrowsAsync<InvalidDataException>(
            () => new EncryptedStateStore(corruptPath).LoadAsync(),
            "Corrupt encrypted state was accepted.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => new EncryptedStateStore(
                Path.Combine(directory, "cancelled-state.dat"))
                .LoadAsync(cancelled.Token),
            "State loading ignored cancellation.");
    }

    private static void TestMonitoringSchedule()
    {
        var settings = new PeggySettings();
        var beforeWake = MonitoringSchedule.Evaluate(
            settings,
            new DateTime(2026, 8, 22, 6, 59, 0));
        var atWake = MonitoringSchedule.Evaluate(
            settings,
            new DateTime(2026, 8, 22, 7, 0, 0));
        var beforeSleep = MonitoringSchedule.Evaluate(
            settings,
            new DateTime(2026, 8, 22, 18, 59, 59));
        var atSleep = MonitoringSchedule.Evaluate(
            settings,
            new DateTime(2026, 8, 22, 19, 0, 0));
        TestAssert.True(
            beforeWake.IsSleeping &&
            beforeWake.NextTransition ==
                MonitoringScheduleTransition.Wake &&
            beforeWake.NextTransitionLocal ==
                new DateTime(2026, 8, 22, 7, 0, 0) &&
            !atWake.IsSleeping &&
            atWake.NextTransition ==
                MonitoringScheduleTransition.Sleep &&
            atWake.NextTransitionLocal ==
                new DateTime(2026, 8, 22, 19, 0, 0) &&
            !beforeSleep.IsSleeping &&
            atSleep.IsSleeping &&
            atSleep.NextTransitionLocal ==
                new DateTime(2026, 8, 23, 7, 0, 0),
            "The default monitoring schedule has incorrect boundaries.");

        settings.MonitoringWakeMinute = 19 * 60;
        settings.MonitoringSleepMinute = 7 * 60;
        TestAssert.True(
            !MonitoringSchedule.Evaluate(
                settings,
                new DateTime(2026, 8, 22, 23, 0, 0)).IsSleeping &&
            !MonitoringSchedule.Evaluate(
                settings,
                new DateTime(2026, 8, 23, 6, 59, 0)).IsSleeping &&
            MonitoringSchedule.Evaluate(
                settings,
                new DateTime(2026, 8, 23, 7, 0, 0)).IsSleeping,
            "A monitoring schedule crossing midnight was evaluated incorrectly.");

        settings.MonitoringScheduleEnabled = false;
        var disabled = MonitoringSchedule.Evaluate(
            settings,
            new DateTime(2026, 8, 22, 23, 0, 0));
        TestAssert.True(
            !disabled.IsSleeping &&
            disabled.NextTransitionLocal is null &&
            disabled.NextTransition ==
                MonitoringScheduleTransition.None,
            "Disabling the monitoring schedule did not keep monitoring awake.");
        settings.MonitoringPaused = true;
        TestAssert.True(
            !MonitoringSchedule.CanRunAutomatic(
                settings,
                new DateTime(2026, 8, 22, 12, 0, 0)),
            "Manual pause did not remain independent from the schedule.");
        settings.MonitoringPaused = false;
        TestAssert.True(
            MonitoringSchedule.CanRunAutomatic(
                settings,
                new DateTime(2026, 8, 22, 23, 0, 0)),
            "A disabled schedule still blocked automatic monitoring.");
        TestAssert.Equal(
            "07:00",
            MonitoringSchedule.FormatMinute(PeggyDefaults.DefaultWakeMinute),
            "Schedule time formatting is incorrect.");
        TestAssert.Throws<ArgumentException>(
            () => MonitoringSchedule.IsAwake(600, 600, 600),
            "An equal wake and sleep time was accepted.");
    }

    private static void TestOpaqueIdentifiers()
    {
        const string value = "folder/item:+/_";
        var encoded = OpaqueIdentifier.Encode("test:", value);
        TestAssert.True(
            !encoded.Contains('+') &&
            !encoded.Contains('/') &&
            OpaqueIdentifier.TryDecode(encoded, "test:", out var decoded) &&
            decoded == value,
            "Opaque identifiers did not round-trip safely.");
        TestAssert.True(
            !OpaqueIdentifier.TryDecode(encoded, "other:", out _) &&
            !OpaqueIdentifier.TryDecode("test:not-base64!", "test:", out _),
            "Invalid opaque identifiers were accepted.");
    }

    private static void TestProviderFactory()
    {
        var previous = Environment.GetEnvironmentVariable(
            "PEGGY_MESSAGE_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_MESSAGE_PROVIDER",
                "mock");
            TestAssert.True(
                MessageProviderFactory.Create() is MockMessageProvider,
                "Mock provider selection failed.");

            Environment.SetEnvironmentVariable(
                "PEGGY_MESSAGE_PROVIDER",
                "unsupported");
            TestAssert.Throws<InvalidOperationException>(
                () => MessageProviderFactory.Create(),
                "An unknown provider mode was accepted.");
        }

        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_MESSAGE_PROVIDER",
                previous);
        }
    }

    private static void TestAccountIdentityResolution()
    {
        const string smtp = "mailbox@example.com";
        const string upn = "signin@tenant.example";
        TestAssert.Equal(
            smtp,
            WorkIqMessageProvider.ResolveAuthenticatedAccount(
                smtp,
                upn,
                upn),
            "A pinned UPN did not resolve to the verified SMTP address.");
        TestAssert.Equal(
            smtp,
            WorkIqMessageProvider.ResolveAuthenticatedAccount(
                smtp,
                upn,
                smtp),
            "A pinned SMTP address was rejected.");
        TestAssert.Throws<InvalidOperationException>(
            () => WorkIqMessageProvider.ResolveAuthenticatedAccount(
                smtp,
                upn,
                "other@example.com"),
            "An unrelated authenticated identity was accepted.");
    }

    private static async Task TestStandaloneWorkIqSetupAsync(
        string directory)
    {
        var npmRoot = Path.Combine(directory, "npm");
        var executable = Path.Combine(
            npmRoot,
            "node_modules",
            "@microsoft",
            "workiq",
            "bin",
            "win-x64",
            "workiq.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        await File.WriteAllTextAsync(executable, string.Empty);
        var wrapper = Path.Combine(npmRoot, "workiq.cmd");
        await File.WriteAllTextAsync(
            wrapper,
            "@node \"%~dp0\\node_modules\\@microsoft\\workiq\\bin\\workiq.js\" %*");
        TestAssert.Equal(
            Path.GetFullPath(executable),
            WorkIqClient.ResolveConfiguredPath(wrapper),
            "The standalone npm WorkIQ wrapper did not resolve its native " +
            "executable.");
        TestAssert.True(
            string.Equals(
                "person@example.com",
                WorkIqSetupService.NormalizeAccount(
                    " PERSON@example.com "),
                StringComparison.OrdinalIgnoreCase),
            "Standalone WorkIQ account normalization failed.");
        TestAssert.Throws<WorkIqSetupException>(
            () => WorkIqSetupService.NormalizeAccount("not-an-account"),
            "An invalid standalone WorkIQ account was accepted.");

        var previousAccount = Environment.GetEnvironmentVariable(
            PeggyDefaults.WorkIqAccountEnvironmentVariable);
        var previousPath = Environment.GetEnvironmentVariable(
            "PEGGY_WORKIQ_PATH");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousMail = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_MAIL");
        var logPath = Path.Combine(directory, "workiq-setup.log");
        try
        {
            Environment.SetEnvironmentVariable(
                PeggyDefaults.WorkIqAccountEnvironmentVariable,
                null);
            var emptyState = new PeggyStateDocument();
            EncryptedStateStore.NormalizeAndPrune(
                emptyState,
                DateTimeOffset.UtcNow);
            TestAssert.Equal(
                string.Empty,
                emptyState.PinnedAccount,
                "First-run state required an account before sign-in.");

            var manager = new PeggyStateManager(
                new EncryptedStateStore(
                    Path.Combine(directory, "first-run-state.dat")));
            await manager.InitializeAsync();
            TestAssert.Equal(
                string.Empty,
                manager.State.PinnedAccount,
                "First-run state initialization pinned an account before " +
                "verification.");
            await manager.PinWorkIqAccountAsync("verified@example.com");
            TestAssert.Equal(
                "verified@example.com",
                manager.State.PinnedAccount,
                "The verified WorkIQ account was not pinned.");
            await TestAssert.ThrowsAsync<InvalidOperationException>(
                () => manager.PinWorkIqAccountAsync("other@example.com"),
                "Peggy allowed an existing account pin to change.");

            Environment.SetEnvironmentVariable(
                PeggyDefaults.WorkIqAccountEnvironmentVariable,
                "signin@tenant.example");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                "mailbox@example.com");
            Environment.SetEnvironmentVariable(
                "PEGGY_WORKIQ_PATH",
                Environment.ProcessPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "setup");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            var identity = await new WorkIqSetupService().SignInAsync(
                "signin@tenant.example");
            var commands = File.ReadAllLines(logPath);
            TestAssert.True(
                identity.Account == "signin@tenant.example" &&
                identity.Mail == "mailbox@example.com" &&
                commands.Length == 4 &&
                commands[0] == "accept-eula" &&
                commands[1].StartsWith(
                    "auth login --account signin@tenant.example",
                    StringComparison.Ordinal) &&
                commands[2].StartsWith(
                    "auth consent --account signin@tenant.example",
                    StringComparison.Ordinal) &&
                commands[3].StartsWith(
                    "fetch /me?$select=id,mail,userPrincipalName",
                    StringComparison.Ordinal),
                "Standalone WorkIQ sign-in did not perform EULA, login, " +
                "consent, and account verification in order.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                PeggyDefaults.WorkIqAccountEnvironmentVariable,
                previousAccount);
            Environment.SetEnvironmentVariable(
                "PEGGY_WORKIQ_PATH",
                previousPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                previousMail);
        }
    }

    private static void TestConfiguredStatePath(string directory)
    {
        var previous = Environment.GetEnvironmentVariable(
            "PEGGY_STATE_PATH");
        var configured = Path.Combine(directory, "configured-state.dat");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_STATE_PATH",
                configured);
            TestAssert.Equal(
                Path.GetFullPath(configured),
                new EncryptedStateStore().Path,
                "The isolated encrypted-state path was ignored.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_STATE_PATH",
                previous);
        }
    }

    private static void TestWorkIqTransportClassification()
    {
        var clientType = typeof(WorkIqMessageProvider).Assembly.GetType(
            "Peggy.Core.WorkIqClient")
            ?? throw new InvalidOperationException(
                "WorkIQ client type was not found.");
        var method = clientType.GetMethod(
            "IsTransientProcessError",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "WorkIQ transient classifier was not found.");
        var networkChanged = (bool)(method.Invoke(
            null,
            ["net::ERR_NETWORK_CHANGED"]) ?? false);
        var authenticationFailure = (bool)(method.Invoke(
            null,
            ["Authentication is required."]) ?? false);
        TestAssert.True(
            networkChanged && !authenticationFailure,
            "WorkIQ transient transport classification is unsafe.");
    }

    private static async Task TestWorkIqRetryPolicyAsync(string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var readCounter = Path.Combine(directory, "read-retry-count.txt");
        var writeCounter = Path.Combine(directory, "write-retry-count.txt");
        var client = new WorkIqClient(executable);

        var read = await client.FetchAsync<Dictionary<string, string>>(
            $"peggy-test|read|{readCounter}",
            CancellationToken.None);
        TestAssert.Equal(
            "ok",
            read["value"],
            "A transient WorkIQ read did not recover.");
        TestAssert.Equal(
            "2",
            File.ReadAllText(readCounter),
            "A transient WorkIQ read used the wrong retry count.");

        await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateAsync<Dictionary<string, string>>(
                $"peggy-test|write|{writeCounter}",
                new { subject = "approved once" },
                CancellationToken.None),
            "An ambiguous WorkIQ write was retried.");
        TestAssert.Equal(
            "1",
            File.ReadAllText(writeCounter),
            "An ambiguous WorkIQ write was executed more than once.");

        var eulaCounter = Path.Combine(directory, "eula-count.txt");
        var eula = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchAsync<Dictionary<string, string>>(
                $"peggy-test|eula|{eulaCounter}",
                CancellationToken.None),
            "A WorkIQ EULA notice was reported as malformed data.");
        TestAssert.Contains(
            "accept-eula",
            eula.Message,
            "The WorkIQ EULA error is not actionable.");
    }

    private static async Task TestChannelDeltaContinuationAsync(
        string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var logPath = Path.Combine(directory, "channel-delta-requests.log");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "channel-delta");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            var provider = new WorkIqMessageProvider(
                executablePath: executable,
                settings: new PeggySettings
                {
                    TeamsChannelWatchlist =
                    [
                        new TeamsChannelWatch
                        {
                            TeamId = "team-1",
                            ChannelId = "channel-1",
                            DisplayName = "Test team / Test channel 1"
                        },
                        new TeamsChannelWatch
                        {
                            TeamId = "team-1",
                            ChannelId = "channel-2",
                            DisplayName = "Test team / Test channel 2"
                        }
                    ]
                });
            var unreadEmail = await provider.ListUnreadAsync(
                MessageSource.Email,
                10);
            TestAssert.True(
                unreadEmail is
                [
                    {
                        WebUrl:
                            "https://outlook.office.com/mail/deeplink/read/unread-with-link"
                    }
                ] &&
                File.ReadAllLines(logPath).Any(request =>
                    request.Contains(
                        "/me/mailFolders/inbox/messages?",
                        StringComparison.Ordinal) &&
                    request.Contains(
                        "webLink",
                        StringComparison.Ordinal)),
                "Outlook source links were not requested and mapped.");
            var now = DateTimeOffset.UtcNow;
            var cursor = new MonitoringCursorState
            {
                InitialScanStartedUtc =
                    now.AddMinutes(-PeggyDefaults.StartupLookbackMinutes)
            };

            var first = await provider.GetChangesAsync(
                cursor,
                100,
                now);
            TestAssert.True(
                first.HasMore &&
                first.Cursor.ChannelContinuationPass &&
                first.Messages.Count == 2 &&
                first.Messages.All(message => message.Preview.StartsWith(
                    "First opaque page channel-",
                    StringComparison.Ordinal) &&
                    message.WebUrl?.StartsWith(
                        "https://teams.microsoft.com/",
                        StringComparison.Ordinal) == true &&
                    (message.ChannelId is "channel-1" or "channel-2")) &&
                first.Cursor.ChannelDeltaLinks.Count == 2 &&
                first.Cursor.ChannelDeltaLinks.Values.All(nextLink =>
                    nextLink.Contains(
                        "peggy-test-channel-next-channel-",
                        StringComparison.Ordinal) &&
                    nextLink.Contains(
                        "%24skiptoken=opaque-token",
                        StringComparison.Ordinal) &&
                    !nextLink.StartsWith(
                        "/v1.0/",
                        StringComparison.OrdinalIgnoreCase)),
                "The first channel delta page did not persist its opaque nextLink.");
            TestAssert.True(
                File.ReadAllLines(logPath).All(request =>
                    !request.Contains(
                        "id%20asc",
                        StringComparison.OrdinalIgnoreCase) &&
                    !request.Contains(
                        "id+asc",
                        StringComparison.OrdinalIgnoreCase)),
                "A Teams read used an unsupported secondary ID sort.");
            TestAssert.True(
                File.ReadAllLines(logPath).Any(request =>
                    request.StartsWith(
                        "do-action /search/query",
                        StringComparison.Ordinal)),
                "Structured Teams discovery did not cover chats and channels.");
            TestAssert.True(
                File.ReadAllLines(logPath)
                    .Where(request =>
                        request.Contains(
                            "/messages/delta()",
                            StringComparison.Ordinal) ||
                        request.Contains(
                            "peggy-test-channel-",
                            StringComparison.Ordinal))
                    .All(request => request.StartsWith(
                        "call-function ",
                        StringComparison.Ordinal)),
                "A channel delta request bypassed WorkIQ call-function.");
            var channelThread = await provider.GetThreadAsync(
                first.Messages.Single(message =>
                    message.ChannelId == "channel-1").ThreadId);
            TestAssert.True(
                channelThread is { Messages.Count: 2 } &&
                channelThread.Messages.Any(message =>
                    message.Preview == "Root context") &&
                channelThread.Messages.Any(message =>
                    message.Preview == "Reply context"),
                "The bounded Teams channel thread could not be retrieved.");

            var beforeSecond = File.ReadAllLines(logPath);
            var second = await provider.GetChangesAsync(
                first.Cursor,
                100,
                now.AddSeconds(1));
            TestAssert.True(
                !second.HasMore &&
                !second.Cursor.ChannelContinuationPass &&
                second.Messages.Count == 2 &&
                second.Messages.All(message => message.Preview.StartsWith(
                    "Second opaque page channel-",
                    StringComparison.Ordinal) &&
                    (message.ChannelId is "channel-1" or "channel-2")) &&
                second.Cursor.ChannelDeltaLinks.Count == 2 &&
                second.Cursor.ChannelDeltaLinks.Values.All(deltaLink =>
                    deltaLink.Contains(
                        "peggy-test-channel-delta-channel-",
                        StringComparison.Ordinal) &&
                    deltaLink.Contains(
                        "%24deltatoken=opaque-delta",
                        StringComparison.Ordinal)),
                "The channel delta continuation was not followed to deltaLink.");
            TestAssert.True(
                !first.Messages.Select(message => message.Id)
                    .Intersect(
                        second.Messages.Select(message => message.Id),
                        StringComparer.Ordinal)
                    .Any(),
                "An edited channel message retained its already-seen identifier.");
            var secondRequests = File.ReadAllLines(logPath)
                .Skip(beforeSecond.Length)
                .ToList();
            TestAssert.True(
                secondRequests.Any(request => request.Contains(
                    "peggy-test-channel-next-channel-",
                    StringComparison.Ordinal) &&
                    request.Contains(
                        "%24skiptoken=opaque-token",
                    StringComparison.Ordinal)) &&
                secondRequests.All(request =>
                    !request.Contains(
                        " /me/mailFolders/",
                        StringComparison.Ordinal) &&
                    !request.Contains(
                        " /me/chats?",
                        StringComparison.Ordinal)),
                "Channel continuation replayed Outlook or Teams chat scans.");

            var third = await provider.GetChangesAsync(
                second.Cursor,
                100,
                now.AddMinutes(1));
            TestAssert.True(
                !third.HasMore &&
                third.Messages.Count == 0 &&
                third.Cursor.ChannelDeltaLinks.Count == 2 &&
                third.Cursor.ChannelDeltaLinks.Values.All(link =>
                    link.Contains(
                        "%24deltatoken=opaque-delta-2",
                        StringComparison.Ordinal)),
                "The next polling round did not reuse the opaque deltaLink.");

            var expiredKey = third.Cursor.ChannelDeltaLinks
                .Single(pair => pair.Value.Contains(
                    "channel-1",
                    StringComparison.Ordinal))
                .Key;
            third.Cursor.ChannelDeltaLinks[expiredKey] =
                "/peggy-test-expired-token-channel-1" +
                "?%24deltatoken=expired";
            third.Cursor.ChannelPageSizes[expiredKey] = 50;
            third.Cursor.ChannelContinuationPass = true;
            third.Cursor.ChannelScanResumeKey = expiredKey;
            var recovered = await provider.GetChangesAsync(
                third.Cursor,
                100,
                now.AddMinutes(2));
            TestAssert.True(
                recovered.HasMore &&
                recovered.Cursor.ChannelDeltaLinks[expiredKey].Contains(
                    "peggy-test-channel-next-channel-1",
                    StringComparison.Ordinal) &&
                !recovered.Cursor.ChannelDeltaLinks[expiredKey].Contains(
                    "expired-token",
                    StringComparison.Ordinal),
                "An expired channel delta token was not reset.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
        }
    }

    private static async Task TestStructuredTeamsMonitoringAsync(
        string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var logPath = Path.Combine(
            directory,
            "structured-search-requests.log");
        var retryCounter = Path.Combine(
            directory,
            "structured-search-retries.txt");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousAnswer = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_ANSWER");
        var previousContextAnswer = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_CONTEXT_ANSWER");
        var previousReference = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_REFERENCE");
        var previousSearchResponse = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE");
        var previousSearchPage0 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0");
        var previousSearchPage1 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1");
        var previousSearchFailures = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT");
        var previousSearchCounter = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_COUNTER");
        var previousMail = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_MAIL");
        try
        {
            var now = DateTimeOffset.UtcNow;
            const string contextUrl =
                "https://teams.microsoft.com/l/message/" +
                "19%3Acontext-chat%40thread.v2/context-message" +
                "?context=%7B%22contextType%22%3A%22chat%22%7D";
            const string channelUrl =
                "https://teams.microsoft.com/l/message/" +
                "19%3Achannel-1%40thread.tacv2/channel-message-1";
            const string selfUrl =
                "https://teams.microsoft.com/l/message/" +
                "19%3Aself-chat%40thread.v2/self-message" +
                "?context=%7B%22contextType%22%3A%22chat%22%7D";
            var firstPage = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        searchTerms = Array.Empty<string>(),
                        hitsContainers = new[]
                        {
                            new
                            {
                                total = 1,
                                moreResultsAvailable = true,
                                hits = new[]
                                {
                                    new
                                    {
                                        hitId = "search-hit-1",
                                        rank = 1,
                                        summary =
                                            "<c0>A decision is requested.</c0>",
                                        resource = new
                                        {
                                            id = "context-message",
                                            createdDateTime =
                                                now.AddMinutes(-1),
                                            lastModifiedDateTime =
                                                now.AddMinutes(-1),
                                            subject = string.Empty,
                                            webLink = contextUrl,
                                            from = new
                                            {
                                                emailAddress = new
                                                {
                                                    name = "Structured sender",
                                                    address =
                                                        "sender@example.com"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = string.Empty,
                                                channelId = string.Empty
                                            },
                                            chatId =
                                                "19:context-chat@thread.v2"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
            var secondPage = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        searchTerms = Array.Empty<string>(),
                        hitsContainers = new[]
                        {
                            new
                            {
                                total = 2,
                                moreResultsAvailable = false,
                                hits = new[]
                                {
                                    new
                                    {
                                        hitId = "search-hit-2",
                                        rank = 2,
                                        summary =
                                            "A channel reply needs review.",
                                        resource = new
                                        {
                                            id = "channel-message-1",
                                            createdDateTime =
                                                now.AddSeconds(-30),
                                            lastModifiedDateTime =
                                                now.AddSeconds(-30),
                                            subject = "Channel topic",
                                            webLink = channelUrl,
                                            from = new
                                            {
                                                emailAddress = new
                                                {
                                                    name = "Channel sender",
                                                    address =
                                                        "sender-2@example.com"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = "team-1",
                                                channelId =
                                                    "19:channel-1@thread.tacv2"
                                            },
                                            chatId =
                                                "19:channel-1@thread.tacv2"
                                        }
                                    },
                                    new
                                    {
                                        hitId = "search-hit-self",
                                        rank = 3,
                                        summary =
                                            "The current user's own message.",
                                        resource = new
                                        {
                                            id = "self-message",
                                            createdDateTime =
                                                now.AddSeconds(-15),
                                            lastModifiedDateTime =
                                                now.AddSeconds(-15),
                                            subject = "Self message",
                                            webLink = selfUrl,
                                            from = new
                                            {
                                                emailAddress = new
                                                {
                                                    name = "Current user",
                                                    address =
                                                        "primary@example.invalid"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = string.Empty,
                                                channelId = string.Empty
                                            },
                                            chatId =
                                                "19:self-chat@thread.v2"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "structured-search");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_ANSWER",
                "Configured semantic answer.");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                firstPage);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                secondPage);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT",
                "1");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_COUNTER",
                retryCounter);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                "primary@example.invalid");
            var provider = new WorkIqMessageProvider(
                executablePath: executable,
                settings: new PeggySettings());
            var firstBatch = await provider.GetChangesAsync(
                new MonitoringCursorState(),
                100,
                now);
            var secondBatch = await provider.GetChangesAsync(
                firstBatch.Cursor,
                100,
                now.AddSeconds(1));
            var structuredMessages = firstBatch.Messages
                .Concat(secondBatch.Messages)
                .ToList();
            TestAssert.True(
                firstBatch.HasMore &&
                !secondBatch.HasMore &&
                firstBatch.Warning is null &&
                secondBatch.Warning is null &&
                structuredMessages.Count == 2 &&
                structuredMessages.Any(message =>
                    message.WebUrl == contextUrl &&
                    message.Preview == "A decision is requested.") &&
                structuredMessages.Any(message =>
                    message.WebUrl == channelUrl &&
                    message.TeamId == "team-1" &&
                    message.RootMessageId == "channel-root-1" &&
                    message.Id.StartsWith(
                        "teams-channel-message:",
                        StringComparison.Ordinal)) &&
                structuredMessages.All(message =>
                    message.WebUrl != selfUrl) &&
                firstBatch.Cursor.TeamsSearchOffset == 1 &&
                secondBatch.Cursor.TeamsSearchOffset == 0 &&
                secondBatch.Cursor.TeamsWatermarkUtc == now &&
                secondBatch.Cursor.PromptWatermarkUtc is null,
                "Structured Teams monitoring did not return exact search hits. " +
                $"firstHasMore={firstBatch.HasMore}, " +
                $"secondHasMore={secondBatch.HasMore}, " +
                $"firstWarning={firstBatch.Warning}, " +
                $"secondWarning={secondBatch.Warning}, " +
                $"messages={JsonSerializer.Serialize(structuredMessages)}, " +
                $"watermark={secondBatch.Cursor.TeamsWatermarkUtc:O}.");
            var requests = File.ReadAllLines(logPath);
            TestAssert.True(
                requests.Count(request => request.StartsWith(
                    "do-action /search/query",
                    StringComparison.Ordinal)) == 3 &&
                requests.Any(request =>
                    request.Contains(
                        "\"entityTypes\":[\"chatMessage\"]",
                        StringComparison.Ordinal) &&
                    request.Contains(
                        "\"queryString\":\"sent",
                        StringComparison.Ordinal)) &&
                requests.All(request =>
                    !request.StartsWith(
                        "ask Search Microsoft Teams",
                        StringComparison.Ordinal) &&
                    !request.Contains(
                        "/me/joinedTeams",
                        StringComparison.Ordinal) &&
                    !request.Contains(
                        "/me/chats?",
                        StringComparison.Ordinal)),
                "Structured monitoring used prompt discovery or a full chat crawl.");
            TestAssert.Equal(
                "3",
                File.ReadAllText(retryCounter),
                "Structured Teams search did not retry its transient failure.");
            TestAssert.Equal(
                "Configured semantic answer.",
                await provider.AskWorkAsync(
                    "Return the configured semantic answer."),
                "WorkIQ prompt answers were not exposed to conversations.");
            File.WriteAllText(logPath, string.Empty);
            var promptOnlyProvider = new WorkIqMessageProvider(
                executablePath: executable,
                settings: new PeggySettings());
            TestAssert.Equal(
                "Configured semantic answer.",
                await promptOnlyProvider.AskWorkAsync(
                    "Validate identity before this prompt."),
                "A direct WorkIQ prompt did not return its answer.");
            var promptRequests = File.ReadAllLines(logPath);
            TestAssert.True(
                promptRequests.Length >= 2 &&
                promptRequests[0].StartsWith(
                    "fetch /me?$select=id,mail,userPrincipalName",
                    StringComparison.Ordinal) &&
                promptRequests.Any(request => request.StartsWith(
                    "ask Validate identity before this prompt.",
                    StringComparison.Ordinal)),
                "A direct WorkIQ prompt bypassed pinned-account validation.");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_CONTEXT_ANSWER",
                "The exact Teams thread asks for a decision.");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_REFERENCE",
                contextUrl);
            File.WriteAllText(logPath, string.Empty);
            var contextProvider = new WorkIqMessageProvider(
                executablePath: executable,
                settings: new PeggySettings());
            var resolution = await contextProvider.ResolveContextAsync(
                MessageSource.Teams,
                "A decision is requested.",
                "The user was asked to decide today.",
                now);
            var contextRequests = File.ReadAllLines(logPath);
            TestAssert.True(
                resolution is
                {
                    WebUrl: contextUrl
                } &&
                resolution.Context.Contains(
                    "exact Teams thread",
                    StringComparison.Ordinal) &&
                contextRequests.Length >= 2 &&
                contextRequests[0].StartsWith(
                    "fetch /me?$select=id,mail,userPrincipalName",
                    StringComparison.Ordinal) &&
                contextRequests.Any(request =>
                    request.StartsWith(
                        "ask-context Search my Microsoft Teams",
                        StringComparison.Ordinal)),
                "Citation-aware alert context bypassed account validation or " +
                "did not recover its Teams link.");

            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT",
                "0");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                null);
            var unlinkedResponse = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        searchTerms = Array.Empty<string>(),
                        hitsContainers = new[]
                        {
                            new
                            {
                                total = 2,
                                moreResultsAvailable = false,
                                hits = new[]
                                {
                                    new
                                    {
                                        hitId = "unlinked-hit",
                                        rank = 1,
                                        summary =
                                            "A source link must be recovered.",
                                        resource = new
                                        {
                                            id = "unlinked-message",
                                            createdDateTime =
                                                now.AddMinutes(1),
                                            lastModifiedDateTime =
                                                now.AddMinutes(1),
                                            subject = "Unlinked result",
                                            webLink = string.Empty,
                                            from = new
                                            {
                                                user = new
                                                {
                                                    id = "sender-3",
                                                    displayName =
                                                        "Structured sender"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = "team-without-link",
                                                channelId =
                                                    "channel-without-link"
                                            },
                                            chatId = "channel-without-link"
                                        }
                                    },
                                    new
                                    {
                                        hitId = "stale-hit",
                                        rank = 2,
                                        summary =
                                            "A stale indexed message.",
                                        resource = new
                                        {
                                            id = "stale-message",
                                            createdDateTime =
                                                now.AddMinutes(1),
                                            lastModifiedDateTime =
                                                now.AddMinutes(1),
                                            subject = "Stale result",
                                            webLink =
                                                "https://teams.microsoft.com/" +
                                                "l/message/" +
                                                "19%3Astale-chat%40thread.v2/" +
                                                "stale-message",
                                            from = new
                                            {
                                                user = new
                                                {
                                                    id = "sender-4",
                                                    displayName =
                                                        "Stale sender"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = string.Empty,
                                                channelId = string.Empty
                                            },
                                            chatId =
                                                "19:stale-chat@thread.v2"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                unlinkedResponse);
            var withheld = await new WorkIqMessageProvider(
                        executablePath: executable,
                        settings: new PeggySettings())
                .GetChangesAsync(
                        new MonitoringCursorState(),
                        100,
                        now.AddMinutes(2));
            TestAssert.True(
                withheld.Messages.Count == 0 &&
                withheld.Warning?.Contains(
                        "exact message and thread",
                        StringComparison.OrdinalIgnoreCase) == true &&
                withheld.Cursor.TeamsWatermarkUtc is not null,
                "A linkless structured result was emitted or retried forever.");

            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                """{"value":[]}""");
            var degraded = await new WorkIqMessageProvider(
                    executablePath: executable,
                    settings: new PeggySettings())
                .GetChangesAsync(
                    new MonitoringCursorState(),
                    100,
                    now.AddMinutes(1));
            TestAssert.True(
                degraded.Messages.Count == 0 &&
                !string.IsNullOrWhiteSpace(degraded.Warning) &&
                degraded.Cursor.TeamsWatermarkUtc is null,
                "Malformed structured search output was silently accepted.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_ANSWER",
                previousAnswer);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_CONTEXT_ANSWER",
                previousContextAnswer);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_REFERENCE",
                previousReference);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE",
                previousSearchResponse);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                previousSearchPage0);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                previousSearchPage1);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT",
                previousSearchFailures);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_COUNTER",
                previousSearchCounter);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                previousMail);
        }
    }

    private static async Task TestStructuredSearchTransportRetryAsync(
        string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var logPath = Path.Combine(
            directory,
            "structured-search-transport.log");
        var counterPath = Path.Combine(
            directory,
            "structured-search-crashes.txt");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousResponse = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE");
        var previousPage0 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0");
        var previousPage1 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1");
        var previousFailureCount = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT");
        var previousCrashCount = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNT");
        var previousCrashCounter = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNTER");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "structured-search");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE",
                """
                {"value":[{"searchTerms":[],"hitsContainers":[{"total":0,"moreResultsAvailable":false,"hits":[]}]}]}
                """);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                null);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                null);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT",
                "0");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNT",
                "1");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNTER",
                counterPath);

            var now = DateTimeOffset.UtcNow;
            var batch = await new WorkIqMessageProvider(
                    executablePath: executable,
                    settings: new PeggySettings())
                .GetChangesAsync(
                    new MonitoringCursorState(),
                    100,
                    now);
            TestAssert.True(
                batch.Warning is null &&
                batch.Messages.Count == 0 &&
                batch.Cursor.TeamsWatermarkUtc == now &&
                File.ReadAllText(counterPath) == "2",
                "A transient WorkIQ MCP termination was not retried.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE",
                previousResponse);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                previousPage0);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                previousPage1);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT",
                previousFailureCount);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNT",
                previousCrashCount);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNTER",
                previousCrashCounter);
        }
    }

    private static async Task TestStructuredTeamsCrossPathIdentityAsync(
        string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var logPath = Path.Combine(
            directory,
            "structured-search-cross-path.log");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousResponse = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE");
        var previousPage0 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0");
        var previousPage1 = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1");
        var previousActivity = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_CHANNEL_ACTIVITY_UTC");
        var previousEdited = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_CHANNEL_EDITED_UTC");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var activityUtc = now.AddMinutes(-2);
            var editedUtc = now.AddMinutes(-1);
            const string sourceUrl =
                "https://teams.microsoft.com/l/message/" +
                "channel/root-edit-channel-1";
            var response = JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        searchTerms = Array.Empty<string>(),
                        hitsContainers = new[]
                        {
                            new
                            {
                                total = 1,
                                moreResultsAvailable = false,
                                hits = new[]
                                {
                                    new
                                    {
                                        hitId = "cross-path-hit",
                                        rank = 1,
                                        summary =
                                            "First opaque page channel-1",
                                        resource = new
                                        {
                                            id = "root-edit-channel-1",
                                            createdDateTime = activityUtc,
                                            lastModifiedDateTime = editedUtc,
                                            subject = "Opaque delta test",
                                            webLink = sourceUrl,
                                            from = new
                                            {
                                                user = new
                                                {
                                                    id = "other-user",
                                                    displayName = "Other user"
                                                }
                                            },
                                            channelIdentity = new
                                            {
                                                teamId = "team-1",
                                                channelId = "channel-1"
                                            },
                                            chatId = "channel-1"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "channel-delta");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE",
                response);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                null);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                null);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_CHANNEL_ACTIVITY_UTC",
                activityUtc.ToString("O"));
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_CHANNEL_EDITED_UTC",
                editedUtc.ToString("O"));

            var provider = new WorkIqMessageProvider(
                executablePath: executable,
                settings: new PeggySettings
                {
                    TeamsChannelWatchlist =
                    [
                        new TeamsChannelWatch
                        {
                            TeamId = "team-1",
                            ChannelId = "channel-1",
                            DisplayName =
                                "Test team / Test channel 1"
                        }
                    ]
                });
            var first = await provider.GetChangesAsync(
                new MonitoringCursorState
                {
                    InitialScanStartedUtc = now.AddMinutes(-5)
                },
                1,
                now);
            var second = await provider.GetChangesAsync(
                first.Cursor,
                100,
                now.AddSeconds(1));
            TestAssert.True(
                first.HasMore &&
                first.Messages.Count == 1 &&
                second.Messages.Count == 1 &&
                first.Messages[0].Id == second.Messages[0].Id &&
                second.Messages[0].IsWatchlistedChannel,
                "Structured search and channel delta used different message " +
                "identities across monitoring batches.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE",
                previousResponse);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_0",
                previousPage0);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_1",
                previousPage1);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_CHANNEL_ACTIVITY_UTC",
                previousActivity);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_CHANNEL_EDITED_UTC",
                previousEdited);
        }
    }

    private static async Task TestChatActivityTimestampsAsync(
        string directory)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var logPath = Path.Combine(
            directory,
            "chat-activity-requests.log");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousAnswer = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_ANSWER");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_ANSWER",
                """{"items":[]}""");
            var now = DateTimeOffset.UtcNow;

            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "chat-metadata");
            var metadataOnly = await new WorkIqMessageProvider(
                    executablePath: executable,
                    settings: new PeggySettings())
                .GetChangesAsync(
                    new MonitoringCursorState(),
                    100,
                    now);
            TestAssert.True(
                metadataOnly.Messages.Count == 0 &&
                File.ReadAllLines(logPath).Any(request =>
                    request.StartsWith(
                        "fetch /me/chats?",
                        StringComparison.Ordinal) &&
                    request.Contains(
                        "lastMessagePreview%2fcreatedDateTime%20gt",
                        StringComparison.OrdinalIgnoreCase)) &&
                !File.ReadAllLines(logPath).Any(request =>
                    request.Contains(
                        "/me/chats/old-chat/messages?",
                        StringComparison.Ordinal)),
                "An old Teams chat outside the monitoring window was fetched.");

            File.WriteAllText(logPath, string.Empty);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "chat-edit");
            var actualEdit = await new WorkIqMessageProvider(
                    executablePath: executable,
                    settings: new PeggySettings())
                .GetChangesAsync(
                    new MonitoringCursorState(),
                    100,
                    now.AddSeconds(1));
            TestAssert.True(
                actualEdit.Messages is
                [
                    {
                        WebUrl: not null
                    } message
                ] &&
                message.WebUrl.Contains(
                    "/l/message/",
                    StringComparison.Ordinal) &&
                !message.WebUrl.Contains(
                    "/l/chat/",
                    StringComparison.Ordinal),
                "A real Teams content edit was not retained with an exact " +
                "message link.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_ANSWER",
                previousAnswer);
        }
    }

    private static async Task TestLiveActionTransportAsync()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The test executable path is unavailable.");
        var previousScenario = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SCENARIO");
        var previousLog = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_LOG");
        var previousMail = Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_MAIL");
        var logPath = Path.Combine(
            Path.GetTempPath(),
            $"peggy-action-requests-{Guid.NewGuid():N}.log");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                "actions");
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                logPath);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                "mailbox@example.com");
            var provider = new WorkIqMessageProvider(
                executablePath: executable);
            var actions = new[]
            {
                new WorkActionRequest
                {
                    Kind = WorkActionKind.MarkMessageRead,
                    TargetId = OpaqueIdentifier.Encode(
                        "email-message:",
                        "mark-id")
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateEmailDraft,
                    Subject = "Status",
                    Body = "Exact email body",
                    Recipients = ["person@example.com"]
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendEmail,
                    Subject = "Status",
                    Body = "Exact email body",
                    Recipients = ["person@example.com"]
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateReplyDraft,
                    RelatedMessageId = OpaqueIdentifier.Encode(
                        "email-message:",
                        "original"),
                    Body = "Exact reply body"
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendReply,
                    RelatedMessageId = OpaqueIdentifier.Encode(
                        "email-message:",
                        "original"),
                    Body = "Exact reply body"
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendChatMessage,
                    TargetId = OpaqueIdentifier.Encode(
                        "teams-thread:",
                        "chat-1"),
                    Body = "Exact Teams message"
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendChatMessage,
                    TargetId = BuildChannelThreadId(
                        "team-1",
                        "channel-1",
                        "root-1"),
                    Body = "Exact channel reply"
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateCalendarEvent,
                    Subject = "Created event",
                    Body = "Created agenda",
                    Recipients = ["person@example.com"],
                    StartUtc =
                        new DateTimeOffset(2030, 1, 2, 10, 0, 0, TimeSpan.Zero),
                    EndUtc =
                        new DateTimeOffset(2030, 1, 2, 11, 0, 0, TimeSpan.Zero)
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.UpdateCalendarEvent,
                    TargetId = OpaqueIdentifier.Encode(
                        "calendar-event:",
                        "event-updated"),
                    Subject = "Updated event",
                    Body = "Updated agenda",
                    Recipients = ["person@example.com"],
                    StartUtc =
                        new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero),
                    EndUtc =
                        new DateTimeOffset(2030, 1, 2, 13, 0, 0, TimeSpan.Zero)
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CancelCalendarEvent,
                    TargetId = OpaqueIdentifier.Encode(
                        "calendar-event:",
                        "event-cancelled")
                },
                new WorkActionRequest
                {
                    Kind = WorkActionKind.DeleteMessage,
                    TargetId = OpaqueIdentifier.Encode(
                        "email-message:",
                        "delete-id")
                }
            };

            foreach (var action in actions)
            {
                var result = await provider.ExecuteActionAsync(action);
                TestAssert.True(
                    result.Success && result.Verified,
                    $"The no-side-effect live transport test failed for {action.Kind}.");
            }

            TestAssert.Equal(
                "draft_created",
                (await provider.DraftReplyAsync(
                    OpaqueIdentifier.Encode(
                        "email-message:",
                        "original"),
                    "Exact reply body")).Status,
                "The live draft wrapper did not report verified success.");

            var sentQueries = File.ReadAllLines(logPath)
                .Where(line => line.StartsWith(
                    "fetch /me/messages?$filter=",
                    StringComparison.Ordinal))
                .ToList();
            TestAssert.Equal(
                4,
                sentQueries.Count,
                "Sent-item verification did not poll after delayed visibility.");
            TestAssert.True(
                sentQueries.All(query =>
                {
                    var sentTime = query.IndexOf(
                        "sentDateTime%20ge",
                        StringComparison.Ordinal);
                    var secondary = Math.Max(
                        query.IndexOf(
                            "subject%20eq",
                            StringComparison.Ordinal),
                        query.IndexOf(
                            "conversationId%20eq",
                            StringComparison.Ordinal));
                    return sentTime >= 0 && secondary > sentTime;
                }),
                "A sent-item verification filter is incompatible with Graph ordering.");
            var mutations = File.ReadAllLines(logPath)
                .Where(line =>
                    line.StartsWith("create ", StringComparison.Ordinal) ||
                    line.StartsWith("update ", StringComparison.Ordinal) ||
                    line.StartsWith("do-action ", StringComparison.Ordinal) ||
                    line.StartsWith("delete ", StringComparison.Ordinal))
                .ToList();
            TestAssert.Equal(
                12,
                mutations.Count,
                "The fake action suite observed a missing or duplicate mutation.");
            TestAssert.Equal(
                2,
                mutations.Count(line => line.EndsWith(
                    "/createReply",
                    StringComparison.Ordinal)),
                "Reply draft creation ran an unexpected number of times.");
            TestAssert.True(
                mutations.Count(line => line == "do-action /me/sendMail") == 1 &&
                mutations.Count(line => line.EndsWith(
                    "/reply",
                    StringComparison.Ordinal) &&
                    !line.EndsWith(
                        "/createReply",
                        StringComparison.Ordinal)) == 1 &&
                mutations.Count(line => line == "create /me/events") == 1 &&
                mutations.Count(line => line ==
                    "update /me/events/event-updated") == 1 &&
                mutations.Count(line => line ==
                    "delete /me/messages/delete-id") == 1,
                "A user-approved external mutation was not one-shot.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SCENARIO",
                previousScenario);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_LOG",
                previousLog);
            Environment.SetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_MAIL",
                previousMail);
            File.Delete(logPath);
        }
    }

    private static string BuildChannelThreadId(
        string teamId,
        string channelId,
        string rootId)
        => "teams-channel-thread:" +
           $"{EncodePart(teamId)}:{EncodePart(channelId)}:{EncodePart(rootId)}";

    private static string EncodePart(string value)
        => Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void TestActionPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        var requests = new[]
        {
            new WorkActionRequest
            {
                Kind = WorkActionKind.MarkMessageRead,
                TargetId = "message"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateEmailDraft,
                Subject = "Status",
                Body = "Body",
                Recipients = ["person@example.com"]
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendEmail,
                Subject = "Status",
                Body = "Body",
                Recipients = ["person@example.com"]
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateReplyDraft,
                RelatedMessageId = "message",
                Body = "Reply"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendReply,
                RelatedMessageId = "message",
                Body = "Reply"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendChatMessage,
                TargetId = "thread",
                Body = "Chat"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateCalendarEvent,
                Subject = "Review",
                Body = "Private agenda",
                Recipients = ["person@example.com"],
                StartUtc = now,
                EndUtc = now.AddHours(1)
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.UpdateCalendarEvent,
                TargetId = "event",
                Subject = "Updated event",
                Body = "Updated agenda"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CancelCalendarEvent,
                TargetId = "event"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateTask,
                Subject = "Task"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.UpdateTask,
                TargetId = "task",
                Body = "Updated notes"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.CompleteTask,
                TargetId = "task"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteMessage,
                TargetId = "message"
            },
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteTask,
                TargetId = "task"
            }
        };

        foreach (var request in requests)
        {
            var normalized = WorkActionPolicy.NormalizeAndValidate(request);
            TestAssert.True(
                normalized.ExactDescription.Length > 0,
                $"Action {request.Kind} has no approval preview.");
            TestAssert.True(
                normalized.IsDestructive ==
                (normalized.TypedConfirmationPhrase.Length > 0),
                $"Action {request.Kind} has an incorrect confirmation policy.");
        }

        var calendar = requests.Single(request =>
            request.Kind == WorkActionKind.CreateCalendarEvent);
        TestAssert.Contains(
            "Private agenda",
            calendar.ExactDescription,
            "Calendar approval omitted attendee-visible content.");
        var cancellation = requests.Single(request =>
            request.Kind == WorkActionKind.CancelCalendarEvent);
        TestAssert.Contains(
            "Cancelled by the organizer.",
            cancellation.ExactDescription,
            "Calendar cancellation approval omitted its outbound comment.");

        var recipients = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendEmail,
                Subject = "Status",
                Body = "Body",
                Recipients =
                [
                    "PERSON@example.com",
                    "person@example.com",
                    "other@example.com"
                ]
            });
        TestAssert.Equal(
            2,
            recipients.Recipients.Count,
            "Email recipients were not normalized and deduplicated.");

        TestAssert.Throws<InvalidOperationException>(
            () => WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendEmail,
                    Subject = "Status",
                    Body = "   ",
                    Recipients = ["person@example.com"]
                }),
            "A blank email body passed action validation.");
        TestAssert.Throws<InvalidOperationException>(
            () => WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateCalendarEvent,
                    Subject = "Invalid",
                    StartUtc = now,
                    EndUtc = now
                }),
            "A zero-duration calendar event passed validation.");
        TestAssert.Throws<InvalidOperationException>(
            () => WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendReply,
                    RelatedMessageId = "message",
                    Body = "Reply",
                    Recipients = ["wrong-recipient@example.com"]
                }),
            "A reply action accepted recipients that execution would ignore.");
        TestAssert.Throws<InvalidOperationException>(
            () => WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateTask,
                    Subject = "Unsupported list task",
                    TaskList = "Custom list"
                }),
            "An unsupported custom task list reached approval.");

        var task = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.UpdateTask,
                TargetId = "task",
                Body = "Exact replacement notes"
            });
        TestAssert.Contains(
            "Exact replacement notes",
            task.ExactDescription,
            "Task approval omitted the replacement notes.");
        var reply = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendReply,
                RelatedMessageId = "message",
                Body = "Reply"
            });
        TestAssert.Contains(
            "Reply-To or sender",
            reply.ExactDescription,
            "Reply approval misstated its recipients.");

        var taskClient = typeof(WorkIqMessageProvider).Assembly.GetType(
            "Peggy.Core.OutlookTaskClient")
            ?? throw new InvalidOperationException(
                "Outlook task client type was not found.");
        var bodyMatches = taskClient.GetMethod(
            "TaskBodyMatches",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Task body verifier was not found.");
        TestAssert.True(
            (bool)(bodyMatches.Invoke(
                null,
                ["line one\r\nline two", "line one\nline two"]) ?? false) &&
            !(bool)(bodyMatches.Invoke(
                null,
                ["different", "expected"]) ?? true),
            "Task note verification is not exact.");

        var contentMatches = typeof(WorkIqMessageProvider).GetMethod(
            "ContentEquals",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Full action-content verifier was not found.");
        var prefix = new string('x', 800);
        TestAssert.True(
            (bool)(contentMatches.Invoke(
                null,
                [prefix + "expected", prefix + "expected"]) ?? false) &&
            !(bool)(contentMatches.Invoke(
                null,
                [prefix + "different", prefix + "expected"]) ?? true),
            "Action verification ignored content beyond preview length.");

        var replyContentMatches = typeof(WorkIqMessageProvider).GetMethod(
            "ReplyContentMatches",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Reply-content verifier was not found.");
        TestAssert.True(
            (bool)(replyContentMatches.Invoke(
                null,
                ["Approved reply Quoted history", "Approved reply"]) ??
                false) &&
            !(bool)(replyContentMatches.Invoke(
                null,
                ["Unapproved prefix Approved reply", "Approved reply"]) ??
                true),
            "Reply verification accepted content before the approved reply.");

        var isNotFound = typeof(WorkIqMessageProvider).GetMethod(
            "IsNotFound",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "WorkIQ not-found classifier was not found.");
        TestAssert.True(
            (bool)(isNotFound.Invoke(
                null,
                [
                    new WorkIqClient.WorkIqRequestException(
                        404,
                        null,
                        "not found")
                ]) ?? false) &&
            !(bool)(isNotFound.Invoke(
                null,
                [
                    new WorkIqClient.WorkIqRequestException(
                        403,
                        null,
                        "account not found")
                ]) ?? true),
            "A non-404 WorkIQ failure was treated as verified deletion.");

        var canOutrank = typeof(WorkIqMessageProvider).GetMethod(
            "CanUnreadChatsOutrank",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Unread chat cutoff helper was not found.");
        var oldest = DateTimeOffset.UtcNow.AddHours(-1);
        TestAssert.True(
            (bool)(canOutrank.Invoke(
                null,
                [oldest.AddMinutes(30), oldest]) ?? false) &&
            !(bool)(canOutrank.Invoke(
                null,
                [oldest.AddMinutes(-30), oldest]) ?? true),
            "Unread chat waves can stop before newer candidates are read.");
    }

    private static async Task TestMockProviderAsync()
    {
        var pagedProvider = new MockMessageProvider();
        var pageNow = DateTimeOffset.UtcNow;
        var pageOne = await pagedProvider.GetChangesAsync(
            new MonitoringCursorState(),
            1,
            pageNow);
        var pageTwo = await pagedProvider.GetChangesAsync(
            pageOne.Cursor,
            1,
            pageNow.AddSeconds(1));
        TestAssert.True(
            pageOne.HasMore &&
            !pageTwo.HasMore &&
            pageOne.Messages.Count == 1 &&
            pageTwo.Messages.Count == 1 &&
            pageOne.Messages[0].Id != pageTwo.Messages[0].Id,
            "Mock monitoring skipped or repeated a limited page.");

        var provider = new MockMessageProvider();
        var all = await provider.ListUnreadAsync(null, 100);
        var emails = await provider.ListUnreadAsync(
            MessageSource.Email,
            100);
        TestAssert.Equal(4, all.Count, "Mock unread listing is incomplete.");
        TestAssert.Equal(2, emails.Count, "Mock source filtering failed.");
        TestAssert.Equal(
            1,
            (await provider.ListUnreadAsync(null, 1)).Count,
            "Mock unread limits were not enforced.");

        var now = DateTimeOffset.UtcNow;
        var first = await provider.GetChangesAsync(
            new MonitoringCursorState(),
            100,
            now);
        TestAssert.Equal(
            now.AddMinutes(-PeggyDefaults.StartupLookbackMinutes),
            first.Cursor.InitialScanStartedUtc,
            "Mock monitoring did not use the exact startup lookback.");
        TestAssert.True(
            first.Messages.All(message =>
                message.ReceivedAt >
                now.AddMinutes(-PeggyDefaults.StartupLookbackMinutes)),
            "Mock monitoring returned data outside the startup window.");
        var second = await provider.GetChangesAsync(
            first.Cursor,
            100,
            now.AddSeconds(1));
        TestAssert.Equal(
            0,
            second.Messages.Count,
            "Mock monitoring repeated an unchanged batch.");

        TestAssert.True(
            await provider.GetThreadAsync("t-100") is { Messages.Count: 2 },
            "Mock thread retrieval failed.");
        TestAssert.Equal(
            1,
            (await provider.ListUpcomingEventsAsync(
                now,
                now.AddDays(1),
                20)).Count,
            "Mock calendar listing failed.");
        await TestAssert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => provider.ListUpcomingEventsAsync(now, now, 20),
            "Mock calendar accepted an invalid range.");
        TestAssert.Equal(
            1,
            (await provider.ListTasksAsync(false, 20)).Count,
            "Mock task listing failed.");
        TestAssert.Equal(
            1,
            (await provider.ListChannelsAsync()).Count,
            "Mock channel listing failed.");

        var marked = await provider.MarkReadAsync("m-1001");
        TestAssert.True(marked.Success, "Mock mark-read failed.");
        TestAssert.True(
            !(await provider.MarkReadAsync("missing")).Success,
            "Mock mark-read reported success for a missing message.");
        TestAssert.Equal(
            "draft_created",
            (await provider.DraftReplyAsync("m-1001", "Reply")).Status,
            "Mock reply draft failed.");
        TestAssert.Equal(
            "message_not_found",
            (await provider.DraftReplyAsync("missing", "Reply")).Status,
            "Mock reply draft accepted a missing message.");

        foreach (var action in new[]
                 {
                     new WorkActionRequest
                     {
                         Kind = WorkActionKind.CreateEmailDraft,
                         Subject = "Draft",
                         Body = "Body",
                         Recipients = ["person@example.com"]
                     },
                     new WorkActionRequest
                     {
                         Kind = WorkActionKind.SendEmail,
                         Subject = "Send",
                         Body = "Body",
                         Recipients = ["person@example.com"]
                     },
                     new WorkActionRequest
                     {
                         Kind = WorkActionKind.CreateReplyDraft,
                         RelatedMessageId = "m-1001",
                         Body = "Reply"
                     },
                     new WorkActionRequest
                     {
                         Kind = WorkActionKind.SendReply,
                         RelatedMessageId = "m-1001",
                         Body = "Reply"
                     },
                     new WorkActionRequest
                     {
                         Kind = WorkActionKind.SendChatMessage,
                         TargetId = "t-200",
                         Body = "Chat"
                     }
                 })
        {
            var result = await provider.ExecuteActionAsync(action);
            TestAssert.True(
                result.Success && result.Verified,
                $"Mock action {action.Kind} did not verify.");
        }

        var createdEvent = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateCalendarEvent,
                Subject = "Created event",
                StartUtc = now.AddHours(4),
                EndUtc = now.AddHours(5)
            });
        TestAssert.True(
            createdEvent.Success &&
            (await provider.ListUpcomingEventsAsync(
                now,
                now.AddDays(1),
                20)).Any(item =>
                item.Id == createdEvent.ResultId &&
                item.Subject == "Created event"),
            "Mock event creation was not observable.");
        var updatedEvent = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.UpdateCalendarEvent,
                TargetId = createdEvent.ResultId,
                Subject = "Updated event"
            });
        var cancelledEvent = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.CancelCalendarEvent,
                TargetId = createdEvent.ResultId
            });
        TestAssert.True(
            updatedEvent.Success &&
            cancelledEvent.Success &&
            (await provider.ListUpcomingEventsAsync(
                now,
                now.AddDays(1),
                20)).Any(item =>
                item.Id == createdEvent.ResultId &&
                item.Subject == "Updated event" &&
                item.IsCancelled),
            "Mock event update or cancellation was not observable.");

        var createdTask = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.CreateTask,
                Subject = "Created task"
            });
        var updatedTask = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.UpdateTask,
                TargetId = createdTask.ResultId,
                Subject = "Updated task"
            });
        var completedTask = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.CompleteTask,
                TargetId = createdTask.ResultId
            });
        TestAssert.True(
            updatedTask.Success &&
            completedTask.Success &&
            (await provider.ListTasksAsync(true, 20)).Any(item =>
                item.Id == createdTask.ResultId &&
                item.Title == "Updated task" &&
                item.IsCompleted),
            "Mock task mutations were not observable.");
        var deletedTask = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteTask,
                TargetId = createdTask.ResultId
            });
        TestAssert.True(
            deletedTask.Success &&
            !(await provider.ListTasksAsync(true, 20)).Any(item =>
                item.Id == createdTask.ResultId),
            "Mock task deletion was not observable.");

        var deletedMessage = await provider.ExecuteActionAsync(
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteMessage,
                TargetId = "m-2001"
            });
        TestAssert.True(
            deletedMessage.Success &&
            !(await provider.ListUnreadAsync(null, 100)).Any(item =>
                item.Id == "m-2001"),
            "Mock message deletion was not observable.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => provider.ListUnreadAsync(null, 20, cancelled.Token),
            "Mock provider ignored cancellation.");
    }
}
