using WixToolset.BootstrapperApplicationApi;
using Peggy.Tray;

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
    private int _inFlight;

    internal BurnInstaller(BootstrapperApplication application, IEngine engine, IBootstrapperCommand command)
    {
        _engine = engine;
        _command = command;
        application.DetectBegin += (_, _) => { CurrentMsiPresent = false; _downgrade = false; };
        application.DetectPackageComplete += (_, e) =>
        {
            if (e.PackageId == "PeggyMsi") CurrentMsiPresent = e.State == PackageState.Present;
        };
        application.DetectRelatedBundle += (_, e) =>
        {
            if (e.RelationType == RelationType.Upgrade &&
                engine.CompareVersions(e.Version, engine.GetVariableVersion("WixBundleVersion")) > 0)
                _downgrade = true;
        };
        application.DetectComplete += (_, e) => _detect?.TrySetResult(e.Status);
        application.PlanMsiPackage += (_, e) =>
        {
            e.UiLevel = INSTALLUILEVEL.None;
            e.DisableExternalUiHandler = false;
        };
        application.PlanComplete += (_, e) => _plan?.TrySetResult(e.Status);
        application.ApplyBegin += (_, e) => e.Cancel = _cancel;
        application.CacheAcquireProgress += (_, e) =>
        {
            e.Cancel = _cancel;
            Progress?.Invoke(new("Preparing the Peggy installation...", e.OverallPercentage));
        };
        application.ExecutePackageBegin += (_, e) =>
        {
            _rollback = !e.ShouldExecute;
            e.Cancel = _cancel && !_rollback;
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
            Progress?.Invoke(new("Files are in use. Quit Peggy and retry after rollback completes.", 0));
        };
        application.Error += (_, e) =>
        {
            engine.Log(LogLevel.Error, $"Peggy setup: installer error {e.ErrorCode} ({e.ErrorType}).");
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
            _apply?.TrySetResult(new(succeeded, _cancel && !succeeded, restart,
                succeeded
                    ? restart ? "Peggy is installed. Restart Windows before connecting accounts. Setup will not restart this PC automatically."
                    : "Peggy installation completed. Continue account setup below."
                    : _cancel ? "Installation cancelled. Windows Installer has completed rollback. You can retry or close."
                    : $"Installation failed (0x{e.Status:X8}). Windows Installer has finished. Review the log, then Retry."));
        };
        application.Shutdown += (_, e) => e.Action = IsLayout
            ? BOOTSTRAPPER_SHUTDOWN_ACTION.SkipCleanup
            : BOOTSTRAPPER_SHUTDOWN_ACTION.None;
    }

    internal event Action<SetupInstallProgress>? Progress;
    internal bool CurrentMsiPresent { get; private set; }
    internal int ExitCode { get; private set; } = 1602;
    internal bool RequiresOnboarding => SetupPolicy.ShouldOnboard(_command.Action, _command.Display,
        _command.Relation, CurrentMsiPresent);
    internal bool IsLayout => _command.Action == LaunchAction.Layout;
    internal bool RequiresWindow => !IsLayout && SetupPolicy.ShowWindow(_command.Display, _command.Relation);
    internal bool MayOnboard => RequiresWindow && _command.Action == LaunchAction.Install;
    internal bool IsRelated => _command.Relation != RelationType.None;
    internal bool ChecksRequirements => _command.Action is LaunchAction.Install or LaunchAction.Repair;
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
        if (owner == IntPtr.Zero)
            throw new ArgumentException("Windows Installer requires an owner window handle, including for unattended setup.", nameof(owner));
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            throw new InvalidOperationException("An installer operation is already running.");
        _cancel = false;
        _rollback = false;
        using var cancellation = ct.Register(Cancel);
        try
        {
            await DetectAsync();
            ct.ThrowIfCancellationRequested();
            _plan = NewCompletion<int>();
            // Burn initializes WixBundleLayoutDirectory from the command before
            // creating this BA; preserve its resolved destination unchanged.
            _engine.Plan(_command.Action);
            ThrowFailure(await _plan.Task, "Plan");
            ct.ThrowIfCancellationRequested();
            _apply = NewCompletion<SetupInstallResult>();
            _engine.Apply(owner);
            // Cancellation must wait for ApplyComplete: the engine owns rollback.
            return await _apply.Task;
        }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    internal void Cancel() => _cancel = true;
    internal void LogFailure(Exception ex, bool preserveExitCode = false)
    {
        if (!preserveExitCode) ExitCode = ex is OperationCanceledException ? 1602 : 1603;
        _engine.Log(LogLevel.Error, $"Peggy setup operation failed ({ex.GetType().Name}); review the inline setup status.");
    }
    internal void WindowReady() => _engine.CloseSplashScreen();
    internal void Quit() => _engine.Quit(ExitCode);
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
}
