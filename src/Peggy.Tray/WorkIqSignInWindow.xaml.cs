using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Peggy.Core;

namespace Peggy.Tray;

public partial class WorkIqSignInWindow : Window
{
    private readonly IWorkIqSetupService _setup;
    private readonly ISetupReadinessService _readiness;
    private string _expectedAccount;
    private string _expectedMailbox;
    private string _expectedUserPrincipalName;
    private string _expectedPrincipalId;
    private bool _firstRun;
    private bool _requireMailAccess;
    private readonly SpeechModelSetupService _speechModels;
    private readonly Func<string, bool, CancellationToken, Task> _installVoice;
    private readonly Func<string, bool> _voiceInstalled;
    private readonly string _voiceModelDirectory;
    private readonly bool _voiceModelDirectoryValid;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _activeOperationCancellation;
    private SetupLocalReadiness? _local;
    private SetupCheck? _copilotAccess;
    private WorkIqAccountIdentity? _candidateIdentity;
    private string _reviewedVersion = string.Empty;
    private bool _mailReady;
    private bool _voiceSkipped;
    private bool _busy;
    private bool _initialized;
    private bool _closed;
    private int _operationGeneration;
    private Uri? _gitHubVerificationUri;
    private string _cancelledStatus = string.Empty;

    public WorkIqSignInWindow(
        string? expectedAccount = null,
        WorkIqSetupService? setup = null,
        string? expectedMailbox = null,
        string? expectedUserPrincipalName = null,
        string? expectedPrincipalId = null,
        bool requireMailAccess = true)
        : this(expectedAccount, setup ?? new WorkIqSetupService(), null,
            previewMode: false, expectedMailbox, expectedUserPrincipalName,
            expectedPrincipalId, requireMailAccess)
    {
    }

    internal WorkIqSignInWindow(
        string? expectedAccount, WorkIqSetupService? setup, bool previewMode)
        : this(expectedAccount, setup ?? new WorkIqSetupService(), null, previewMode)
    {
    }

    internal WorkIqSignInWindow(
        string? expectedAccount,
        IWorkIqSetupService setup,
        ISetupReadinessService? readiness,
        bool previewMode,
        string? expectedMailbox = null,
        string? expectedUserPrincipalName = null,
        string? expectedPrincipalId = null,
        bool requireMailAccess = true,
        Func<string, bool, CancellationToken, Task>? installVoice = null,
        Func<string, bool>? voiceInstalled = null)
    {
        _setup = setup;
        _readiness = readiness ?? new SetupReadinessService(setup);
        _speechModels = new SpeechModelSetupService();
        _installVoice = installVoice ?? ((directory, repair, ct) =>
            _speechModels.InstallAsync(directory, force: repair, ct: ct));
        _voiceInstalled = voiceInstalled ?? _speechModels.AreModelsInstalled;
        _voiceModelDirectoryValid = PeggyDefaults.TryResolveSpeechModelDirectory(
            configuredDirectory: null, out _voiceModelDirectory);
        _expectedAccount = expectedAccount?.Trim() ?? string.Empty;
        _expectedMailbox = expectedMailbox?.Trim() ?? string.Empty;
        _expectedUserPrincipalName = expectedUserPrincipalName?.Trim() ?? string.Empty;
        _expectedPrincipalId = expectedPrincipalId?.Trim() ?? string.Empty;
        _firstRun = _expectedAccount.Length == 0 && _expectedMailbox.Length == 0;
        _requireMailAccess = _firstRun || requireMailAccess;
        InitializeComponent();
        _initialized = true;
        // Environment configuration is only a preference, never verified identity.
        AccountText.Text = _expectedAccount.Length > 0 ? _expectedAccount : _expectedMailbox;
        FirstRunRequirements.Visibility = _firstRun ? Visibility.Visible : Visibility.Collapsed;
        CopilotAccessGroup.Visibility = _firstRun ? Visibility.Visible : Visibility.Collapsed;
        VoiceSetupGroup.Visibility = _firstRun ? Visibility.Visible : Visibility.Collapsed;
        if (!_requireMailAccess)
        {
            MailAccessStatus.Text = "Optional / skipped — Mail access: Outlook monitoring is Off.";
            MailAccessIndicator.State = SetupStepState.Skipped;
        }
        UpdateMonitoringSummary();
        RefreshVoiceModelsStatus();
        UpdateControls();
        Loaded += (_, _) => PeggyWindowLayout.ConstrainToWorkArea(this);
        if (!previewMode)
        {
            Loaded += async (_, _) => await CheckLocalAsync();
        }
        Closed += (_, _) =>
        {
            _closed = true;
            _operationGeneration++;
            ClearGitHubChallenge();
            MarkInterruptedChecks(SetupStepState.Idle, "Cancelled — Setup closed.");
            _lifetimeCancellation.Cancel();
            _activeOperationCancellation?.Cancel();
            _lifetimeCancellation.Dispose();
        };
    }

    public string? AuthenticatedAccount { get; private set; }
    public string? AuthenticatedMailbox { get; private set; }
    public string? AcceptedEulaVersion { get; private set; }
    public WorkIqAccountIdentity? AuthenticatedIdentity { get; private set; }
    public bool AuthenticationAttempted { get; private set; }
    public bool VoiceModelsReady { get; private set; }

    internal bool CanFinish =>
        InstallerAllowsOnboarding && !_busy && _candidateIdentity is not null &&
        (!_requireMailAccess || _mailReady) &&
        _local?.WorkIq.IsReady == true &&
        string.Equals(_candidateIdentity.EulaAcceptedVersion,
            _local.WorkIq.Version, StringComparison.Ordinal) &&
        (!_firstRun || (_local.IsReady && _copilotAccess?.IsReady == true));

    internal void SetPreviewInstallationState(string installationStatus, string operationStatus)
    {
        InstallationStatus.Text = installationStatus;
        OperationStatus.Text = operationStatus;
        InstallButton.Visibility = Visibility.Collapsed;
    }

    internal Task CheckLocalAsync()
        => RunOperationAsync(async ct =>
        {
            var previousVersion = _local?.WorkIq.Version;
            _local = null;
            SetStep(NodeIndicator, NodeStatus, SetupStepState.Running, "Checking — Node.js");
            SetStep(NpmIndicator, NpmStatus, SetupStepState.Running, "Checking — npm");
            SetStep(PowerShellIndicator, PowerShellStatus, SetupStepState.Running, "Checking — PowerShell");
            SetStep(CopilotCliIndicator, CopilotCliStatus, SetupStepState.Running, "Checking — Copilot CLI");
            SetStep(InstallationIndicator, InstallationStatus, SetupStepState.Running, "Checking — standalone WorkIQ");
            var local = await _readiness.CheckLocalAsync(ct, CreateProgress<SetupLocalCheckUpdate>(ct, ApplyLocalCheck));
            ct.ThrowIfCancellationRequested();
            _local = local;
            ApplyLocalCheck(new(SetupLocalCheck.Node, local.Node));
            ApplyLocalCheck(new(SetupLocalCheck.Npm, local.Npm));
            ApplyLocalCheck(new(SetupLocalCheck.PowerShell, local.PowerShell));
            ApplyLocalCheck(new(SetupLocalCheck.Copilot, local.Copilot));
            ApplyLocalCheck(new(SetupLocalCheck.WorkIq, local.WorkIq));
            if (!string.Equals(previousVersion, local.WorkIq.Version, StringComparison.Ordinal))
            {
                EulaCheck.IsChecked = false;
                if (_candidateIdentity is not null &&
                    _candidateIdentity.EulaAcceptedVersion != local.WorkIq.Version)
                {
                    ResetCandidate();
                }
            }
            EulaVersionStatus.Text = local.WorkIq.IsReady
                ? $"Review the WorkIQ EULA for installed version {local.WorkIq.Version}. Acceptance applies only to this exact version."
                : "Check the WorkIQ version before reviewing its terms.";
            ShowDetails(string.Join("\n\n", local.Checks
                .Where(check => check.Details.Length > 0)
                .Select(check => $"{check.StatusText}\n{check.Details}")));
            OperationStatus.Text = local.IsReady
                ? "Local requirements are ready. Check GitHub Copilot access and connect Microsoft 365."
                : "Review the local requirements above. Use setup.exe to repair prerequisites, then Retry.";
        }, "Checking local requirements...", "Local checks cancelled. Retry to continue.");

    private async void RetryLocal_Click(object sender, RoutedEventArgs e) => await CheckLocalAsync();

    private async void Install_Click(object sender, RoutedEventArgs e)
        => await InstallWorkIqAsync();

    internal async Task InstallWorkIqAsync()
    {
        if (_busy || _local?.Node.IsReady != true || _local.Npm.IsReady != true) return;
        var installed = false;
        await RunOperationAsync(async ct =>
        {
            SetStep(InstallationIndicator, InstallationStatus, SetupStepState.Running,
                "Installing — Standalone WorkIQ with npm.");
            await _setup.InstallAsync(ct);
            ct.ThrowIfCancellationRequested();
            SetStep(InstallationIndicator, InstallationStatus, SetupStepState.Succeeded, "Ready — WorkIQ installed.");
            installed = true;
            OperationStatus.Text = "WorkIQ installed. Checking local requirements...";
        }, "Installing standalone WorkIQ with npm...", "WorkIQ installation cancelled.");
        if (!_closed && installed) await CheckLocalAsync();
    }

    internal Task CheckCopilotAsync(bool signIn = false)
        => RunOperationAsync(async ct =>
        {
            _copilotAccess = null;
            ClearGitHubChallenge();
            SetStep(CopilotAccessIndicator, CopilotAccessStatus, SetupStepState.Running,
                signIn ? "Starting — GitHub device sign-in." : "Checking — GitHub Copilot model catalog.");
            if (signIn)
            {
                var acceptingChallenges = true;
                var challengeReceived = false;
                try
                {
                    await _readiness.SignInCopilotAsync(ct,
                        CreateProgress<CopilotDeviceCodeChallenge>(ct, challenge =>
                        {
                            if (acceptingChallenges)
                                challengeReceived |= ShowGitHubChallenge(challenge);
                        }));
                }
                finally { acceptingChallenges = false; }
                ct.ThrowIfCancellationRequested();
                ClearGitHubChallenge();
                SetStep(CopilotAccessIndicator, CopilotAccessStatus, SetupStepState.Running,
                    challengeReceived ? "Checking — GitHub Copilot model catalog."
                        : "Checking — Verifying existing GitHub Copilot access.");
                OperationStatus.Text = challengeReceived
                    ? "Checking GitHub Copilot access after sign-in. No model prompt is sent."
                    : "Verifying existing GitHub Copilot access using the model catalog. No model prompt is sent.";
            }
            var result = await _readiness.CheckCopilotAccessAsync(ct);
            _installer?.Diagnostics.Event("copilot.access", result.IsReady ? "ready" : "not-ready",
                signIn ? null : result.Summary);
            ct.ThrowIfCancellationRequested();
            _copilotAccess = result;
            SetStep(CopilotAccessIndicator, CopilotAccessStatus, StateFor(result), result.StatusText);
            ShowDetails(signIn ? "" : result.Details);
            OperationStatus.Text = result.IsReady
                ? "GitHub Copilot is ready. No model prompt was sent."
                : result.Summary;
        }, signIn ? "Starting GitHub Copilot device sign-in..." :
            "Checking the GitHub Copilot model catalog...",
            "GitHub Copilot check cancelled. Retry when ready.", hideFailureDetails: signIn);

    private async void CheckCopilot_Click(object sender, RoutedEventArgs e)
        => await CheckCopilotAsync();

    private async void SignInCopilot_Click(object sender, RoutedEventArgs e)
        => await CheckCopilotAsync(signIn: true);

    internal Task ConnectMicrosoftAsync()
    {
        if (_busy || !SignInButton.IsEnabled) return Task.CompletedTask;
        var account = AccountText.Text.Trim();
        var reviewedVersion = _reviewedVersion;
        ResetCandidate();
        return RunOperationAsync(async ct =>
        {
            SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Running,
                "Checking — The accepted WorkIQ EULA version.");
            AuthenticationAttempted = true;
            try
            {
                var acceptingStages = true;
                WorkIqAccountIdentity identity;
                try
                {
                    identity = await _setup.SignInAsync(account, reviewedVersion, ct,
                        CreateProgress<WorkIqSignInStage>(ct, stage =>
                        {
                            if (acceptingStages) ApplyMicrosoftStage(stage);
                        }));
                }
                finally { acceptingStages = false; }
                ct.ThrowIfCancellationRequested();
                SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Running,
                    "Checking — Account verified: confirming the returned identity.");
                if (!MatchesTypedAccount(account, identity) ||
                    string.IsNullOrWhiteSpace(identity.PrincipalId) ||
                    !string.Equals(identity.EulaAcceptedVersion, reviewedVersion, StringComparison.Ordinal))
                {
                    throw new WorkIqSetupException(
                        "The returned identity or accepted WorkIQ version did not match. Sign in again.");
                }
                _candidateIdentity = identity;
                ConnectedName.Text = string.IsNullOrWhiteSpace(identity.DisplayName)
                    ? identity.Mail ?? identity.Account : identity.DisplayName;
                ConnectedEmail.Text = identity.Mail ?? identity.UserPrincipalName ?? identity.Account;
                SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Succeeded,
                    "Ready — Microsoft sign-in");
                SetStep(MicrosoftConsentIndicator, MicrosoftConsentStatus, SetupStepState.Succeeded,
                    "Ready — Permission consent");
                SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Succeeded,
                    "Ready — Account verified");
                ConnectedCard.Visibility = Visibility.Visible;
                AccountEditor.Visibility = Visibility.Collapsed;
                UpdateMonitoringSummary();
                if (_requireMailAccess) await ProbeMailAsync(ct);
                ct.ThrowIfCancellationRequested();
                OperationStatus.Text = "Review the connected account above. Choose Finish when all required checks are ready.";
            }
            catch
            {
                if (!_closed && !ct.IsCancellationRequested && _candidateIdentity is null)
                    SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Failed,
                        "Failed — Account verification did not complete. Retry sign-in.");
                throw;
            }
        }, "Checking the accepted WorkIQ EULA version before Microsoft sign-in.",
            "Microsoft 365 connection cancelled. Peggy's saved account has not changed.");
    }

    private static bool MatchesTypedAccount(string account, WorkIqAccountIdentity identity)
        => new[] { identity.Mail, identity.UserPrincipalName }.Any(alias =>
            string.Equals(account, alias?.Trim(), StringComparison.OrdinalIgnoreCase));

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await ConnectMicrosoftAsync();

    private async Task ProbeMailAsync(CancellationToken ct)
    {
        _mailReady = false;
        SetStep(MailAccessIndicator, MailAccessStatus, SetupStepState.Running,
            "Checking — Mail access ready: testing Inbox access without reading message bodies.");
        try
        {
            await TraceSetupStepAsync("account.probe-inbox",
                () => _setup.ProbeInboxAccessAsync(_candidateIdentity!, ct));
            ct.ThrowIfCancellationRequested();
            _mailReady = true;
            SetStep(MailAccessIndicator, MailAccessStatus, SetupStepState.Succeeded,
                "Ready — Mail access ready (Inbox access verified; no message bodies read)");
        }
        catch
        {
            if (!_closed && !ct.IsCancellationRequested)
                SetStep(MailAccessIndicator, MailAccessStatus, SetupStepState.Failed,
                    "Failed — Mail access is not ready. Check permissions or mailbox availability, then Retry.");
            throw;
        }
    }

    internal Task RetryMailAsync()
    {
        if (_candidateIdentity is null || !_requireMailAccess) return Task.CompletedTask;
        return RunOperationAsync(async ct =>
        {
            await ProbeMailAsync(ct);
            ct.ThrowIfCancellationRequested();
            OperationStatus.Text = "Mail access is ready. Review your account, then choose Finish.";
        }, "Checking mail access for the verified account...", "Mail access check cancelled. Retry to continue.");
    }

    private async void RetryMail_Click(object sender, RoutedEventArgs e) => await RetryMailAsync();

    internal void ChangeAccount()
    {
        if (_busy) return;
        ResetCandidate();
        AccountText.SelectAll();
        AccountText.Focus();
        OperationStatus.Text = "Enter your work email and sign in. Peggy's saved account changes only after Finish.";
        UpdateControls();
    }

    private void ChangeAccount_Click(object sender, RoutedEventArgs e) => ChangeAccount();

    private void ResetCandidate()
    {
        _candidateIdentity = null;
        ConfirmInstallerAccountSwitch.IsChecked = false;
        _mailReady = false;
        ConnectedCard.Visibility = Visibility.Collapsed;
        AccountEditor.Visibility = Visibility.Visible;
        SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Idle, "Not started — Microsoft sign-in");
        SetStep(MicrosoftConsentIndicator, MicrosoftConsentStatus, SetupStepState.Idle, "Not started — Permission consent");
        SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Idle,
            "Action required — Account verified: sign in to verify your identity.");
        SetStep(MailAccessIndicator, MailAccessStatus, _requireMailAccess ? SetupStepState.Idle : SetupStepState.Skipped, _requireMailAccess
            ? "Action required — Mail access ready: checked after account verification, without a second login."
            : "Optional / skipped — Mail access: Outlook monitoring is Off.");
        UpdateMonitoringSummary();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (FinishInstallerWithoutOnboarding()) return;
        if (!CanFinish) return;
        await RunOperationAsync(async ct =>
        {
            var candidate = _candidateIdentity!;
            SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Running,
                "Checking — Confirming the connected account before Finish.");
            WorkIqAccountIdentity current;
            try
            {
                current = await TraceSetupStepAsync("finish.verify-account",
                    () => _setup.VerifyAccountAsync(candidate.Account, ct));
            }
            catch
            {
                if (!_closed && !ct.IsCancellationRequested)
                {
                    ResetCandidate();
                    SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Failed,
                        "Failed — Account verification did not complete. Retry sign-in.");
                }
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (!WorkIqSetupService.RepresentsSameAccount(candidate.Account, candidate.Mail,
                    candidate.UserPrincipalName, candidate.PrincipalId, current))
            {
                _installer?.Diagnostics.Event("finish.compare-account", "mismatch");
                ResetCandidate();
                SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Failed,
                    "Failed — WorkIQ changed accounts. Retry sign-in.");
                throw new WorkIqSetupException("WorkIQ changed accounts. Verify your account again before finishing.");
            }
            _installer?.Diagnostics.Event("finish.compare-account", "matched");
            var version = await TraceSetupStepAsync("finish.workiq-version",
                () => _setup.GetVersionAsync(ct));
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(version, candidate.EulaAcceptedVersion, StringComparison.Ordinal))
            {
                _installer?.Diagnostics.Event("finish.compare-eula-version", "mismatch");
                ResetCandidate();
                throw new WorkIqEulaChangedException(
                    "WorkIQ changed versions. Retry local checks and review the current EULA before signing in.");
            }
            _installer?.Diagnostics.Event("finish.compare-eula-version", "matched");
            SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Succeeded, "Ready — Account verified");
            if (IsDifferentAccount(candidate) && _installer is not null &&
                ConfirmInstallerAccountSwitch.IsChecked != true)
            {
                _installer.Diagnostics.Event("finish.confirm-account-switch", "awaiting-user");
                OperationStatus.Text = "Review and confirm the account switch above before choosing Finish.";
                return;
            }
            if (IsDifferentAccount(candidate) && _installer is null &&
                MessageBox.Show(this,
                    $"Switch Peggy from {_expectedAccount} to {candidate.Mail ?? candidate.Account}?\n\n" +
                    "Peggy will clear the current account's local alerts, monitoring history, selected " +
                    "Outlook folders and Teams, Copilot conversation context, and action history. " +
                    "General app and speech settings will be kept.",
                    "Switch Microsoft 365 account", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                OperationStatus.Text = "Account switch cancelled. Peggy's saved account has not changed.";
                return;
            }
            AuthenticatedAccount = candidate.Account;
            AuthenticatedMailbox = candidate.Mail;
            AcceptedEulaVersion = candidate.EulaAcceptedVersion;
            AuthenticatedIdentity = candidate;
            if (_installer is null) DialogResult = true;
            else
            {
                var voiceReady = _installer.Diagnostics.Run("finish.check-voice-files", RevalidateVoiceModels);
                await TraceSetupStepAsync("finish.save-setup",
                    () => _installer.CompleteAsync(candidate, voiceReady, ct));
                _installer.Diagnostics.Run("finish.launch", () => _installer.Close(launch: true));
                _installerFinished = true;
                Close();
            }
        }, "Confirming the verified account before finishing...", "Finish cancelled. Peggy's saved account has not changed.");
    }

    private bool IsDifferentAccount(WorkIqAccountIdentity identity)
        => !_firstRun && !WorkIqSetupService.RepresentsSameAccount(
            _expectedAccount, _expectedMailbox, _expectedUserPrincipalName, _expectedPrincipalId, identity);

    private void UpdateMonitoringSummary()
        => MonitoringSummary.Text = _firstRun
            ? "Default monitoring: Inbox only + Chats only. Change this later in Settings."
            : _candidateIdentity is not null && IsDifferentAccount(_candidateIdentity)
            ? "Your existing monitoring modes are retained. Account-specific folder and team selections are cleared when you switch accounts."
            : "Your existing monitoring settings are kept when reconnecting the same account.";

    private async void InstallVoiceModels_Click(object sender, RoutedEventArgs e)
        => await InstallVoiceModelsAsync();

    internal async Task InstallVoiceModelsAsync()
    {
        if (_busy || !_voiceModelDirectoryValid) return;
        var repair = VoiceModelsReady;
        _voiceSkipped = false;
        await RunOperationAsync(async ct =>
        {
            SetStep(VoiceModelsIndicator, VoiceModelsStatus, SetupStepState.Running,
                "Downloading — Verifying optional English voice models.");
            await _installVoice(_voiceModelDirectory, repair, ct);
            ct.ThrowIfCancellationRequested();
            RefreshVoiceModelsStatus();
            OperationStatus.Text = "Optional English voice is ready.";
        }, $"Downloading and verifying {SpeechModelSetupService.DownloadSizeDescription} of English voice models...",
            "Voice installation cancelled. You can skip voice and finish setup.");
    }

    private void SkipVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _voiceSkipped = true;
        RefreshVoiceModelsStatus();
        OperationStatus.Text = "Optional English voice skipped. You can install it later in Settings.";
    }

    private void RefreshVoiceModelsStatus()
    {
        if (!_voiceModelDirectoryValid)
        {
            VoiceModelsReady = false;
            SetStep(VoiceModelsIndicator, VoiceModelsStatus, SetupStepState.Skipped,
                "Optional / skipped — PEGGY_SPEECH_MODELS is not a valid path. Correct or remove it to install voice.");
            InstallVoiceModelsButton.IsEnabled = false;
            return;
        }
        VoiceModelsReady = _voiceInstalled(_voiceModelDirectory);
        SetStep(VoiceModelsIndicator, VoiceModelsStatus, VoiceModelsReady ? SetupStepState.Succeeded : SetupStepState.Skipped, VoiceModelsReady
            ? $"Ready — English voice models at {_voiceModelDirectory}."
            : _voiceSkipped ? "Optional / skipped — Install voice later in Settings."
            : "Optional / skipped — English voice is not installed. Install now or skip.");
        InstallVoiceModelsButton.Content = VoiceModelsReady ? "Repair voice models" : "Install voice models";
    }

    internal bool RevalidateVoiceModels()
    {
        RefreshVoiceModelsStatus();
        return VoiceModelsReady;
    }

    private void AccountText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_initialized) return;
        if (_candidateIdentity is not null) ResetCandidate();
        UpdateControls();
    }

    private void Eula_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _reviewedVersion = EulaCheck.IsChecked == true ? _local?.WorkIq.Version ?? "" : "";
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (!_initialized || _closed) return;
        var validEmail = WorkIqSetupService.IsValidAccount(AccountText.Text);
        EmailValidation.Text = validEmail ? "Your identity will be verified against this email."
            : string.IsNullOrWhiteSpace(AccountText.Text) ? "Enter your work email address."
            : "Enter a valid work email, such as name@company.com.";
        AccountChangeNotice.Visibility = _firstRun ? Visibility.Collapsed : Visibility.Visible;
        AccountChangeNotice.Text = "Changing accounts clears local account-specific data only after verification and your final confirmation.";
        var workIqReady = _local?.WorkIq.IsReady == true;
        AccountText.IsEnabled = !_busy;
        EulaCheck.IsEnabled = !_busy && workIqReady;
        ReviewEulaButton.IsEnabled = !_busy;
        InstallButton.IsEnabled = !_busy && _local?.Node.IsReady == true && _local.Npm.IsReady;
        InstallButton.Visibility = workIqReady ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = InstallationIndicator.State == SetupStepState.Failed
            ? "Install WorkIQ / Retry" : "Install WorkIQ with npm";
        RetryLocalButton.IsEnabled = !_busy;
        CheckCopilotButton.IsEnabled = SignInCopilotButton.IsEnabled = !_busy && _local?.Copilot.IsReady == true;
        SignInButton.IsEnabled = !_busy && workIqReady && (!_firstRun || _local!.IsReady) &&
            validEmail && EulaCheck.IsChecked == true && _reviewedVersion == _local!.WorkIq.Version;
        SignInButton.Content = AccountVerifiedIndicator.State == SetupStepState.Failed ||
            MicrosoftSignInIndicator.State == SetupStepState.Failed || MicrosoftConsentIndicator.State == SetupStepState.Failed
            ? "Retry Microsoft sign-in" : "Sign in with Microsoft";
        ChangeAccountButton.IsEnabled = !_busy;
        RetryMailButton.Visibility = _candidateIdentity is not null && _requireMailAccess && !_mailReady
            ? Visibility.Visible : Visibility.Collapsed;
        RetryMailButton.IsEnabled = !_busy;
        InstallVoiceModelsButton.IsEnabled = !_busy && _voiceModelDirectoryValid;
        if (VoiceModelsIndicator.State == SetupStepState.Failed)
            InstallVoiceModelsButton.Content = "Retry voice installation";
        SkipVoiceButton.IsEnabled = !_busy;
        FinishButton.IsEnabled = CanFinish;
        CancelButton.Content = _busy ? "Cancel operation" : "Cancel";
        UpdateInstallerControls();
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task> action, string status, string cancelled, bool hideFailureDetails = false,
        [System.Runtime.CompilerServices.CallerMemberName] string stage = "")
    {
        if (_busy || _closed || !InstallerAllowsOnboarding) return;
        _busy = true;
        _operationGeneration++;
        _cancelledStatus = cancelled;
        ShowDetails("");
        OperationStatus.Text = status;
        UpdateControls();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _activeOperationCancellation = operation;
        try
        {
            if (_installer is null) await action(operation.Token);
            else await _installer.Diagnostics.RunAsync($"ui.{stage}",
                () => action(operation.Token), includeExceptionMessages: !hideFailureDetails);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_closed)
            {
                MarkInterruptedChecks(SetupStepState.Idle, "Cancelled — Retry when ready.");
                OperationStatus.Text = cancelled;
            }
        }
        catch (Exception ex) when (SetupReadinessService.IsExpectedFailure(ex))
        {
            _installer?.ReportFailure(ex, includeExceptionMessages: !hideFailureDetails);
            if (!_closed)
            {
                if (ex is WorkIqEulaChangedException)
                {
                    EulaCheck.IsChecked = false;
                    _local = null;
                }
                var failure = hideFailureDetails
                    ? "GitHub sign-in did not complete. Retry with a new code. This setup flow supports GitHub.com only. If device login is unsupported, update GitHub Copilot CLI using setup.exe, then retry."
                    : ex.Message;
                MarkInterruptedChecks(SetupStepState.Failed, $"Failed — {failure}");
                OperationStatus.Text = $"Failed — {failure}";
                ShowDetails(hideFailureDetails ? "" : ex.ToString());
            }
        }
        finally
        {
            _operationGeneration++;
            ClearGitHubChallenge();
            MarkInterruptedChecks(SetupStepState.Failed, "Failed — Check did not complete. Retry when ready.");
            _activeOperationCancellation = null;
            _busy = false;
            UpdateControls();
        }
    }

    private Task<T> TraceSetupStepAsync<T>(string stage, Func<Task<T>> action)
        => _installer is null ? action() : _installer.Diagnostics.RunAsync(stage, action);

    private Task TraceSetupStepAsync(string stage, Func<Task> action)
        => _installer is null ? action() : _installer.Diagnostics.RunAsync(stage, action);

    private void MarkInterruptedChecks(SetupStepState state, string status)
    {
        foreach (var (indicator, text) in new[]
        {
            (NodeIndicator, NodeStatus), (NpmIndicator, NpmStatus), (PowerShellIndicator, PowerShellStatus),
            (CopilotCliIndicator, CopilotCliStatus), (InstallationIndicator, InstallationStatus),
            (CopilotAccessIndicator, CopilotAccessStatus), (MicrosoftSignInIndicator, MicrosoftSignInStatus),
            (MicrosoftConsentIndicator, MicrosoftConsentStatus), (AccountVerifiedIndicator, AccountVerifiedStatus),
            (MailAccessIndicator, MailAccessStatus), (VoiceModelsIndicator, VoiceModelsStatus)
        })
        {
            if (indicator.State is SetupStepState.Running or SetupStepState.WaitingForUser)
                SetStep(indicator, text, state, status);
        }
    }

    private static void SetStep(SetupStepIndicator indicator, TextBlock text, SetupStepState state, string status)
    {
        indicator.State = state;
        text.Text = status;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(text,
            System.Windows.Automation.AutomationLiveSetting.Polite);
    }

    private static SetupStepState StateFor(SetupCheck check) => check.State switch
    {
        SetupCheckState.Checking => SetupStepState.Running,
        SetupCheckState.Ready => SetupStepState.Succeeded,
        SetupCheckState.Skipped => SetupStepState.Skipped,
        _ => SetupStepState.Failed
    };

    private void ApplyLocalCheck(SetupLocalCheckUpdate update)
    {
        var (indicator, text) = update.Check switch
        {
            SetupLocalCheck.Node => (NodeIndicator, NodeStatus),
            SetupLocalCheck.Npm => (NpmIndicator, NpmStatus),
            SetupLocalCheck.PowerShell => (PowerShellIndicator, PowerShellStatus),
            SetupLocalCheck.Copilot => (CopilotCliIndicator, CopilotCliStatus),
            _ => (InstallationIndicator, InstallationStatus)
        };
        SetStep(indicator, text, StateFor(update.Result), update.Result.StatusText);
    }

    private void ApplyMicrosoftStage(WorkIqSignInStage stage)
    {
        switch (stage)
        {
            case WorkIqSignInStage.AcceptingEula:
                SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Running,
                    "Checking — The accepted WorkIQ EULA version.");
                break;
            case WorkIqSignInStage.SigningIn:
                SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.WaitingForUser,
                    "Waiting for you — Complete Microsoft sign-in if prompted.");
                OperationStatus.Text = "Complete Microsoft sign-in if prompted. Peggy is waiting for WorkIQ to finish.";
                break;
            case WorkIqSignInStage.Consenting:
                SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Succeeded,
                    "Ready — Microsoft sign-in");
                SetStep(MicrosoftConsentIndicator, MicrosoftConsentStatus, SetupStepState.WaitingForUser,
                    "Waiting for you — Review Microsoft 365 permission consent if prompted.");
                OperationStatus.Text = "Review Microsoft 365 permission consent if prompted.";
                break;
            case WorkIqSignInStage.VerifyingAccount:
                SetStep(MicrosoftConsentIndicator, MicrosoftConsentStatus, SetupStepState.Succeeded,
                    "Ready — Permission consent");
                SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Running,
                    "Checking — Account verified: matching your work email to the signed-in profile.");
                OperationStatus.Text = "Verifying the signed-in Microsoft 365 account.";
                break;
        }
    }

    private IProgress<T> CreateProgress<T>(CancellationToken ct, Action<T> update)
    {
        var generation = _operationGeneration;
        return new CallbackProgress<T>(value =>
        {
            void Deliver()
            {
                if (!_closed && !ct.IsCancellationRequested && generation == _operationGeneration)
                    update(value);
            }
            if (Dispatcher.CheckAccess()) Deliver();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Deliver));
        });
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    internal static bool IsAllowedGitHubVerificationUri(Uri? uri)
        => uri is { IsAbsoluteUri: true } &&
            string.Equals(uri.OriginalString, "https://github.com/login/device", StringComparison.Ordinal);

    private bool ShowGitHubChallenge(CopilotDeviceCodeChallenge challenge)
    {
        if (!IsAllowedGitHubVerificationUri(challenge.VerificationUri) ||
            !Regex.IsMatch(challenge.UserCode ?? "", @"\A[A-Z0-9]{4}-[A-Z0-9]{4}\z",
                RegexOptions.CultureInvariant))
        {
            return false;
        }
        _gitHubVerificationUri = challenge.VerificationUri;
        GitHubDeviceCodeText.Text = challenge.UserCode;
        GitHubDeviceCodeCard.Visibility = Visibility.Visible;
        GitHubDeviceCodeText.IsEnabled = CopyGitHubCodeButton.IsEnabled = OpenGitHubButton.IsEnabled = true;
        SetStep(CopilotAccessIndicator, CopilotAccessStatus, SetupStepState.WaitingForUser,
            "Waiting for you — Enter the device code on GitHub. Complete sign-in if prompted.");
        OperationStatus.Text = "Complete GitHub device sign-in, then return here. Access will be checked automatically.";
        return true;
    }

    private void ClearGitHubChallenge()
    {
        _gitHubVerificationUri = null;
        GitHubDeviceCodeText.Clear();
        GitHubDeviceCodeText.IsEnabled = CopyGitHubCodeButton.IsEnabled = OpenGitHubButton.IsEnabled = false;
        GitHubDeviceCodeCard.Visibility = Visibility.Collapsed;
    }

    internal void CopyGitHubCode(Action<string> copy)
    {
        if (!_closed && _busy && CopyGitHubCodeButton.IsEnabled &&
            _activeOperationCancellation?.IsCancellationRequested == false &&
            IsAllowedGitHubVerificationUri(_gitHubVerificationUri))
            copy(GitHubDeviceCodeText.Text);
    }

    private void CopyGitHubCode_Click(object sender, RoutedEventArgs e)
    {
        try { CopyGitHubCode(Clipboard.SetText); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            OperationStatus.Text = "Could not copy the code. Select it and copy it manually.";
        }
    }

    internal void OpenGitHub(Action<Uri> open)
    {
        if (!_closed && _busy && OpenGitHubButton.IsEnabled &&
            _activeOperationCancellation?.IsCancellationRequested == false &&
            IsAllowedGitHubVerificationUri(_gitHubVerificationUri))
            open(_gitHubVerificationUri!);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenGitHub(uri => Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            }));
        }
        catch (Win32Exception)
        {
            OperationStatus.Text = "Could not open GitHub. Open https://github.com/login/device and enter the displayed code.";
        }
    }

    private void ShowDetails(string details)
    {
        DetailsText.Text = details;
        TechnicalDetails.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsText.Text); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            OperationStatus.Text = "Could not copy details. Select the text and copy it manually.";
        }
    }

    private void ReviewEula_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = WorkIqSetupService.EulaUrl, UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            OperationStatus.Text = $"Could not open the WorkIQ EULA: {ex.Message}";
            ShowDetails(WorkIqSetupService.EulaUrl);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_installer is not null && HandleInstallerCancel()) return;
        if (_busy)
        {
            CancelActiveOperation();
            return;
        }
        DialogResult = false;
    }

    internal void CancelActiveOperation()
    {
        _operationGeneration++;
        _activeOperationCancellation?.Cancel();
        ClearGitHubChallenge();
        MarkInterruptedChecks(SetupStepState.Idle, "Cancelled — Retry when ready.");
        OperationStatus.Text = _cancelledStatus;
    }
}
