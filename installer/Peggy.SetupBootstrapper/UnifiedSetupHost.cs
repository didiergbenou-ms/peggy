using System.Diagnostics;
using System.IO;
using Peggy.Core;
using Peggy.PrerequisiteCheck;
using Peggy.Tray;

namespace Peggy.SetupBootstrapper;

internal sealed class UnifiedSetupHost : IUnifiedSetupHost
{
    private readonly BurnInstaller _burn;
    private readonly EncryptedStateStore _store = new();
    private readonly WorkIqSetupService _setup = new();
    private SingleInstanceGuard? _guard;
    private IReadOnlyList<PrerequisiteResult> _requirements = [];
    private string? _initialFailure;
    private string _expectedIdentity = "";
    private bool _prepared;
    private bool _committed;
    private bool _restart;
    private bool _onboard;
    private bool _completed;
    private bool _closed;

    internal UnifiedSetupHost(BurnInstaller burn)
    {
        _burn = burn;
        if (burn.MayOnboard && InteractiveIdentity.CanInstallDependencies)
        {
            try
            {
                InitialState = Diagnostics.Run("state.load-initial",
                    () => Task.Run(() => _store.LoadAsync()).GetAwaiter().GetResult());
                _expectedIdentity = IdentityStamp(InitialState);
                Diagnostics.Event("state.initial", "loaded",
                    $"hasPinnedAccount={!string.IsNullOrWhiteSpace(InitialState.PinnedAccount)}");
            }
            catch (Exception ex) when (SetupReadinessService.IsExpectedFailure(ex))
            {
                _initialFailure = $"Could not read Peggy's saved settings: {ex.Message}";
                burn.LogFailure(ex);
            }
        }
    }

    public PeggyStateDocument InitialState { get; } = new();
    public SetupDiagnostics Diagnostics => _burn.Diagnostics;
    public bool RequiresOnboarding => _onboard;
    public string ActionLabel => _burn.ActionLabel;
    public event Action<SetupInstallProgress>? Progress
    {
        add => _burn.Progress += value;
        remove => _burn.Progress -= value;
    }

    public Task<IReadOnlyList<SetupRequirement>> PrepareAsync(CancellationToken ct)
        => Diagnostics.RunAsync<IReadOnlyList<SetupRequirement>>("setup.prepare", async () =>
    {
        _prepared = false;
        ValidateInteractiveIdentity();
        AcquireGuard();
        await _burn.DetectAsync();
        _onboard = _burn.RequiresOnboarding;
        if (_onboard)
        {
            if (_initialFailure is not null) throw new InvalidOperationException(_initialFailure);
            await ReloadAndValidateAsync(ct);
        }
        PrerequisiteEngine.RefreshEnvironment();
        _requirements = _burn.ChecksRequirements
            ? await Diagnostics.RunAsync("prerequisites.check", () => PrerequisiteEngine.CheckAllAsync(ct)) : [];
        foreach (var requirement in _requirements)
            Diagnostics.Event($"prerequisite.{requirement.Id}", requirement.State.ToString(), requirement.Detail);
        _prepared = true;
        return _requirements.Select(result => new SetupRequirement(
            result.Id, result.Name, result.State == PrerequisiteState.Ready, result.Detail)).ToArray();
    });

    public Task InstallRequirementAsync(string id, CancellationToken ct)
        => Diagnostics.RunAsync($"prerequisite.install.{id}", async () =>
    {
        ValidateInteractiveIdentity();
        AcquireGuard();
        var result = _requirements.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("Retry local checks before installing this requirement.");
        var winget = PrerequisiteEngine.ResolveCommand(["winget.exe"])
            ?? throw new InvalidOperationException(
                $"WinGet is unavailable. Install this requirement from {PrerequisiteInstall.OfficialUrl(id)}, then Retry.");
        var start = PrerequisiteInstall.CreateStartInfo(winget, result, explicitConsent: true, interactive: true,
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PEGGY_COPILOT_PATH")));
        var code = await PrerequisiteInstall.RunAsync(start, ct);
        Diagnostics.Event("prerequisite.installer", "exited", $"id={id}; exitCode={code}");
        PrerequisiteEngine.RefreshEnvironment();
        if (code != 0)
            throw new InvalidOperationException($"The vendor installer exited with code {code}. Retry checks or use its official installer.");
    });

    public Task<SetupInstallResult> ApplyAsync(IntPtr owner, CancellationToken ct)
        => Diagnostics.RunAsync("setup.install", async () =>
    {
        ValidateInteractiveIdentity();
        AcquireGuard();
        if (!_prepared || _requirements.Any(item => item.State != PrerequisiteState.Ready))
            throw new InvalidOperationException("Complete local requirements before installing Peggy.");
        if (_onboard) await ReloadAndValidateAsync(ct);
        var result = await _burn.ApplyAsync(owner, ct);
        _committed = result.Succeeded;
        _restart = result.RestartRequired;
        Diagnostics.Event("setup.install-result", "recorded",
            $"committed={_committed}; restartRequired={_restart}; cancelled={result.Cancelled}; onboarding={_onboard}");
        if (_committed)
        {
            PrerequisiteEngine.RefreshEnvironment();
            // Do not treat a cancellation racing a successful commit as a rollback.
            if (_onboard) await ReloadAndValidateAsync(CancellationToken.None);
        }
        return result.Succeeded && !result.RestartRequired && !_onboard
            ? result with { Message = "Peggy maintenance completed. Choose Finish to close setup." }
            : result;
    });

    public Task<WorkIqAccountIdentity?> VerifySavedIdentityAsync(CancellationToken ct)
        => Diagnostics.RunAsync<WorkIqAccountIdentity?>("account.reuse-saved", async () =>
    {
        EnsureOnboarding();
        var state = await ReloadAndValidateAsync(ct);
        if (string.IsNullOrWhiteSpace(state.PinnedAccount) ||
            string.IsNullOrWhiteSpace(state.WorkIqEulaAcceptedVersion) ||
            !_setup.TryGetExecutablePath(out _))
        {
            Diagnostics.Event("account.reuse-saved", "skipped", "reason=no-verified-saved-setup");
            return null;
        }
        var installed = await Diagnostics.RunAsync("account.workiq-version", () => _setup.GetVersionAsync(ct));
        if (!string.Equals(installed, state.WorkIqEulaAcceptedVersion, StringComparison.Ordinal))
        {
            Diagnostics.Event("account.reuse-saved", "skipped", "reason=eula-version-changed");
            return null;
        }
        await Diagnostics.RunAsync("account.restore-eula", () => _setup.AcceptEulaAsync(installed, ct));
        var identity = await Diagnostics.RunAsync("account.verify-saved",
            () => _setup.VerifyAccountAsync(state.PinnedAccount, ct));
        if (!WorkIqSetupService.RepresentsSameAccount(state.PinnedAccount, state.PinnedMailbox,
                state.PinnedUserPrincipalName, state.PinnedPrincipalId, identity))
            throw new WorkIqSetupException("WorkIQ is signed in as a different account. Verify your account above; saved data has not changed.");
        return identity with { EulaAcceptedVersion = installed };
    });

    public Task CompleteAsync(WorkIqAccountIdentity identity, bool voiceReady, CancellationToken ct)
        => Diagnostics.RunAsync("finish.commit-setup", async () =>
    {
        EnsureOnboarding();
        AcquireGuard();
        await ReloadAndValidateAsync(ct);
        var state = new PeggyStateManager(_store);
        await Diagnostics.RunAsync("finish.load-state", () => state.LoadForSetupAsync(ct));
        var initialSetup = string.IsNullOrWhiteSpace(InitialState.PinnedAccount);
        ct.ThrowIfCancellationRequested();
        try
        {
            // Finish is the commit boundary. A late Cancel must not interrupt the
            // saved identity/EULA/settings sequence and claim that nothing changed.
            await Diagnostics.RunAsync("finish.save-identity",
                () => WorkIqSetupCompletion.ApplyIdentityAsync(state, identity, CancellationToken.None));
            if (initialSetup)
            {
                state.State.Settings.SpeechEnabled = voiceReady;
                await Diagnostics.RunAsync("finish.save-voice-preference", () => state.SaveAsync(CancellationToken.None));
            }
            Diagnostics.Run("finish.sync-account-preference", () =>
            {
                if (!PeggyDefaults.TrySynchronizeWorkIqAccount(identity.Account, out var error))
                    throw new InvalidOperationException($"The verified account was saved, but its user preference could not be updated: {error}");
            });
            _completed = true;
            Diagnostics.Event("finish.setup-saved", "completed", $"firstSetup={initialSetup}; voiceReady={voiceReady}");
        }
        finally { _expectedIdentity = IdentityStamp(state.State); }
    });

    public void Cancel() => _burn.Cancel();
    public void ReportFailure(Exception exception, bool includeExceptionMessages = true)
    {
        Diagnostics.Event("setup.state-at-failure", "recorded",
            $"committed={_committed}; completed={_completed}; restartRequired={_restart}; onboarding={_onboard}");
        _burn.LogFailure(exception, preserveExitCode: _committed, includeExceptionMessages);
    }

    public void Close(bool launch)
    {
        if (_closed) return;
        Diagnostics.Event("setup.close", "requested", $"launch={launch}; committed={_committed}; completed={_completed}");
        if (launch)
        {
            EnsureOnboarding();
            if (!_completed) throw new InvalidOperationException("Verified account setup must be saved before Peggy can start.");
        }
        // The mutex was acquired on the WPF thread and must be released there, before launch.
        Diagnostics.Run("guard.release", () => _guard?.Dispose());
        _guard = null;
        if (launch)
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Peggy", "peggy-tray.exe");
            Diagnostics.Run("finish.start-process", () =>
            {
                if (!File.Exists(target))
                    throw new InvalidOperationException("Peggy is installed, but its executable is unavailable. Open Peggy from Start after repairing the installation.");
                using var process = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(target)!
                }) ?? throw new InvalidOperationException("Setup is saved, but Peggy could not start. Open Peggy from the Start menu.");
                Diagnostics.Event("finish.child-process", "started", $"pid={process.Id}");
            });
        }
        _closed = true;
        Diagnostics.Event("setup.close", "completed", $"launchRequested={launch}");
    }

    internal async Task RunWithoutWindowAsync(IntPtr owner)
    {
        // Related-bundle removal runs inside the newer bundle's guarded transaction.
        if (!_burn.IsRelated) AcquireGuard();
        try { await _burn.ApplyAsync(owner, CancellationToken.None); }
        finally { Close(launch: false); }
    }

    private void AcquireGuard() => Diagnostics.Run("guard.acquire", () =>
    {
        _guard ??= new SingleInstanceGuard();
        if (!_guard.TryAcquire())
            throw new InvalidOperationException("Peggy is running. Choose Quit Peggy from its tray menu, then Retry. Setup will not close Peggy for you.");
    });

    private Task<PeggyStateDocument> ReloadAndValidateAsync(CancellationToken ct)
        => Diagnostics.RunAsync("state.reload-and-validate", async () =>
    {
        var current = await _store.LoadAsync(ct);
        if (!string.Equals(_expectedIdentity, IdentityStamp(current), StringComparison.Ordinal))
        {
            Diagnostics.Event("state.identity-comparison", "mismatch",
                $"stateFileExists={File.Exists(_store.Path)}; hasPinnedAccount={!string.IsNullOrWhiteSpace(current.PinnedAccount)}");
            throw new InvalidOperationException("Peggy's saved account changed after setup opened. Close and reopen this installer to protect the current account.");
        }
        return current;
    });

    private static string IdentityStamp(PeggyStateDocument state) =>
        string.Join("\n", state.PinnedAccount, state.PinnedMailbox, state.PinnedUserPrincipalName,
            state.PinnedPrincipalId, state.WorkIqEulaAcceptedVersion);

    private static void ValidateInteractiveIdentity()
    {
        if (!InteractiveIdentity.CanInstallDependencies)
            throw new InvalidOperationException("Run Peggy setup normally from your own Windows desktop, not as administrator or another user. Account sign-in is blocked in this session.");
    }

    private void EnsureOnboarding()
    {
        ValidateInteractiveIdentity();
        if (!_committed || _restart || !_onboard)
            throw new InvalidOperationException("Account setup is available only after a successful installation that does not require a restart.");
    }
}
