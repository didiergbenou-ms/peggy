extern alias setup;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using Peggy.Core;
using Peggy.Tray;
using setup::Peggy.SetupBootstrapper;
using WixToolset.BootstrapperApplicationApi;

namespace Peggy.Core.Tests;

internal static class SetupShutdownSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        await TestInstallerOwnershipAsync();
        await TestApplyBeginCancellationAfterBaExitAsync();
        await TestShutdownRoutesAsync();
        await TestSyntheticPipeAsync();
        await TestSaveBeforeExitAsync(directory);
        await TestAcceptedSettingsSaveDuringShutdownAsync(directory);
        await TestFreshStateBaselineAsync(directory);
        TestCompatibleAppGuard();
        await TestMaintenanceClosePolicyAsync();
        await NativeSetupShutdownSmokeTests.RunAsync();
    }

    private static (BurnInstaller Burn, UnifiedSetupSmokeTests.FakeApplication App,
        UnifiedSetupSmokeTests.FakeEngine Engine) CreateBurn(SetupInstanceLease lease,
        LaunchAction action = LaunchAction.Install, RelationType relation = RelationType.None)
    {
        var app = new UnifiedSetupSmokeTests.FakeApplication();
        var engine = DispatchProxy.Create<IEngine, UnifiedSetupSmokeTests.FakeEngine>();
        var fake = (UnifiedSetupSmokeTests.FakeEngine)(object)engine;
        fake.Application = app;
        var command = new BootstrapperCommand(action, Display.Full, "", 0,
            ResumeType.None, IntPtr.Zero, relation, false, "", "", "");
        return (new BurnInstaller(app, engine, command, lease), app, fake);
    }

    private static async Task TestInstallerOwnershipAsync()
    {
        var name = @"Local\Peggy.Synthetic.Setup." + Guid.NewGuid().ToString("N");
        using var firstLease = new SetupInstanceLease(name);
        using var secondLease = new SetupInstanceLease(name);
        var first = CreateBurn(firstLease);
        var second = CreateBurn(secondLease);
        first.Burn.EnsureSetupOwnership();
        await MustFailAsync(() => second.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None), "Another Peggy setup");
        TestAssert.True(second.Engine.Applies == 0 && second.Engine.Plans == 0 &&
            second.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Blocked independent setup planned/applied or allowed destructive cleanup.");
        first.Burn.LogFailure(new InvalidOperationException("Synthetic prepare failure."));
        TestAssert.True(first.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Preparation failure allowed cleanup.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await MustCancelAsync(() => first.Burn.ApplyAsync(new IntPtr(1), cancel.Token));
        }
        TestAssert.True(first.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Cancelled-before-Apply setup allowed cleanup.");
        first.Engine.PlanStatus = unchecked((int)0x80004005);
        await MustFailAsync(() => first.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None), "Plan");
        TestAssert.True(first.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Failed Plan allowed cleanup.");
        first.Engine.PlanStatus = 0;
        first.Engine.ThrowApply = true;
        await MustFailAsync(() => first.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None), "in-flight");
        TestAssert.True(first.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Synchronous Engine.Apply failure enabled post-BA implicit uninstall.");
        // A later pre-Apply failure must not enable a separate cleanup transaction.
        first.Engine.PlanStatus = unchecked((int)0x80004005);
        await MustFailAsync(() => first.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None), "Plan");
        TestAssert.True(first.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Retry enabled post-BA implicit uninstall.");
        foreach (var action in new[] { LaunchAction.Uninstall, LaunchAction.Layout })
        {
            var exempt = CreateBurn(secondLease, action,
                action == LaunchAction.Uninstall ? RelationType.Upgrade : RelationType.None);
            var operation = exempt.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None);
            await exempt.Engine.ApplyStarted.Task;
            exempt.App.Complete(new(0, ApplyRestart.None, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None,
                BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None));
            await operation;
            TestAssert.True(exempt.Engine.Applies == 1 && exempt.Engine.LastAction == action &&
                exempt.App.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
                "Related/layout Apply was skipped, changed action, or permitted implicit cleanup.");
        }
    }

    private static async Task TestApplyBeginCancellationAfterBaExitAsync()
    {
        var name = @"Local\Peggy.Synthetic.ApplyBegin." + Guid.NewGuid().ToString("N");
        using var firstLease = new SetupInstanceLease(name);
        using var nextLease = new SetupInstanceLease(name);
        var first = CreateBurn(firstLease);
        var next = CreateBurn(nextLease);
        using var cancellation = new CancellationTokenSource();
        var apply = first.Burn.ApplyAsync(new IntPtr(1), cancellation.Token);
        await first.Engine.ApplyStarted.Task;
        cancellation.Cancel();
        var begin = new ApplyBeginEventArgs(1, false);
        first.App.BeginApply(begin);
        TestAssert.True(begin.Cancel && !apply.IsCompleted,
            "ApplyBegin cancellation did not await native ApplyComplete.");
        first.App.Complete(new(unchecked((int)0x80070642), ApplyRestart.None,
            BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None));
        TestAssert.True((await apply).Cancelled, "ApplyBegin cancellation was not reported.");
        var shutdownAction = first.App.ReportShutdown();
        TestAssert.True(first.Engine.Logs.Any(line => line.Contains("burn.shutdown") &&
            line.Contains("ownsApply=True") && line.Contains("applyStarted=True")),
            "Test did not cover cancellation after the Engine.Apply ownership boundary.");

        // WiX 6 waits for BA exit (releasing its lease) before CoreCleanup.
        firstLease.Dispose();
        next.Burn.EnsureSetupOwnership();
        var nextApply = next.Burn.ApplyAsync(new IntPtr(1), CancellationToken.None);
        await next.Engine.ApplyStarted.Task;
        next.App.Complete(new(0, ApplyRestart.None, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None,
            BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None));
        TestAssert.True((await nextApply).Succeeded, "Next synthetic setup did not commit.");

        // CoreApply cancels before fAffectedMachineState; CoreCleanup can then
        // plan/apply UNINSTALL using stale eligibility after another BA commits.
        static bool SurvivesNativeCleanup(BOOTSTRAPPER_SHUTDOWN_ACTION action,
            bool affectedMachineState, bool eligibleForCleanup) =>
            action == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup || affectedMachineState || !eligibleForCleanup;
        TestAssert.True(!SurvivesNativeCleanup(BOOTSTRAPPER_SHUTDOWN_ACTION.None, false, true) &&
            SurvivesNativeCleanup(shutdownAction, false, true),
            "Cancelled ApplyBegin allowed post-BA cleanup to uninstall the next setup's installation.");
    }

    private static async Task TestMaintenanceClosePolicyAsync()
    {
        using var lease = new SetupInstanceLease(@"Local\Peggy.Synthetic.Policy." + Guid.NewGuid().ToString("N"));
        var install = CreateBurn(lease);
        await install.Burn.DetectAsync();
        TestAssert.True(install.Burn.MayCloseRunningApp, "Interactive install/upgrade cannot close Peggy.");
        install.Engine.CurrentMsiPresent = true;
        await install.Burn.DetectAsync();
        TestAssert.True(!install.Burn.MayCloseRunningApp && install.Burn.ActionLabel == "Maintain Peggy",
            "Same-version maintenance can close Peggy.");
        foreach (var action in new[] { LaunchAction.Repair, LaunchAction.Uninstall, LaunchAction.Modify, LaunchAction.Layout })
            TestAssert.True(!CreateBurn(lease, action).Burn.MayCloseRunningApp,
                "Maintenance/layout enabled automatic shutdown.");
        TestAssert.True(!CreateBurn(lease, LaunchAction.Install, RelationType.Upgrade).Burn.MayCloseRunningApp,
            "Related bundle enabled automatic shutdown.");
    }

    private static async Task TestShutdownRoutesAsync()
    {
        var diagnostics = new SetupDiagnostics(_ => { }, "synthetic");
        SetupAppShutdown Create(FakePlatform platform, int milliseconds = 2000) =>
            new(platform, diagnostics, _ => { }, TimeSpan.FromMilliseconds(milliseconds));
        var modern = new FakePlatform();
        var owned = false;
        await Create(modern).AcquireAsync(() => { owned = true; return true; }, () => { }, true, CancellationToken.None);
        TestAssert.True(owned && modern.Modern == 1 && modern.Legacy == 0 && modern.Waited,
            "Modern shutdown did not wait for exit before taking the app guard.");
        var legacy = new FakePlatform { ModernAvailable = false,
            Process = FakePlatform.Identity with { Version = "0.1.14-beta.1+build" } };
        await Create(legacy).AcquireAsync(() => true, () => { }, true, CancellationToken.None);
        TestAssert.True(legacy.Legacy == 1 && legacy.Waited, "Known shipped beta did not use graceful legacy fallback.");
        TestAssert.True(SetupAppShutdown.IsKnownLegacy("0.1.13") &&
            !SetupAppShutdown.IsKnownLegacy("0.1.14-beta.2") && !SetupAppShutdown.IsKnownLegacy("unknown"),
            "Legacy shutdown was enabled for an unverified/new version.");
        var noEndpoint = new FakePlatform { ModernAvailable = false };
        await MustFailAsync(() => Create(noEndpoint).AcquireAsync(() => true, () => { }, true, CancellationToken.None),
            "endpoint");
        TestAssert.True(noEndpoint.Legacy == 0, "Hung modern app silently fell back to weaker legacy shutdown.");
        var quiet = new FakePlatform();
        await MustFailAsync(() => Create(quiet).AcquireAsync(() => true, () => { }, false, CancellationToken.None), "maintenance");
        TestAssert.True(quiet.Modern == 0 && quiet.Legacy == 0, "Quiet/maintenance setup closed Peggy.");
        var ownershipLost = new FakePlatform();
        await MustFailAsync(() => Create(ownershipLost).AcquireAsync(() => true,
            () => throw new InvalidOperationException("Ownership lost"), true, CancellationToken.None), "Ownership");
        TestAssert.True(ownershipLost.Modern == 0, "Non-owner requested app shutdown.");
        var blocked = new FakePlatform { Running = false };
        await MustFailAsync(() => Create(blocked).AcquireAsync(() => false, () => { }, true, CancellationToken.None), "lock");
        var hanging = new FakePlatform { Hang = true };
        await MustFailAsync(() => Create(hanging, 50).AcquireAsync(() => true, () => { }, true, CancellationToken.None), "force-closed");
        TestAssert.True(hanging.Legacy == 0, "Modern timeout fell back to legacy close.");
        var lockedFiles = new FakePlatform { Unlocked = false };
        await MustFailAsync(() => Create(lockedFiles, 50).AcquireAsync(() => true, () => { }, true, CancellationToken.None), "files");
        using var cancellation = new CancellationTokenSource();
        var cancelled = new FakePlatform { AfterExit = cancellation.Cancel };
        var acquiredAfterCancel = false;
        await MustCancelAsync(() => Create(cancelled).AcquireAsync(
            () => { acquiredAfterCancel = true; return true; }, () => { }, true, cancellation.Token));
        TestAssert.True(!acquiredAfterCancel, "Cancel after app exit continued into guard/apply.");
        foreach (var changed in new[]
        {
            FakePlatform.Identity with { Pid = 42 },
            FakePlatform.Identity with { StartFileTime = 43 },
            FakePlatform.Identity with { Path = @"C:\other\peggy-tray.exe" },
            FakePlatform.Identity with { Sid = "other-owner" },
            FakePlatform.Identity with { Session = 20 }
        })
        {
            TestAssert.True(!SetupProcessValidation.Matches(FakePlatform.Identity, changed,
                FakePlatform.Identity.Path, FakePlatform.Identity.Sid, FakePlatform.Identity.Session),
                "PID reuse/path/owner/session mismatch accepted.");
            var mismatch = new FakePlatform { Invalid = true };
            await MustFailAsync(() => Create(mismatch).AcquireAsync(() => true, () => { }, true, CancellationToken.None), "mismatch");
            TestAssert.True(mismatch.Modern == 0 && mismatch.Legacy == 0, "Unverified process was asked to exit.");
        }
    }

    private static async Task TestSyntheticPipeAsync()
    {
        var name = "Peggy.Synthetic.Pipe." + Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var endpoint = new SetupShutdownPipe(name, process.SessionId,
            () => { called.TrySetResult(); return Task.CompletedTask; }, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await MustFailAsync(() => SetupShutdownPipe.RequestAsync(name, process.Id + 100000,
            () => { }, timeout.Token), "verified");
        TestAssert.True(!called.Task.IsCompleted, "Wrong server PID received shutdown.");
        var validated = false;
        TestAssert.True(await SetupShutdownPipe.RequestAsync(name, process.Id,
            () => validated = true, timeout.Token), "Synthetic endpoint rejected a valid request.");
        await called.Task.WaitAsync(timeout.Token);
        TestAssert.True(validated, "Pipe did not revalidate the retained process identity.");
        TestAssert.True(!await SetupShutdownPipe.RequestAsync(name + ".missing", process.Id,
            () => { }, timeout.Token, 30), "Missing endpoint was not reported.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await MustCancelAsync(() => SetupShutdownPipe.RequestAsync(name + ".missing", process.Id,
            () => { }, cancelled.Token));
        endpoint.Dispose();
        await endpoint.Completion.WaitAsync(timeout.Token);
        var wrongSessionCalled = false;
        var rejectedSession = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var wrongSession = new SetupShutdownPipe(name + ".session", process.SessionId + 1,
            () => { wrongSessionCalled = true; return Task.CompletedTask; }, ex => rejectedSession.TrySetResult(ex)))
        {
            try
            {
                await SetupShutdownPipe.RequestAsync(name + ".session", process.Id, () => { }, timeout.Token);
                throw new Exception("Cross-session pipe request was accepted.");
            }
            catch (IOException) { }
            TestAssert.True(!wrongSessionCalled, "Cross-session IPC invoked shutdown.");
            TestAssert.True(await rejectedSession.Task.WaitAsync(timeout.Token) is
                SetupShutdownRequestRejectedException { Reason: "client-session-mismatch" },
                "Rejected cross-session request did not log a safe reason.");
        }
        var rejectedProtocol = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidProtocolCalled = false;
        using (var invalidProtocol = new SetupShutdownPipe(name + ".protocol", process.SessionId,
            () => { invalidProtocolCalled = true; return Task.CompletedTask; }, ex => rejectedProtocol.TrySetResult(ex)))
        using (var client = new NamedPipeClientStream(".", name + ".protocol", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new byte[] { 255 }, timeout.Token);
            TestAssert.True(await rejectedProtocol.Task.WaitAsync(timeout.Token) is
                SetupShutdownRequestRejectedException { Reason: "unsupported-protocol" } && !invalidProtocolCalled,
                "Invalid protocol was accepted or silently discarded.");
        }
        using var silentServer = new NamedPipeServerStream(name + ".silent", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var responseTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var request = SetupShutdownPipe.RequestAsync(name + ".silent", process.Id, () => { }, responseTimeout.Token);
        await silentServer.WaitForConnectionAsync(timeout.Token);
        await MustCancelAsync(() => request);
    }

    private static async Task TestSaveBeforeExitAsync(string directory)
    {
        var store = new EncryptedStateStore(Path.Combine(directory, "shutdown-synthetic.dat"));
        var state = new PeggyStateDocument { PinnedAccount = "latest@example.test" };
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        var exited = false;
        var operation = GracefulShutdownSequence.RunAsync(() => drained.Task,
            async () => { await store.SaveAsync(state); saved = true; },
            () => { TestAssert.True(saved, "Exited before final save."); exited = true; });
        TestAssert.True(!exited && !saved, "Shutdown did not drain ongoing work.");
        drained.SetResult();
        await operation;
        TestAssert.True(exited && (await store.LoadAsync()).PinnedAccount == "latest@example.test",
            "Final encrypted state was not saved.");
        exited = false;
        try
        {
            await GracefulShutdownSequence.RunAsync(() => Task.CompletedTask,
                () => throw new IOException("Synthetic save failure"), () => exited = true);
            throw new Exception("Save failure ignored.");
        }
        catch (IOException) { }
        TestAssert.True(!exited, "Failed save exited and released installation protection.");
    }

    private static async Task TestAcceptedSettingsSaveDuringShutdownAsync(string directory)
    {
        foreach (var initiallyQueued in new[] { false, true })
        {
            var store = new EncryptedStateStore(Path.Combine(directory, $"settings-shutdown-{initiallyQueued}.dat"));
            var state = new PeggyStateManager(store);
            await state.LoadForSetupAsync();
            await state.SaveAsync();
            using var shutdown = new CancellationTokenSource();
            using var runtimeGate = new SemaphoreSlim(1, 1);
            var settingsTransactions = new ShutdownTransactionTracker();
            var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var voiceStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new PeggySettings { SpeechSpeed = 1.25f, SpeechTuningVersion = 3 };
            if (initiallyQueued) await runtimeGate.WaitAsync();
            async Task SaveAcceptedSettingsAsync()
            {
                await runtimeGate.WaitAsync(shutdown.Token);
                try
                {
                    // The dialog is already hidden, but voice/runtime drain and
                    // the accepted settings transaction have not completed.
                    saveStarted.SetResult();
                    await voiceStopped.Task;
                    await state.ApplySettingsAsync(accepted, false, false, false,
                        state.State.CopilotSessionId, state.State.CopilotSessionCredits,
                        DateTimeOffset.UtcNow, shutdown.Token);
                }
                finally { runtimeGate.Release(); }
            }
            var settingsSave = settingsTransactions.RunAsync(SaveAcceptedSettingsAsync);
            var stop = GracefulShutdownSequence.CancelAfterRuntimeDrainAsync(settingsTransactions, runtimeGate, shutdown);
            var repeatedDrain = settingsTransactions.StopAndDrainAsync();
            if (initiallyQueued) runtimeGate.Release();
            await saveStarted.Task;
            var newActionExecuted = false;
            async Task NewActionAsync()
            {
                await runtimeGate.WaitAsync(shutdown.Token);
                try { newActionExecuted = true; }
                finally { runtimeGate.Release(); }
            }
            var lateAction = settingsTransactions.RunAsync(NewActionAsync);
            try
            {
                TestAssert.True(!shutdown.IsCancellationRequested && !stop.IsCompleted &&
                    !repeatedDrain.IsCompleted && !settingsSave.IsCompleted,
                    "Shutdown cancelled an accepted settings save while its runtime transaction was in flight.");
            }
            finally { voiceStopped.TrySetResult(); }
            await settingsSave;
            await stop;
            await repeatedDrain;
            await lateAction;
            await state.SaveAsync();
            var persisted = await store.LoadAsync();
            TestAssert.True(shutdown.IsCancellationRequested && !newActionExecuted &&
                persisted.Settings.SpeechSpeed == 1.25f && state.State.Settings.SpeechSpeed == 1.25f,
                "Shutdown lost accepted preferences, failed cancellation, or admitted a new runtime action.");
        }
        var failedTransaction = new ShutdownTransactionTracker();
        try
        {
            await failedTransaction.RunAsync(() => throw new IOException("Synthetic settings-save failure."));
            throw new InvalidOperationException("Settings save failure was swallowed.");
        }
        catch (IOException) { }
        await failedTransaction.StopAndDrainAsync();
    }

    private static async Task TestFreshStateBaselineAsync(string directory)
    {
        var store = new EncryptedStateStore(Path.Combine(directory, "setup-baseline-synthetic.dat"));
        await store.SaveAsync(new PeggyStateDocument { PinnedAccount = "initial@example.test" });
        var baseline = new SetupStateBaseline(store);
        await baseline.RefreshAsync(CancellationToken.None);
        await store.SaveAsync(new PeggyStateDocument { PinnedAccount = "newer@example.test" });
        TestAssert.True(!baseline.Matches(await store.LoadAsync()), "Preview did not detect an account change.");
        var bytes = await File.ReadAllBytesAsync(store.Path);
        await baseline.RefreshAsync(CancellationToken.None);
        var afterRefresh = await File.ReadAllBytesAsync(store.Path);
        TestAssert.True(baseline.State.PinnedAccount == "newer@example.test" &&
            baseline.Matches(await store.LoadAsync()) &&
            bytes.SequenceEqual(afterRefresh),
            "Post-shutdown read failed to rebase or rewrote the live app's newer state.");
        await store.SaveAsync(new PeggyStateDocument { PinnedAccount = "unexpected@example.test" });
        TestAssert.True(!baseline.Matches(await store.LoadAsync()),
            "Post-guard identity changes were accepted without revalidation.");
    }

    private static void TestCompatibleAppGuard()
    {
        var prefix = @"Local\Peggy.Synthetic.App." + Guid.NewGuid().ToString("N");
        using (var legacy = new SingleInstanceGuard(prefix + ".legacy"))
        {
            TestAssert.True(legacy.TryAcquire(), "Could not own synthetic legacy guard.");
            OnOtherThread(() =>
            {
                using var next = new SingleInstanceGuard(prefix + ".user", prefix + ".legacy");
                TestAssert.True(!next.TryAcquire(), "Paired guard ignored the shipped-version mutex.");
            });
            using var probe = new SingleInstanceGuard(prefix + ".user");
            TestAssert.True(probe.TryAcquire(), "Failed compatibility acquisition leaked the per-user guard.");
        }
        using var pair = new SingleInstanceGuard(prefix + ".user", prefix + ".legacy");
        TestAssert.True(pair.TryAcquire() && pair.TryAcquire(), "Paired guard could not acquire/reuse ownership.");
        OnOtherThread(() =>
        {
            using var nextSession = new SingleInstanceGuard(prefix + ".user", prefix + ".other-session");
            using var oldApp = new SingleInstanceGuard(prefix + ".legacy");
            TestAssert.True(!nextSession.TryAcquire() && !oldApp.TryAcquire(),
                "Owned paired guard allowed another session or a legacy app through.");
        });
    }

    private static void OnOtherThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Synthetic guard thread failed.", failure);
    }

    private static async Task MustFailAsync(Func<Task> operation, string fragment)
    {
        try { await operation(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected safe failure: " + fragment);
    }

    private static async Task MustCancelAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }

    private sealed class FakePlatform : ISetupShutdownPlatform
    {
        internal static readonly SetupProcessIdentity Identity = new(12345, 67890,
            @"C:\synthetic\Peggy\peggy-tray.exe", "synthetic-owner", 9, "0.1.14-beta.2");
        internal SetupProcessIdentity Process = Identity;
        internal bool Running = true, ModernAvailable = true, Unlocked = true, Invalid, Hang, Waited;
        internal int Modern, Legacy;
        internal Action? AfterExit;
        public IReadOnlyList<SetupProcessIdentity> FindRunning() => Running ? [Process] : [];
        public void Validate(SetupProcessIdentity process)
        {
            if (Invalid) throw new InvalidOperationException("Synthetic process mismatch");
        }
        public async Task<bool> RequestModernAsync(SetupProcessIdentity process, CancellationToken ct)
        {
            Modern++;
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            return ModernAvailable;
        }
        public Task RequestLegacyAsync(SetupProcessIdentity process, CancellationToken ct)
        { Legacy++; return Task.CompletedTask; }
        public Task WaitForExitAsync(SetupProcessIdentity process, CancellationToken ct)
        { Waited = true; AfterExit?.Invoke(); return Task.CompletedTask; }
        public bool FilesUnlocked() => Unlocked;
    }
}
