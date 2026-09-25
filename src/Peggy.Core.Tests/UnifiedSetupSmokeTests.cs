extern alias setup;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Peggy.Core;
using setup::Peggy.SetupBootstrapper;
using Peggy.Tray;
using WixToolset.BootstrapperApplicationApi;

namespace Peggy.Core.Tests;

internal static class UnifiedSetupSmokeTests
{
    private static readonly WorkIqAccountIdentity Identity = new("person@example.test", "person@example.test", "person@example.test")
    { PrincipalId = "verified-person", EulaAcceptedVersion = "1.0.0", DisplayName = "Test Person" };

    internal static async Task RunIsolatedAsync()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "unified-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RunAsync(directory);
            await SetupExperienceSmokeTests.RunAsync();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                try
                {
                    SetupExperienceSmokeTests.RunDesktop();
                    RunDesktop();
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { application.Shutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await completion.Task;
            thread.Join();
        }
        finally { await DeleteSyntheticDirectoryAsync(directory); }
    }

    internal static async Task DeleteSyntheticDirectoryAsync(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                if (attempt == 0) Console.WriteLine("Retrying transient synthetic-directory cleanup lock.");
                await Task.Delay(100);
            }
        }
    }

    internal static async Task RunAsync(string directory)
    {
        await SetupDiagnosticsSmokeTests.RunAsync(directory);
        await SetupLogCollectorSmokeTests.RunAsync(directory);
        foreach (var display in new[] { Display.None, Display.Passive, Display.Embedded })
            TestAssert.True(!SetupPolicy.ShowWindow(display, RelationType.None) &&
                !SetupPolicy.ShouldOnboard(LaunchAction.Install, display, RelationType.None, false) &&
                !SetupPolicy.IsUpgrade(LaunchAction.Install, display, RelationType.None, false, true),
                "Quiet/passive setup enabled onboarding UI.");
        foreach (var relation in new[] { RelationType.Upgrade, RelationType.ChainPackage, RelationType.Update })
            TestAssert.True(!SetupPolicy.ShowWindow(Display.Full, relation) &&
                !SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, relation, false) &&
                !SetupPolicy.IsUpgrade(LaunchAction.Install, Display.Full, relation, false, true),
                "Related bundle execution enabled UI or authentication.");
        foreach (var action in new[] { LaunchAction.Uninstall, LaunchAction.UnsafeUninstall, LaunchAction.Repair, LaunchAction.Layout, LaunchAction.Modify })
            TestAssert.True(!SetupPolicy.ShouldOnboard(action, Display.Full, RelationType.None, false) &&
                !SetupPolicy.IsUpgrade(action, Display.Full, RelationType.None, false, true),
                "Maintenance enabled onboarding.");
        TestAssert.True(SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, RelationType.None, false) &&
            !SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, RelationType.None, true),
            "Fresh install/upgrade and same-product maintenance were not distinguished.");
        await TestBurnBridgeAsync();
        await SetupShutdownSmokeTests.RunAsync(directory);
        await SetupAppLauncherSmokeTests.RunAsync();
        await TestStateCompletionAsync(directory);
    }

    private static async Task TestBurnBridgeAsync()
    {
        var entryPoint = typeof(BurnInstaller).Assembly.EntryPoint!;
        TestAssert.True(entryPoint.GetCustomAttribute<STAThreadAttribute>() is null,
            "The native Burn entry point must initialize MTA before its separate WPF STA thread starts.");
        var application = new FakeApplication();
        var engine = DispatchProxy.Create<IEngine, FakeEngine>();
        var fake = (FakeEngine)(object)engine;
        fake.Application = application;
        var command = new BootstrapperCommand(LaunchAction.Install, Display.Full, "", 0,
            ResumeType.None, IntPtr.Zero, RelationType.None, false, "", "", "");
        using var lease = new SetupInstanceLease(@"Local\Peggy.Test.Setup." + Guid.NewGuid().ToString("N"));
        var burn = new BurnInstaller(application, engine, command, lease);
        var owner = new IntPtr(1);
        try { await burn.ApplyAsync(IntPtr.Zero, CancellationToken.None); throw new Exception("Null owner was accepted."); }
        catch (ArgumentException) { }
        fake.Downgrade = true;
        try { await burn.DetectAsync(); throw new Exception("Downgrade was accepted."); }
        catch (InvalidOperationException ex) { TestAssert.True(ex.Message.Contains("Downgrade", StringComparison.OrdinalIgnoreCase), "Wrong downgrade failure."); }
        TestAssert.True(fake.Plans == 0 && fake.Applies == 0, "Downgrade reached Plan/Apply.");
        fake.Downgrade = false;
        foreach (var (version, present, upgrade) in new (string?, bool, bool)[]
        {
            ("0.9.0", false, true), ("1.0.0", false, false),
            ("0.9.0", true, false), (null, false, false)
        })
        {
            fake.RelatedVersion = version;
            fake.CurrentMsiPresent = present;
            await burn.DetectAsync();
            TestAssert.True(burn.IsUpgrade == upgrade,
                "Only a detected older bundle, without the current MSI, may enable automatic upgrade handoff.");
        }
        using var cancellation = new CancellationTokenSource();
        var first = burn.ApplyAsync(owner, cancellation.Token);
        await fake.ApplyStarted.Task;
        try { await burn.ApplyAsync(owner, CancellationToken.None); throw new Exception("Concurrent Apply accepted."); }
        catch (InvalidOperationException) { }
        cancellation.Cancel();
        var progress = new ExecuteProgressEventArgs("PeggyMsi", 50, 50, false);
        application.ReportProgress(progress);
        TestAssert.True(progress.Cancel && !first.IsCompleted && fake.Applies == 1,
            "Cancellation exited before rollback or allowed duplicate Apply.");
        var rollback = new ExecutePackageBeginEventArgs("PeggyMsi", false,
            ActionState.Uninstall, INSTALLUILEVEL.None, false, false);
        application.BeginPackage(rollback);
        var rollbackProgress = new ExecuteProgressEventArgs("PeggyMsi", 60, 60, false);
        application.ReportProgress(rollbackProgress);
        TestAssert.True(!rollback.Cancel && !rollbackProgress.Cancel && !first.IsCompleted,
            "Cancellation interrupted Burn-owned rollback or returned before ApplyComplete.");
        TestAssert.True(application.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Rollback enabled a separate post-BA implicit uninstall.");
        var complete = new ApplyCompleteEventArgs(unchecked((int)0x80070642), ApplyRestart.None,
            BOOTSTRAPPER_APPLYCOMPLETE_ACTION.Restart, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.Restart);
        application.Complete(complete);
        TestAssert.True((await first).Cancelled && complete.Action == BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None,
            "Cancelled Apply restarted Windows or reported success.");
        fake.ApplyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = burn.ApplyAsync(owner, CancellationToken.None);
        await fake.ApplyStarted.Task;
        application.Complete(new(0, ApplyRestart.RestartRequired,
            BOOTSTRAPPER_APPLYCOMPLETE_ACTION.Restart, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.Restart));
        var result = await retry;
        TestAssert.True(result.Succeeded && result.RestartRequired && burn.ExitCode == 3010 && fake.Applies == 2,
            "Retry or restart-required return code was lost.");
        TestAssert.True(application.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Normal setup enabled a separate post-BA implicit uninstall or automatic restart.");
        burn.LogFailure(new InvalidOperationException("Post-install synthetic failure",
            new IOException("Synthetic underlying cause")), preserveExitCode: true);
        TestAssert.True(burn.ExitCode == 3010 && fake.Logs.Any(line =>
            line.Contains("Post-install synthetic failure") && line.Contains("Synthetic underlying cause")) &&
            fake.Logs.Any(line => line.Contains("burn.shutdown") && line.Contains("exitCode=3010")),
            "Detailed failure logging changed the committed exit code or lost shutdown/error context.");
        var layoutApplication = new FakeApplication();
        var layoutEngine = DispatchProxy.Create<IEngine, FakeEngine>();
        var layoutFake = (FakeEngine)(object)layoutEngine;
        layoutFake.Application = layoutApplication;
        layoutFake.Downgrade = true;
        var layoutCommand = new BootstrapperCommand(LaunchAction.Layout, Display.None, "", 0,
            ResumeType.None, IntPtr.Zero, RelationType.None, false, @"C:\layout-test", "", "");
        var layout = new BurnInstaller(layoutApplication, layoutEngine, layoutCommand);
        var layoutTask = layout.ApplyAsync(owner, CancellationToken.None);
        await layoutFake.ApplyStarted.Task;
        TestAssert.True(layout.IsLayout && !layout.RequiresWindow && !layout.RequiresOnboarding &&
            !layout.ChecksRequirements && layoutFake.LastAction == LaunchAction.Layout &&
            layoutCommand.LayoutDirectory == @"C:\layout-test" && layoutFake.VariableWrites == 0,
            "Layout was converted to install, enabled setup, or lost its output directory.");
        layoutApplication.Complete(new(0, ApplyRestart.None,
            BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None, BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None));
        TestAssert.True((await layoutTask).Succeeded && layout.ExitCode == 0, "Layout did not complete successfully.");
        TestAssert.True(layoutApplication.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup,
            "Layout allowed shutdown to clean up an installed bundle.");
    }

    private static async Task TestStateCompletionAsync(string directory)
    {
        var store = new EncryptedStateStore(Path.Combine(directory, "unified-state.dat"));
        var document = new PeggyStateDocument();
        document.Settings.SpeechEnabled = true;
        document.Settings.OutlookScope = OutlookMonitoringScope.Off;
        await store.SaveAsync(document);
        var original = await File.ReadAllBytesAsync(store.Path);
        var state = new PeggyStateManager(store);
        await state.LoadForSetupAsync();
        var afterLoad = await File.ReadAllBytesAsync(store.Path);
        TestAssert.True(original.SequenceEqual(afterLoad),
            "Setup's read-only load rewrote state.");
        await WorkIqSetupCompletion.ApplyIdentityAsync(state, Identity);
        await WorkIqSetupCompletion.ApplyIdentityAsync(state, Identity);
        var saved = await store.LoadAsync();
        TestAssert.True(saved.PinnedPrincipalId == Identity.PrincipalId &&
            saved.WorkIqEulaAcceptedVersion == Identity.EulaAcceptedVersion &&
            saved.Settings.SpeechEnabled && saved.Settings.OutlookScope == OutlookMonitoringScope.Off,
            "Completion did not persist verified identity/EULA or changed upgrade settings.");
        await WorkIqSetupCompletion.ApplyIdentityAsync(state,
            new("other@example.test", "other@example.test", "other@example.test")
            { PrincipalId = "other-person", EulaAcceptedVersion = "1.0.0" });
        TestAssert.True(state.State.PinnedPrincipalId == "other-person" && state.State.Settings.SpeechEnabled,
            "Shared account switching lost general settings.");
        try
        {
            await WorkIqSetupCompletion.ApplyIdentityAsync(state, Identity with { EulaAcceptedVersion = "" });
            throw new Exception("Unaccepted identity was persisted.");
        }
        catch (InvalidOperationException) { }
        var blockedDirectory = Path.Combine(directory, "blocked-state-parent");
        await File.WriteAllTextAsync(blockedDirectory, "A file cannot be used as a state directory.");
        var failing = new PeggyStateManager(new EncryptedStateStore(Path.Combine(blockedDirectory, "state.dat")));
        try
        {
            await WorkIqSetupCompletion.ApplyIdentityAsync(failing, Identity);
            throw new Exception("Persistence failure was ignored.");
        }
        catch (IOException) { }
        TestAssert.True(string.IsNullOrWhiteSpace(failing.State.PinnedAccount),
            "A failed state write left an in-memory setup identity pinned.");
    }

    internal static void RunDesktop()
    {
        using (var owner = PeggyBootstrapperApplication.CreateHiddenOwner())
        {
            TestAssert.True(owner.Handle != IntPtr.Zero && !IsWindowVisible(owner.Handle),
                "Unattended Burn operations need a valid owner HWND without displaying another window.");
        }
        TestFreshInstallAndFinish();
        TestCancelAndRestart();
        TestUpgradeAndPersistenceFailure();
        TestVerifiedUpgradeAutoFinish();
        TestUpgradeHandoffExclusions();
        TestUpgradeCancellation();
        TestUpgradeVerificationRetry();
        TestLaunchFailureRetry();
        TestFinishStageDiagnostics();
        TestAuthenticationDiagnostics();
        TestRefreshedAccountAndMaintenance();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private static void TestFreshInstallAndFinish()
    {
        var host = new FakeHost { Ready = false };
        var setup = new FakeWorkIq();
        var window = CreateWindow(host, setup);
        try
        {
            var count = Application.Current.Windows.Count;
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            var handle = new WindowInteropHelper(window).Handle;
            Complete(window.CheckLocalAsync());
            Complete(window.CheckCopilotAsync());
            TestAssert.True(setup.VersionChecks == 0 && !Find<GroupBox>(window, "MicrosoftConnectionGroup").IsEnabled &&
                !Find<Button>(window, "InstallerApplyButton").IsEnabled,
                "Authentication/local WorkIQ started before the MSI commit.");
            Complete(window.InstallRequirementAsync("node"));
            TestAssert.True(host.RequirementInstalls == 1 && Find<Button>(window, "InstallerApplyButton").IsEnabled,
                "Explicit dependency installation did not enable installing Peggy.");
            host.Result = new(false, false, false, "Simulated failure — Retry");
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(!Find<GroupBox>(window, "MicrosoftConnectionGroup").IsEnabled && host.Applies == 1,
                "Failed installation enabled onboarding.");
            host.Result = new(true, false, false, "Installed");
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(new WindowInteropHelper(window).Handle == handle &&
                Application.Current.Windows.Count == count &&
                Find<GroupBox>(window, "MicrosoftConnectionGroup").IsEnabled,
                "Setup replaced its window or failed to enable onboarding after commit.");
            TestAssert.True(Find<TextBlock>(window, "OperationStatus").Text.StartsWith(
                    "Peggy is installed.", StringComparison.Ordinal),
                "Fresh installation left the completed saved-account lookup displayed as running.");
            Connect(window);
            setup.VerifiedIdentity = Identity with { PrincipalId = "unexpected-person" };
            Click(window, "FinishButton");
            TestAssert.True(host.Completions == 0 && !window.CanFinish, "Finish persisted an unexpected account.");
            setup.VerifiedIdentity = Identity;
            Connect(window);
            Click(window, "FinishButton");
            TestAssert.True(host.Completions == 1 && host.Launched && !window.IsVisible,
                "Verified Finish did not persist and launch exactly once.");
        }
        finally { window.Close(); }
    }

    private static void TestCancelAndRestart()
    {
        var pending = new TaskCompletionSource<SetupInstallResult>();
        var host = new FakeHost { ApplyOperation = _ => pending.Task };
        var setup = new FakeWorkIq();
        var window = CreateWindow(host, setup);
        try
        {
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            var apply = window.ApplyInstallerAsync();
            host.Report("Installing...", 42);
            TestAssert.True(Find<ProgressBar>(window, "InstallerProgress").Value == 42, "Burn progress was not displayed.");
            window.Close();
            TestAssert.True(window.IsVisible && host.Cancels == 1 && !apply.IsCompleted,
                "Closing during Apply terminated the installer before rollback.");
            pending.SetResult(new(false, true, false, "Cancelled after rollback"));
            Complete(apply);
            host.ApplyOperation = null;
            host.Result = new(true, false, true, "Restart Windows before setup continues.");
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(setup.VersionChecks == 0 && !Find<GroupBox>(window, "MicrosoftConnectionGroup").IsEnabled &&
                Find<Button>(window, "FinishButton").IsEnabled,
                "Restart-required install started account setup or trapped the user.");
            Click(window, "FinishButton");
            TestAssert.True(!host.Launched && host.Completions == 0, "Restart Finish launched Peggy or saved an account.");
        }
        finally { window.Close(); }
    }

    private static void TestUpgradeAndPersistenceFailure()
    {
        var host = UpgradeHost();
        host.FailCompletion = true;
        var setup = new FakeWorkIq();
        var window = CreateWindow(host, setup);
        try
        {
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(window.CanFinish && setup.SignIns == 0, "A valid upgraded account required another sign-in.");
            TestAssert.True(window.IsVisible && !host.Launched && host.Completions == 0 &&
                Find<TextBlock>(window, "OperationStatus").Text.Contains("persist"),
                "Persistence failure launched Peggy or claimed setup was complete.");
            host.FailCompletion = false;
            Click(window, "FinishButton");
            TestAssert.True(host.Launched && host.Completions == 1, "Persistence retry could not complete.");
            var entries = host.Logs.Select(SetupDiagnosticsSmokeTests.Parse).ToList();
            TestAssert.True(entries.Any(entry => entry.GetProperty("stage").GetString() == "finish.save-setup" &&
                    entry.GetProperty("outcome").GetString() == "failed") &&
                entries.Any(entry => entry.GetProperty("stage").GetString() == "finish.save-setup" &&
                    entry.GetProperty("outcome").GetString() == "completed") &&
                entries.Any(entry => entry.GetProperty("stage").GetString() == "finish.launch" &&
                    entry.GetProperty("outcome").GetString() == "completed"),
                "Finish persistence failure/retry and launch were not retained in diagnostics.");
        }
        finally { window.Close(); }
    }

    private static FakeHost UpgradeHost() => new()
    {
        IsUpgrade = true,
        InitialState = new PeggyStateDocument
        {
            PinnedAccount = Identity.Account, PinnedMailbox = Identity.Mail!,
            PinnedUserPrincipalName = Identity.UserPrincipalName!, PinnedPrincipalId = Identity.PrincipalId,
            WorkIqEulaAcceptedVersion = Identity.EulaAcceptedVersion
        },
        SavedIdentity = Identity
    };

    private static void TestVerifiedUpgradeAutoFinish()
    {
        var host = UpgradeHost();
        var verified = new TaskCompletionSource<WorkIqAccountIdentity?>();
        host.VerifySavedOperation = _ => verified.Task;
        var setup = new FakeWorkIq();
        var window = CreateWindow(host, setup);
        try
        {
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            var apply = window.ApplyInstallerAsync();
            Complete(window.ApplyInstallerAsync());
            Complete(window.FinishAsync());
            TestAssert.True(host.Completions == 0 && !host.Launched && window.IsVisible,
                "Upgrade completed before pending saved-account verification.");
            verified.SetResult(Identity);
            Complete(apply);
            Complete(window.FinishAsync());
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(host.Completions == 1 && host.Launches == 1 && host.LaunchCloses == 1 &&
                !window.IsVisible && setup.SignIns == 0 && setup.MailProbes == 1 && setup.Verifications == 1,
                "Verified upgrade did not await checks and persist/launch exactly once without another click.");
            TestAssert.True(host.Logs.Any(line => line.Contains("finish.automatic-upgrade")),
                "Automatic handoff was missing from diagnostics.");
        }
        finally { window.Close(); }

        var mailOff = UpgradeHost();
        mailOff.InitialState.Settings.OutlookScope = OutlookMonitoringScope.Off;
        var mailOffSetup = new FakeWorkIq();
        var mailOffWindow = CreateWindow(mailOff, mailOffSetup);
        try
        {
            Complete(mailOffWindow.PrepareInstallerAsync());
            Complete(mailOffWindow.ApplyInstallerAsync());
            TestAssert.True(mailOff.Launched && mailOffSetup.MailProbes == 0,
                "Upgrade required Inbox permission despite Outlook monitoring being Off.");
        }
        finally { mailOffWindow.Close(); }
    }

    private static void TestUpgradeHandoffExclusions()
    {
        foreach (var reason in new[] { "fresh", "unknown", "unknown-verified", "eula", "changed-eula",
            "different", "restart", "maintenance", "missing-saved", "cancelled-result" })
        {
            var host = UpgradeHost();
            if (reason == "fresh") host.IsUpgrade = false;
            if (reason == "unknown") host.InitialState.PinnedPrincipalId = "";
            if (reason == "unknown-verified") host.SavedIdentity = Identity with { PrincipalId = "" };
            if (reason == "eula") host.InitialState.WorkIqEulaAcceptedVersion = "";
            if (reason == "changed-eula") host.SavedIdentity = Identity with { EulaAcceptedVersion = "2.0.0" };
            if (reason == "different") host.InitialState.PinnedPrincipalId = "different-person";
            if (reason == "restart") host.Result = new(true, false, true, "Restart required");
            if (reason == "maintenance") host.Onboard = false;
            if (reason == "missing-saved") host.SavedIdentity = null;
            if (reason == "cancelled-result") host.Result = new(true, true, false, "Committed with cancellation");
            var window = CreateWindow(host, new FakeWorkIq());
            try
            {
                window.Show();
                SetupStepIndicatorTests.DrainDispatcher();
                Complete(window.ApplyInstallerAsync());
                TestAssert.True(window.IsVisible && host.Completions == 0 && host.Launches == 0,
                    $"Automatic handoff ran for {reason}.");
                window.Close();
                TestAssert.True(host.Launches == 0 && host.LaunchCloses == 0,
                    $"Closing setup launched Peggy for {reason}.");
            }
            finally { window.Close(); }
        }
    }

    private static void TestUpgradeCancellation()
    {
        foreach (var phase in new[] { "apply", "local", "saved-verification", "mail", "final-verification", "save", "launch" })
        {
            var host = UpgradeHost();
            var setup = new FakeWorkIq();
            var readiness = new FakeReadiness();
            var pending = new TaskCompletionSource();
            var window = new WorkIqSignInWindow(host, setup, readiness,
                installVoice: (_, _, _) => Task.CompletedTask, voiceInstalled: _ => false);
            if (phase == "apply")
                host.ApplyOperation = async _ => { await pending.Task; return new(true, false, false, "Committed"); };
            if (phase == "local") readiness.LocalOperation = async _ => await pending.Task;
            if (phase == "saved-verification")
                host.VerifySavedOperation = async _ => { await pending.Task; return Identity; };
            if (phase == "mail") setup.MailOperation = async _ => await pending.Task;
            if (phase == "final-verification")
                setup.VerifyOperation = async _ => { await pending.Task; return Identity; };
            if (phase == "save") host.CompleteOperation = async _ => await pending.Task;
            if (phase == "launch") host.LaunchOperation = async _ => await pending.Task;
            try
            {
                window.Show();
                SetupStepIndicatorTests.DrainDispatcher();
                var apply = window.ApplyInstallerAsync();
                TestAssert.True((string)Find<Button>(window, "CancelButton").Content == "Cancel operation",
                    $"The pending {phase} operation did not offer cancellation.");
                window.Close();
                TestAssert.True(window.IsVisible && !apply.IsCompleted,
                    $"Closing interrupted the in-flight {phase} operation.");
                pending.SetResult();
                Complete(apply);
                TestAssert.True(window.IsVisible && host.LaunchCloses == 0 &&
                    (phase == "launch" || host.Launches == 0) &&
                    (phase is "save" or "launch" || host.Completions == 0),
                    $"Cancellation at {phase} completed automatic handoff.");
                if (phase == "save")
                {
                    Complete(window.FinishAsync());
                    TestAssert.True(host.Completions == 1 && host.Launches == 1 && host.LaunchCloses == 1,
                        "Retry after committed save rewrote state or could not launch.");
                }
            }
            finally { window.Close(); }
        }
    }

    private static void TestUpgradeVerificationRetry()
    {
        foreach (var phase in new[] { "local", "saved", "mail", "final-account", "final-version" })
        {
            var host = UpgradeHost();
            var setup = new FakeWorkIq();
            var readiness = new FakeReadiness();
            if (phase == "local") readiness.LocalOperation = _ => throw new InvalidOperationException("Synthetic local failure.");
            if (phase == "saved") host.VerifySavedOperation = _ => throw new WorkIqSetupException("Synthetic saved verification failure.");
            if (phase == "mail") setup.MailOperation = _ => throw new WorkIqSetupException("Synthetic mail failure.");
            if (phase == "final-account") setup.FailVerification = true;
            if (phase == "final-version") setup.FailVersion = true;
            var window = new WorkIqSignInWindow(host, setup, readiness,
                installVoice: (_, _, _) => Task.CompletedTask, voiceInstalled: _ => false);
            try
            {
                window.Show();
                SetupStepIndicatorTests.DrainDispatcher();
                Complete(window.ApplyInstallerAsync());
                TestAssert.True(window.IsVisible && host.Completions == 0 && host.Launches == 0 &&
                    Find<TextBlock>(window, "OperationStatus").Text.StartsWith("Failed"),
                    $"Failure at {phase} launched Peggy or was not visible.");
                readiness.LocalOperation = null;
                host.VerifySavedOperation = null;
                setup.MailOperation = null;
                setup.FailVerification = setup.FailVersion = false;
                Complete(window.CheckLocalAsync());
                if (phase == "mail") Complete(window.RetryMailAsync());
                else Connect(window);
                TestAssert.True(host.Launches == 0, "Retry checks unexpectedly resumed automatic completion.");
                Complete(window.FinishAsync());
                TestAssert.True(host.Launches == 1 && host.Completions == 1,
                    $"Deliberate Finish could not recover from {phase} failure.");
            }
            finally { window.Close(); }
        }
    }

    private static void TestLaunchFailureRetry()
    {
        var host = UpgradeHost();
        host.FailLaunch = true;
        var window = CreateWindow(host, new FakeWorkIq());
        try
        {
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(window.IsVisible && host.Completions == 1 && host.LaunchCloses == 0 &&
                Find<TextBlock>(window, "OperationStatus").Text.Contains("Synthetic missing") &&
                (string)Find<Button>(window, "FinishButton").Content == "Retry launch" &&
                !Find<GroupBox>(window, "MicrosoftConnectionGroup").IsEnabled,
                "Launch failure closed setup, hid the retry, or allowed editing already-committed setup.");
            host.FailLaunch = false;
            Complete(window.FinishAsync());
            TestAssert.True(host.Completions == 1 && host.Launches == 2 && host.LaunchCloses == 1 && !window.IsVisible,
                "Launch retry rewrote saved state or failed to close after launch.");
        }
        finally { window.Close(); }
    }

    private static void TestFinishStageDiagnostics()
    {
        foreach (var stage in new[] { "finish.verify-account", "finish.workiq-version", "finish.launch" })
        {
            var host = new FakeHost { SavedIdentity = Identity };
            var setup = new FakeWorkIq();
            var window = CreateWindow(host, setup);
            try
            {
                Complete(window.PrepareInstallerAsync());
                Complete(window.ApplyInstallerAsync());
                Complete(window.CheckCopilotAsync());
                if (stage == "finish.verify-account") setup.FailVerification = true;
                if (stage == "finish.workiq-version") setup.FailVersion = true;
                if (stage == "finish.launch") host.FailLaunch = true;
                Click(window, "FinishButton");
                TestAssert.True(!host.Launched && host.Logs.Select(SetupDiagnosticsSmokeTests.Parse).Any(entry =>
                        entry.GetProperty("stage").GetString() == stage &&
                        entry.GetProperty("outcome").GetString() == "failed" &&
                        entry.GetProperty("errors").GetArrayLength() > 0),
                    $"Finish diagnostics did not identify {stage} as the failed step.");
            }
            finally { window.Close(); }
        }
    }

    private static void TestAuthenticationDiagnostics()
    {
        var host = new FakeHost();
        var readiness = new FakeReadiness { FailSignIn = true };
        var window = new WorkIqSignInWindow(host, new FakeWorkIq(), readiness,
            installVoice: (_, _, _) => Task.CompletedTask, voiceInstalled: _ => false);
        try
        {
            Complete(window.PrepareInstallerAsync());
            Complete(window.ApplyInstallerAsync());
            Complete(window.CheckCopilotAsync(signIn: true));
            TestAssert.True(host.Logs.Any(line => line.Contains("authentication-detail-omitted")) &&
                host.Logs.All(line => !line.Contains("UNSTRUCTURED-LOGIN-SECRET")) &&
                host.Logs.Select(SetupDiagnosticsSmokeTests.Parse).Any(entry =>
                    entry.GetProperty("stage").GetString() == "ui.CheckCopilotAsync" &&
                    entry.GetProperty("outcome").GetString() == "failed"),
                "Device-sign-in diagnostics either lost the failure or exposed raw authentication output.");
        }
        finally { window.Close(); }
    }

    private static WorkIqSignInWindow CreateWindow(FakeHost host, FakeWorkIq setup) =>
        new(host, setup, new FakeReadiness(),
            installVoice: (_, _, _) => Task.CompletedTask, voiceInstalled: _ => false);

    private static void Connect(WorkIqSignInWindow window)
    {
        Complete(window.CheckCopilotAsync());
        Find<TextBox>(window, "AccountText").Text = Identity.Account;
        Find<CheckBox>(window, "EulaCheck").IsChecked = true;
        Complete(window.ConnectMicrosoftAsync());
    }

    private static void Click(Window window, string name)
    {
        Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SetupStepIndicatorTests.DrainDispatcher();
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        (T)window.FindName(name);
    private static void Complete(Task task) => SetupStepIndicatorTests.Complete(task);

    internal static void RunVisualPreview()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var host = new FakeHost { Ready = false };
        host.ApplyOperation = async ct =>
        {
            for (var i = 0; i <= 100; i += 10)
            {
                await Task.Delay(300);
                host.Report(ct.IsCancellationRequested ? "Simulated rollback..." : "Installing Peggy (simulated)...", i);
            }
            return ct.IsCancellationRequested
                ? new(false, true, false, "Simulated cancellation completed.")
                : new(true, false, false, "Peggy installed (simulated). Connect your test accounts below.");
        };
        var window = CreateWindow(host, new FakeWorkIq());
        window.Title = "Peggy setup — simulated unified preview";
        Find<TextBox>(window, "AccountText").Text = Identity.Account;
        var link = Find<Button>(window, "ReviewEulaButton");
        var panel = (Panel)link.Parent;
        var index = panel.Children.IndexOf(link);
        panel.Children.Remove(link);
        panel.Children.Insert(index, new Border { IsEnabled = false, Child = link });
        application.Run(window);
    }

    private sealed class FakeHost : IUnifiedSetupHost
    {
        public List<string> Logs { get; } = [];
        public SetupDiagnostics Diagnostics { get; }
        public FakeHost() => Diagnostics = new(Logs.Add, "synthetic-beta");
        public PeggyStateDocument InitialState { get; set; } = new();
        public bool Onboard { get; set; } = true;
        public bool RequiresOnboarding => Onboard;
        public bool IsUpgrade { get; set; }
        public string ActionLabel => "Install / update Peggy";
        public bool Ready { get; set; } = true;
        public bool FailCompletion { get; set; }
        public bool FailLaunch { get; set; }
        public bool Launched { get; private set; }
        public int RequirementInstalls, Applies, Completions, Cancels, Launches, LaunchCloses;
        public WorkIqAccountIdentity? SavedIdentity { get; set; }
        public SetupInstallResult Result { get; set; } = new(true, false, false, "Installed");
        public Func<CancellationToken, Task<SetupInstallResult>>? ApplyOperation { get; set; }
        public Func<CancellationToken, Task<WorkIqAccountIdentity?>>? VerifySavedOperation { get; set; }
        public Func<CancellationToken, Task>? CompleteOperation { get; set; }
        public Func<CancellationToken, Task>? LaunchOperation { get; set; }
        public event Action<SetupInstallProgress>? Progress;
        public void Report(string message, int percentage) => Progress?.Invoke(new(message, percentage));
        public Task<IReadOnlyList<SetupRequirement>> PrepareAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SetupRequirement>>([new("node", "Node.js 18+", Ready, Ready ? "Ready" : "Not installed")]);
        public Task InstallRequirementAsync(string id, CancellationToken ct)
        { RequirementInstalls++; Ready = true; return Task.CompletedTask; }
        public Task<SetupInstallResult> ApplyAsync(IntPtr owner, CancellationToken ct)
        { Applies++; return ApplyOperation?.Invoke(ct) ?? Task.FromResult(Result); }
        public Task<WorkIqAccountIdentity?> VerifySavedIdentityAsync(CancellationToken ct) =>
            VerifySavedOperation?.Invoke(ct) ?? Task.FromResult(SavedIdentity);
        public async Task CompleteAsync(WorkIqAccountIdentity identity, bool voiceReady, CancellationToken ct)
        {
            if (FailCompletion) throw new InvalidOperationException("Could not persist verified setup.");
            if (CompleteOperation is not null) await CompleteOperation(ct);
            Completions++;
        }
        public async Task LaunchAsync(CancellationToken ct)
        {
            Launches++;
            if (FailLaunch) throw new InvalidOperationException("Synthetic missing installed executable.");
            if (LaunchOperation is not null) await LaunchOperation(ct);
            ct.ThrowIfCancellationRequested();
            Launched = true;
        }
        public void Cancel() => Cancels++;
        public void Close(bool launch)
        {
            if (launch) LaunchCloses++;
        }
        public void ReportFailure(Exception exception, bool includeExceptionMessages = true)
            => Diagnostics.Failure("setup.operation", exception, includeExceptionMessages: includeExceptionMessages);
    }

    private sealed class FakeWorkIq : IWorkIqSetupService
    {
        public int VersionChecks, SignIns, MailProbes, Verifications;
        public bool FailVerification, FailVersion;
        public Func<CancellationToken, Task<WorkIqAccountIdentity>>? VerifyOperation;
        public Func<CancellationToken, Task>? MailOperation;
        public WorkIqAccountIdentity VerifiedIdentity = Identity;
        public bool TryGetExecutablePath(out string executablePath) { executablePath = "fake-workiq"; return true; }
        public Task<string> GetVersionAsync(CancellationToken ct = default)
        {
            VersionChecks++;
            if (FailVersion) throw new InvalidOperationException("Synthetic WorkIQ version failure.");
            return Task.FromResult("1.0.0");
        }
        public Task InstallAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkIqAccountIdentity> SignInAsync(string account, string version, CancellationToken ct = default,
            IProgress<WorkIqSignInStage>? progress = null) { SignIns++; return Task.FromResult(Identity); }
        public Task<WorkIqAccountIdentity> VerifyAccountAsync(string account, CancellationToken ct = default)
        {
            Verifications++;
            return FailVerification ? throw new WorkIqSetupException("Synthetic identity failure.")
                : VerifyOperation?.Invoke(ct) ?? Task.FromResult(VerifiedIdentity);
        }
        public Task ProbeInboxAccessAsync(WorkIqAccountIdentity identity, CancellationToken ct = default)
        {
            MailProbes++;
            return MailOperation?.Invoke(ct) ?? Task.CompletedTask;
        }
    }

    private sealed class FakeReadiness : ISetupReadinessService
    {
        public bool FailSignIn { get; init; }
        public Func<CancellationToken, Task>? LocalOperation;
        public async Task<SetupLocalReadiness> CheckLocalAsync(CancellationToken ct, IProgress<SetupLocalCheckUpdate>? progress = null)
        {
            if (LocalOperation is not null) await LocalOperation(ct);
            var ready = new SetupCheck(SetupCheckState.Ready, "Ready (simulated)", Version: "1.0.0");
            return new SetupLocalReadiness(ready, ready, ready, ready, ready);
        }
        public Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct) => Task.FromResult(new SetupCheck(SetupCheckState.Ready, "Available (simulated)"));
        public Task SignInCopilotAsync(CancellationToken ct, IProgress<CopilotDeviceCodeChallenge>? progress = null)
            => FailSignIn ? throw new InvalidOperationException("UNSTRUCTURED-LOGIN-SECRET") : Task.CompletedTask;
    }

    internal sealed class FakeApplication : BootstrapperApplication
    {
        protected override void Run() { }
        public void Detect(bool downgrade, bool present = false, string? relatedVersion = null)
        {
            OnDetectBegin(new(false, RegistrationType.None, 1, false));
            if (downgrade) OnDetectRelatedBundle(new("newer", RelationType.Upgrade, "", false, "9.0.0", false, false));
            if (relatedVersion is not null)
                OnDetectRelatedBundle(new("related", RelationType.Upgrade, "", false, relatedVersion, false, false));
            OnDetectPackageComplete(new("PeggyMsi", 0, present ? PackageState.Present : PackageState.Absent, false));
            OnDetectComplete(new(0, false));
        }
        public void Plan(int status = 0) => OnPlanComplete(new(status));
        public void ReportProgress(ExecuteProgressEventArgs args) => OnExecuteProgress(args);
        public void BeginPackage(ExecutePackageBeginEventArgs args) => OnExecutePackageBegin(args);
        public void BeginApply(ApplyBeginEventArgs args) => OnApplyBegin(args);
        public void Complete(ApplyCompleteEventArgs args) => OnApplyComplete(args);
        public BOOTSTRAPPER_SHUTDOWN_ACTION ReportShutdown()
        {
            var args = new ShutdownEventArgs(BOOTSTRAPPER_SHUTDOWN_ACTION.Restart);
            OnShutdown(args);
            return args.Action;
        }
    }

    private static void TestRefreshedAccountAndMaintenance()
    {
        var host = new FakeHost
        {
            InitialState = new PeggyStateDocument { PinnedAccount = "old@example.test" },
            SavedIdentity = Identity
        };
        var window = CreateWindow(host, new FakeWorkIq());
        try
        {
            window.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            host.InitialState = new PeggyStateDocument
            {
                PinnedAccount = Identity.Account,
                PinnedMailbox = Identity.Mail ?? "",
                PinnedUserPrincipalName = Identity.UserPrincipalName ?? "",
                PinnedPrincipalId = Identity.PrincipalId
            };
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(Find<StackPanel>(window, "InstallerAccountSwitchConfirmation").Visibility == Visibility.Collapsed &&
                Find<TextBox>(window, "AccountText").Text == Identity.Account,
                "Installer kept its stale account preview after acquiring the application guard.");
        }
        finally { window.Close(); }
        var maintenance = new FakeHost { Onboard = false };
        var maintenanceWindow = CreateWindow(maintenance, new FakeWorkIq());
        try
        {
            maintenanceWindow.Show();
            SetupStepIndicatorTests.DrainDispatcher();
            Complete(maintenanceWindow.ApplyInstallerAsync());
            Click(maintenanceWindow, "FinishButton");
            TestAssert.True(!maintenance.Launched && maintenance.Completions == 0,
                "Maintenance Finish launched Peggy or saved account setup.");
        }
        finally { maintenanceWindow.Close(); }
    }

    public class FakeEngine : DispatchProxy
    {
        private FakeApplication? _application;
        internal object Application { set => _application = (FakeApplication)value; }
        public bool Downgrade;
        public bool CurrentMsiPresent;
        public string? RelatedVersion;
        public bool ThrowApply;
        public int PlanStatus;
        public int Plans, Applies;
        public LaunchAction LastAction;
        public int VariableWrites;
        public List<string> Logs { get; } = [];
        public TaskCompletionSource ApplyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "Log": Logs.Add((string)args![1]!); break;
                case "Detect": _application!.Detect(Downgrade, CurrentMsiPresent, RelatedVersion); break;
                case "Plan": Plans++; LastAction = (LaunchAction)args![0]!; _application!.Plan(PlanStatus); break;
                case "SetVariableString": VariableWrites++; break;
                case "Apply":
                    TestAssert.True((IntPtr)args![0]! != IntPtr.Zero,
                        "Burn Apply was called without the required owner HWND.");
                    Applies++;
                    if (ThrowApply) throw new InvalidOperationException("Synthetic in-flight Apply failure.");
                    ApplyStarted.TrySetResult();
                    break;
                case "CompareVersions": return Version.Parse((string)args![0]!).CompareTo(Version.Parse((string)args[1]!));
                case "GetVariableVersion": return "1.0.0";
            }
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
