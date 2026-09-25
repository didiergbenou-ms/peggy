using WixToolset.BootstrapperApplicationApi;
using Peggy.Tray;
using Peggy.Core;

namespace Peggy.SetupBootstrapper;

internal sealed class BurnInstaller
{
    private readonly IEngine _engine;
    private readonly IBootstrapperCommand _command;
    private TaskCompletionSource<int>? _detect;
    private TaskCompletionSource<int>? _plan;
    private TaskCompletionSource<SetupInstallResult>? _apply;
    private volatile bool _cancel;
    private volatile bool _rollback;
    private bool _downgrade;
    private bool _olderBundleDetected;
    private int _inFlight;
    private bool _applyStarted;
    private bool _applyCompleted;
    private bool _ownsApply;
    private readonly SetupInstanceLease? _setupLease;

    internal BurnInstaller(BootstrapperApplication application, IEngine engine, IBootstrapperCommand command,
        SetupInstanceLease? setupLease = null)
    {
        _engine = engine;
        _command = command;
        _setupLease = IsLayout || IsRelated ? null : setupLease ?? new SetupInstanceLease();
        Diagnostics = new SetupDiagnostics(line => engine.Log(LogLevel.Standard, line), PeggyReleaseVersion.Display);
        Diagnostics.Event("session", "opened",
            $"action={command.Action}; display={command.Display}; relation={command.Relation}");
        application.DetectBegin += (_, _) =>
        {
            CurrentMsiPresent = false;
            _downgrade = false;
            _olderBundleDetected = false;
            Diagnostics.Event("burn.detect", "started");
        };
        application.DetectPackageComplete += (_, e) =>
        {
            if (e.PackageId == "PeggyMsi") CurrentMsiPresent = e.State == PackageState.Present;
            Diagnostics.Event("burn.detect-package", "completed", $"package={e.PackageId}; state={e.State}");
        };
        application.DetectRelatedBundle += (_, e) =>
        {
            Diagnostics.Event("burn.related-bundle", "detected", $"relation={e.RelationType}; version={e.Version}");
            if (e.RelationType == RelationType.Upgrade)
            {
                var comparison = engine.CompareVersions(e.Version, engine.GetVariableVersion("WixBundleVersion"));
                _downgrade |= comparison > 0;
                _olderBundleDetected |= comparison < 0;
            }
        };
        application.DetectComplete += (_, e) =>
        {
            Diagnostics.Event("burn.detect", e.Status < 0 ? "failed" : "completed", $"status=0x{e.Status:X8}");
            _detect?.TrySetResult(e.Status);
        };
        application.PlanMsiPackage += (_, e) =>
        {
            e.UiLevel = INSTALLUILEVEL.None;
            e.DisableExternalUiHandler = false;
        };
        application.PlanComplete += (_, e) =>
        {
            Diagnostics.Event("burn.plan", e.Status < 0 ? "failed" : "completed", $"status=0x{e.Status:X8}");
            _plan?.TrySetResult(e.Status);
        };
        application.ApplyBegin += (_, e) =>
        {
            e.Cancel = _cancel;
            _applyStarted = true;
            Diagnostics.Event("burn.apply", "started", $"cancel={_cancel}");
        };
        application.CacheAcquireProgress += (_, e) =>
        {
            e.Cancel = _cancel;
            Progress?.Invoke(new("Preparing the Peggy installation...", e.OverallPercentage));
        };
        application.ExecutePackageBegin += (_, e) =>
        {
            _rollback = !e.ShouldExecute;
            e.Cancel = _cancel && !_rollback;
            Diagnostics.Event("burn.execute-package", "started",
                $"package={e.PackageId}; rollback={_rollback}; cancel={e.Cancel}");
        };
        application.ExecuteProgress += (_, e) =>
        {
            e.Cancel = _cancel && !_rollback;
            Progress?.Invoke(new(_rollback ? "Rolling back the interrupted installation..." : "Installing Peggy...",
                e.OverallPercentage));
        };
        application.Progress += (_, e) => e.Cancel = _cancel && !_rollback;
        application.ExecuteFilesInUse += (_, e) =>
        {
            e.Result = Result.Cancel;
            Diagnostics.Event("burn.files-in-use", "blocked", "response=Cancel");
            Progress?.Invoke(new("Files are in use. Quit Peggy and retry after rollback completes.", 0));
        };
        application.Error += (_, e) =>
        {
            engine.Log(LogLevel.Error, $"Peggy setup: installer error {e.ErrorCode} ({e.ErrorType}).");
            Diagnostics.Event("burn.error", "failed", $"error={e.ErrorCode}; type={e.ErrorType}; rollback={_rollback}");
            e.Result = _cancel ? Result.Cancel : e.Recommendation;
            Progress?.Invoke(new($"Windows Installer reported error {e.ErrorCode}. Wait for it to finish, then retry.", 0));
        };
        application.ApplyDowngrade += (_, e) =>
        {
            if (!IsLayout) e.Status = unchecked((int)0x80070666);
        };
        application.ApplyComplete += (_, e) =>
        {
            e.Action = BOOTSTRAPPER_APPLYCOMPLETE_ACTION.None;
            var restart = e.Restart != ApplyRestart.None;
            var succeeded = e.Status >= 0;
            ExitCode = succeeded ? restart ? 3010 : 0 : e.Status;
            _applyCompleted = true;
            Diagnostics.Event("burn.apply", succeeded ? "completed" : _cancel ? "cancelled" : "failed",
                $"status=0x{e.Status:X8}; restart={e.Restart}; exitCode={ExitCode}");
            _apply?.TrySetResult(new(succeeded, _cancel && !succeeded, restart,
                succeeded
                    ? restart ? "Peggy is installed. Restart Windows before connecting accounts. Setup will not restart this PC automatically."
                    : "Peggy installation completed. Continue account setup below."
                    : _cancel ? "Installation cancelled. Windows Installer has completed rollback. You can retry or close."
                    : $"Installation failed (0x{e.Status:X8}). Windows Installer has finished. Review the log, then Retry."));
        };
        application.Shutdown += (_, e) =>
        {
            // WiX 6 stops the BA before CoreCleanup, beyond this process's lease.
            // CoreCleanup is a new implicit uninstall, NOT Apply rollback. Even
            // ApplyBegin cancellation can leave it eligible: always suppress it.
            // Explicit uninstall, related removal and rollback finish in CoreApply.
            e.Action = BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup;
            Diagnostics.Event("burn.shutdown", "requested",
                $"action={e.Action}; reason=disable-post-ba-implicit-uninstall; exitCode={ExitCode}; ownsApply={_ownsApply}; setupOwned={_setupLease?.IsOwned == true}; applyStarted={_applyStarted}; applyCompleted={_applyCompleted}; msiPresentAtDetect={CurrentMsiPresent}");
        };
    }

    internal event Action<SetupInstallProgress>? Progress;
    internal SetupDiagnostics Diagnostics { get; }
    internal bool CurrentMsiPresent { get; private set; }
    internal int ExitCode { get; private set; } = 1602;
    internal bool RequiresOnboarding => SetupPolicy.ShouldOnboard(_command.Action, _command.Display,
        _command.Relation, CurrentMsiPresent);
    internal bool IsUpgrade => SetupPolicy.IsUpgrade(_command.Action, _command.Display,
        _command.Relation, CurrentMsiPresent, _olderBundleDetected);
    internal bool IsLayout => _command.Action == LaunchAction.Layout;
    internal bool RequiresWindow => !IsLayout && SetupPolicy.ShowWindow(_command.Display, _command.Relation);
    internal bool MayOnboard => RequiresWindow && _command.Action == LaunchAction.Install;
    internal bool IsRelated => _command.Relation != RelationType.None;
    internal bool ChecksRequirements => _command.Action is LaunchAction.Install or LaunchAction.Repair;
    internal bool MayCloseRunningApp =>
        RequiresWindow && _command.Action == LaunchAction.Install && !CurrentMsiPresent;
    internal string ActionLabel => _command.Action switch
    {
        LaunchAction.Uninstall or LaunchAction.UnsafeUninstall => "Uninstall Peggy",
        LaunchAction.Repair => "Repair Peggy",
        LaunchAction.Layout => "Download Peggy",
        _ => CurrentMsiPresent ? "Maintain Peggy" : "Install / update Peggy"
    };

    internal async Task DetectAsync()
    {
        _detect = NewCompletion<int>();
        _engine.Detect();
        var status = await _detect.Task;
        ThrowFailure(status, "Detect");
        if (_downgrade && !IsLayout &&
            _command.Action is not (LaunchAction.Uninstall or LaunchAction.UnsafeUninstall))
            throw new InvalidOperationException("A newer Peggy version is already installed. Downgrades are blocked.");
    }

    internal async Task<SetupInstallResult> ApplyAsync(IntPtr owner, CancellationToken ct)
    {
        EnsureSetupOwnership();
        if (owner == IntPtr.Zero)
            throw new ArgumentException("Windows Installer requires an owner window handle, including for unattended setup.", nameof(owner));
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new InvalidOperationException("An installer operation is already running.");
        _cancel = false;
        _rollback = false;
        _applyStarted = _applyCompleted = false;
        using var cancellation = ct.Register(Cancel);
        try
        {
            await DetectAsync();
            ct.ThrowIfCancellationRequested();
            _plan = NewCompletion<int>();
            // Burn initializes WixBundleLayoutDirectory from the command before
            // creating this BA; preserve its resolved destination unchanged.
            Diagnostics.Event("burn.plan", "started", $"action={_command.Action}");
            _engine.Plan(_command.Action);
            ThrowFailure(await _plan.Task, "Plan");
            ct.ThrowIfCancellationRequested();
            _apply = NewCompletion<SetupInstallResult>();
            // Once handed to Burn, even a synchronous exception may represent an
            // in-flight transaction. Track ownership independently of shutdown's
            // implicit cleanup; cancellation must still wait for ApplyComplete.
            _ownsApply = true;
            Diagnostics.Event("burn.apply-ownership", "acquired");
            _engine.Apply(owner);
            // Cancellation must wait for ApplyComplete: the engine owns rollback.
            return await _apply.Task;
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    internal void EnsureSetupOwnership()
    {
        if (IsLayout || IsRelated) return; // Related removal executes inside the parent transaction.
        if (_setupLease!.TryAcquire())
        {
            Diagnostics.Event("setup.guard", "owned");
            return;
        }
        Diagnostics.Event("setup.guard", "blocked");
        throw new InvalidOperationException("Another Peggy setup is open. Finish or close that setup, then Retry here, or Cancel. This setup has not changed Peggy.");
    }

    internal void ReportProgress(string message) => Progress?.Invoke(new(message, 0));

    internal void Cancel()
    {
        Diagnostics.Event("burn.cancel", "requested", $"rollback={_rollback}; applyStarted={_applyStarted}");
        _cancel = true;
    }
    internal void LogFailure(Exception ex, bool preserveExitCode = false,
        bool includeExceptionMessages = true)
    {
        if (!preserveExitCode) ExitCode = ex is OperationCanceledException ? 1602 : 1603;
        _engine.Log(LogLevel.Error, $"Peggy setup operation failed ({ex.GetType().Name}); review the inline setup status.");
        Diagnostics.Failure("setup.operation", ex,
            $"preserveExitCode={preserveExitCode}; exitCode={ExitCode}; applyStarted={_applyStarted}; applyCompleted={_applyCompleted}",
            includeExceptionMessages);
    }
    internal void WindowReady() => _engine.CloseSplashScreen();
    internal void Quit()
    {
        Diagnostics.Event("session", "closing", $"exitCode={ExitCode}");
        _engine.Quit(ExitCode);
    }
    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void ThrowFailure(int status, string phase)
    {
        if (status < 0) throw new InvalidOperationException($"Windows Installer {phase} failed (0x{status:X8}).");
    }
}

internal static class SetupPolicy
{
    internal static bool ShowWindow(Display display, RelationType relation) =>
        display == Display.Full && relation == RelationType.None;

    internal static bool ShouldOnboard(LaunchAction action, Display display, RelationType relation, bool currentMsiPresent) =>
        ShowWindow(display, relation) && action == LaunchAction.Install && !currentMsiPresent;

    internal static bool IsUpgrade(LaunchAction action, Display display, RelationType relation,
        bool currentMsiPresent, bool olderBundleDetected) =>
        ShouldOnboard(action, display, relation, currentMsiPresent) && olderBundleDetected;
}
