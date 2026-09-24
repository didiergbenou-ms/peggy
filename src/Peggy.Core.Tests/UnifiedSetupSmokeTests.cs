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
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async Task RunAsync(string directory)
    {
        await SetupDiagnosticsSmokeTests.RunAsync(directory);
        await SetupLogCollectorSmokeTests.RunAsync(directory);
        foreach (var display in new[] { Display.None, Display.Passive, Display.Embedded })
            TestAssert.True(!SetupPolicy.ShowWindow(display, RelationType.None) &&
                !SetupPolicy.ShouldOnboard(LaunchAction.Install, display, RelationType.None, false),
                "Quiet/passive setup enabled onboarding UI.");
        foreach (var relation in new[] { RelationType.Upgrade, RelationType.ChainPackage, RelationType.Update })
            TestAssert.True(!SetupPolicy.ShowWindow(Display.Full, relation) &&
                !SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, relation, false),
                "Related bundle execution enabled UI or authentication.");
        foreach (var action in new[] { LaunchAction.Uninstall, LaunchAction.Repair, LaunchAction.Layout, LaunchAction.Modify })
            TestAssert.True(!SetupPolicy.ShouldOnboard(action, Display.Full, RelationType.None, false),
                "Maintenance enabled onboarding.");
        TestAssert.True(SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, RelationType.None, false) &&
            !SetupPolicy.ShouldOnboard(LaunchAction.Install, Display.Full, RelationType.None, true),
            "Fresh install/upgrade and same-product maintenance were not distinguished.");
        await TestBurnBridgeAsync();
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
        var burn = new BurnInstaller(application, engine, command);
        var owner = new IntPtr(1);
        try { await burn.ApplyAsync(IntPtr.Zero, CancellationToken.None); throw new Exception("Null owner was accepted."); }
        catch (ArgumentException) { }
        fake.Downgrade = true;
        try { await burn.DetectAsync(); throw new Exception("Downgrade was accepted."); }
        catch (InvalidOperationException ex) { TestAssert.True(ex.Message.Contains("Downgrade", StringComparison.OrdinalIgnoreCase), "Wrong downgrade failure."); }
        TestAssert.True(fake.Plans == 0 && fake.Applies == 0, "Downgrade reached Plan/Apply.");
        fake.Downgrade = false;
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
        TestAssert.True(application.ReportShutdown() == BOOTSTRAPPER_SHUTDOWN_ACTION.None,
            "Normal setup requested an automatic restart.");
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
        TestFinishStageDiagnostics();
        TestAuthenticationDiagnostics();
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
        var host = new FakeHost
        {
            InitialState = new PeggyStateDocument
            {
                PinnedAccount = Identity.Account, PinnedMailbox = Identity.Mail!,
                PinnedUserPrincipalName = Identity.UserPrincipalName!, PinnedPrincipalId = Identity.PrincipalId,
                WorkIqEulaAcceptedVersion = Identity.EulaAcceptedVersion
            },
            SavedIdentity = Identity,
            FailCompletion = true
        };
        var setup = new FakeWorkIq();
        var window = CreateWindow(host, setup);
        try
        {
            Complete(window.PrepareInstallerAsync());
            Complete(window.ApplyInstallerAsync());
            TestAssert.True(window.CanFinish && setup.SignIns == 0, "A valid upgraded account required another sign-in.");
            Click(window, "FinishButton");
            TestAssert.True(!host.Launched && host.Completions == 0 &&
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
        public PeggyStateDocument InitialState { get; init; } = new();
        public bool RequiresOnboarding => true;
        public string ActionLabel => "Install / update Peggy";
        public bool Ready { get; set; } = true;
        public bool FailCompletion { get; set; }
        public bool FailLaunch { get; set; }
        public bool Launched { get; private set; }
        public int RequirementInstalls, Applies, Completions, Cancels;
        public WorkIqAccountIdentity? SavedIdentity { get; init; }
        public SetupInstallResult Result { get; set; } = new(true, false, false, "Installed");
        public Func<CancellationToken, Task<SetupInstallResult>>? ApplyOperation { get; set; }
        public event Action<SetupInstallProgress>? Progress;
        public void Report(string message, int percentage) => Progress?.Invoke(new(message, percentage));
        public Task<IReadOnlyList<SetupRequirement>> PrepareAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SetupRequirement>>([new("node", "Node.js 18+", Ready, Ready ? "Ready" : "Not installed")]);
        public Task InstallRequirementAsync(string id, CancellationToken ct)
        { RequirementInstalls++; Ready = true; return Task.CompletedTask; }
        public Task<SetupInstallResult> ApplyAsync(IntPtr owner, CancellationToken ct)
        { Applies++; return ApplyOperation?.Invoke(ct) ?? Task.FromResult(Result); }
        public Task<WorkIqAccountIdentity?> VerifySavedIdentityAsync(CancellationToken ct) => Task.FromResult(SavedIdentity);
        public Task CompleteAsync(WorkIqAccountIdentity identity, bool voiceReady, CancellationToken ct)
        {
            if (FailCompletion) throw new InvalidOperationException("Could not persist verified setup.");
            Completions++; return Task.CompletedTask;
        }
        public void Cancel() => Cancels++;
        public void Close(bool launch)
        {
            if (launch && FailLaunch) throw new InvalidOperationException("Synthetic missing installed executable.");
            Launched |= launch;
        }
        public void ReportFailure(Exception exception, bool includeExceptionMessages = true)
            => Diagnostics.Failure("setup.operation", exception, includeExceptionMessages: includeExceptionMessages);
    }

    private sealed class FakeWorkIq : IWorkIqSetupService
    {
        public int VersionChecks, SignIns;
        public bool FailVerification, FailVersion;
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
            => FailVerification ? throw new WorkIqSetupException("Synthetic identity failure.")
                : Task.FromResult(VerifiedIdentity);
        public Task ProbeInboxAccessAsync(WorkIqAccountIdentity identity, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeReadiness : ISetupReadinessService
    {
        public bool FailSignIn { get; init; }
        public Task<SetupLocalReadiness> CheckLocalAsync(CancellationToken ct, IProgress<SetupLocalCheckUpdate>? progress = null)
        {
            var ready = new SetupCheck(SetupCheckState.Ready, "Ready (simulated)", Version: "1.0.0");
            return Task.FromResult(new SetupLocalReadiness(ready, ready, ready, ready, ready));
        }
        public Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct) => Task.FromResult(new SetupCheck(SetupCheckState.Ready, "Available (simulated)"));
        public Task SignInCopilotAsync(CancellationToken ct, IProgress<CopilotDeviceCodeChallenge>? progress = null)
            => FailSignIn ? throw new InvalidOperationException("UNSTRUCTURED-LOGIN-SECRET") : Task.CompletedTask;
    }

    private sealed class FakeApplication : BootstrapperApplication
    {
        protected override void Run() { }
        public void Detect(bool downgrade)
        {
            OnDetectBegin(new(false, RegistrationType.None, 1, false));
            if (downgrade) OnDetectRelatedBundle(new("newer", RelationType.Upgrade, "", false, "9.0.0", false, false));
            OnDetectPackageComplete(new("PeggyMsi", 0, PackageState.Absent, false));
            OnDetectComplete(new(0, false));
        }
        public void Plan() => OnPlanComplete(new(0));
        public void ReportProgress(ExecuteProgressEventArgs args) => OnExecuteProgress(args);
        public void Complete(ApplyCompleteEventArgs args) => OnApplyComplete(args);
        public BOOTSTRAPPER_SHUTDOWN_ACTION ReportShutdown()
        {
            var args = new ShutdownEventArgs(BOOTSTRAPPER_SHUTDOWN_ACTION.Restart);
            OnShutdown(args);
            return args.Action;
        }
    }

    public class FakeEngine : DispatchProxy
    {
        private FakeApplication? _application;
        internal object Application { set => _application = (FakeApplication)value; }
        public bool Downgrade;
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
                case "Detect": _application!.Detect(Downgrade); break;
                case "Plan": Plans++; LastAction = (LaunchAction)args![0]!; _application!.Plan(); break;
                case "SetVariableString": VariableWrites++; break;
                case "Apply":
                    TestAssert.True((IntPtr)args![0]! != IntPtr.Zero,
                        "Burn Apply was called without the required owner HWND.");
                    Applies++;
                    ApplyStarted.TrySetResult();
                    break;
                case "CompareVersions": return 1;
                case "GetVariableVersion": return "1.0.0";
            }
            return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
