using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Peggy.Core;

namespace Peggy.Tray;

public partial class WorkIqSignInWindow : Window
{
    private readonly IWorkIqSetupService _setup;
    private readonly ISetupReadinessService _readiness;
    private readonly string _expectedAccount;
    private readonly string _expectedMailbox;
    private readonly string _expectedUserPrincipalName;
    private readonly string _expectedPrincipalId;
    private readonly bool _firstRun;
    private readonly bool _requireMailAccess;
    private readonly SpeechModelSetupService _speechModels;
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
        bool requireMailAccess = true)
    {
        _setup = setup;
        _readiness = readiness ?? new SetupReadinessService(setup);
        _speechModels = new SpeechModelSetupService();
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
        !_busy && _candidateIdentity is not null &&
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
            NodeStatus.Text = "Checking — Node.js";
            NpmStatus.Text = "Checking — npm";
            PowerShellStatus.Text = "Checking — PowerShell";
            CopilotCliStatus.Text = "Checking — Copilot CLI";
            InstallationStatus.Text = "Checking — standalone WorkIQ";
            var local = await _readiness.CheckLocalAsync(ct);
            ct.ThrowIfCancellationRequested();
            _local = local;
            NodeStatus.Text = local.Node.StatusText;
            NpmStatus.Text = local.Npm.StatusText;
            PowerShellStatus.Text = local.PowerShell.StatusText;
            CopilotCliStatus.Text = local.Copilot.StatusText;
            InstallationStatus.Text = local.WorkIq.StatusText;
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
    {
        if (_busy || _local?.Node.IsReady != true || _local.Npm.IsReady != true) return;
        var installed = false;
        await RunOperationAsync(async ct =>
        {
            await _setup.InstallAsync(ct);
            ct.ThrowIfCancellationRequested();
            installed = true;
            OperationStatus.Text = "WorkIQ installed. Checking local requirements...";
        }, "Installing standalone WorkIQ with npm...", "WorkIQ installation cancelled.");
        if (!_closed && installed) await CheckLocalAsync();
    }

    internal Task CheckCopilotAsync(bool signIn = false)
        => RunOperationAsync(async ct =>
        {
            _copilotAccess = null;
            CopilotAccessStatus.Text = "Checking — GitHub Copilot access";
            if (signIn) await _readiness.SignInCopilotAsync(ct);
            var result = await _readiness.CheckCopilotAccessAsync(ct);
            ct.ThrowIfCancellationRequested();
            _copilotAccess = result;
            CopilotAccessStatus.Text = result.StatusText;
            ShowDetails(result.Details);
            OperationStatus.Text = result.IsReady
                ? "GitHub Copilot is ready. No model prompt was sent."
                : result.Summary;
        }, signIn ? "Complete GitHub Copilot sign-in in the opened window." :
            "Checking the GitHub Copilot model catalog...",
            "GitHub Copilot check cancelled. Retry when ready.");

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
            AccountVerifiedStatus.Text = "Checking — Account verified: signing in and confirming identity.";
            AuthenticationAttempted = true;
            try
            {
                var identity = await _setup.SignInAsync(account, reviewedVersion, ct);
                ct.ThrowIfCancellationRequested();
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
                AccountVerifiedStatus.Text = "Ready — Account verified";
                ConnectedCard.Visibility = Visibility.Visible;
                AccountEditor.Visibility = Visibility.Collapsed;
                UpdateMonitoringSummary();
                if (_requireMailAccess) await ProbeMailAsync(ct);
                OperationStatus.Text = "Review the connected account above. Choose Finish when all required checks are ready.";
            }
            catch
            {
                if (_candidateIdentity is null)
                    AccountVerifiedStatus.Text = "Failed — Account verification did not complete. Retry sign-in.";
                throw;
            }
        }, "Checking Microsoft sign-in and permission consent. Complete any WorkIQ browser prompts.",
            "Microsoft 365 connection cancelled. Peggy's saved account has not changed.");
    }

    private static bool MatchesTypedAccount(string account, WorkIqAccountIdentity identity)
        => new[] { identity.Mail, identity.UserPrincipalName }.Any(alias =>
            string.Equals(account, alias?.Trim(), StringComparison.OrdinalIgnoreCase));

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await ConnectMicrosoftAsync();

    private async Task ProbeMailAsync(CancellationToken ct)
    {
        _mailReady = false;
        MailAccessStatus.Text = "Checking — Mail access ready: testing Inbox access without reading message bodies.";
        try
        {
            await _setup.ProbeInboxAccessAsync(_candidateIdentity!, ct);
            ct.ThrowIfCancellationRequested();
            _mailReady = true;
            MailAccessStatus.Text = "Ready — Mail access ready (Inbox access verified; no message bodies read)";
        }
        catch
        {
            MailAccessStatus.Text = "Failed — Mail access is not ready. Check permissions or mailbox availability, then Retry.";
            throw;
        }
    }

    internal Task RetryMailAsync()
    {
        if (_candidateIdentity is null || !_requireMailAccess) return Task.CompletedTask;
        return RunOperationAsync(async ct =>
        {
            await ProbeMailAsync(ct);
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
        _mailReady = false;
        ConnectedCard.Visibility = Visibility.Collapsed;
        AccountEditor.Visibility = Visibility.Visible;
        AccountVerifiedStatus.Text = "Action required — Account verified: sign in to verify your identity.";
        MailAccessStatus.Text = _requireMailAccess
            ? "Action required — Mail access ready: checked after account verification, without a second login."
            : "Optional / skipped — Mail access: Outlook monitoring is Off.";
        UpdateMonitoringSummary();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFinish) return;
        await RunOperationAsync(async ct =>
        {
            var candidate = _candidateIdentity!;
            WorkIqAccountIdentity current;
            try
            {
                current = await _setup.VerifyAccountAsync(candidate.Account, ct);
            }
            catch
            {
                ResetCandidate();
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (!WorkIqSetupService.RepresentsSameAccount(candidate.Account, candidate.Mail,
                    candidate.UserPrincipalName, candidate.PrincipalId, current))
            {
                ResetCandidate();
                throw new WorkIqSetupException("WorkIQ changed accounts. Verify your account again before finishing.");
            }
            var version = await _setup.GetVersionAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(version, candidate.EulaAcceptedVersion, StringComparison.Ordinal))
            {
                ResetCandidate();
                throw new WorkIqEulaChangedException(
                    "WorkIQ changed versions. Retry local checks and review the current EULA before signing in.");
            }
            if (IsDifferentAccount(candidate) &&
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
            DialogResult = true;
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
    {
        if (_busy || !_voiceModelDirectoryValid) return;
        var repair = VoiceModelsReady;
        _voiceSkipped = false;
        await RunOperationAsync(async ct =>
        {
            VoiceModelsStatus.Text = "Checking — Downloading and verifying optional English voice models.";
            await _speechModels.InstallAsync(_voiceModelDirectory, force: repair, ct: ct);
            RefreshVoiceModelsStatus();
            OperationStatus.Text = "Optional English voice is ready.";
        }, $"Downloading and verifying {SpeechModelSetupService.DownloadSizeDescription} of English voice models...",
            "Voice installation cancelled. You can skip voice and finish setup.");
        if (!_closed) RefreshVoiceModelsStatus();
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
            VoiceModelsStatus.Text = "Optional / skipped — PEGGY_SPEECH_MODELS is not a valid path. Correct or remove it to install voice.";
            InstallVoiceModelsButton.IsEnabled = false;
            return;
        }
        VoiceModelsReady = _speechModels.AreModelsInstalled(_voiceModelDirectory);
        VoiceModelsStatus.Text = VoiceModelsReady
            ? $"Ready — English voice models at {_voiceModelDirectory}."
            : _voiceSkipped ? "Optional / skipped — Install voice later in Settings."
            : "Optional / skipped — English voice is not installed. Install now or skip.";
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
        RetryLocalButton.IsEnabled = !_busy;
        CheckCopilotButton.IsEnabled = SignInCopilotButton.IsEnabled = !_busy && _local?.Copilot.IsReady == true;
        SignInButton.IsEnabled = !_busy && workIqReady && (!_firstRun || _local!.IsReady) &&
            validEmail && EulaCheck.IsChecked == true && _reviewedVersion == _local!.WorkIq.Version;
        ChangeAccountButton.IsEnabled = !_busy;
        RetryMailButton.Visibility = _candidateIdentity is not null && _requireMailAccess && !_mailReady
            ? Visibility.Visible : Visibility.Collapsed;
        RetryMailButton.IsEnabled = !_busy;
        InstallVoiceModelsButton.IsEnabled = !_busy && _voiceModelDirectoryValid;
        SkipVoiceButton.IsEnabled = !_busy;
        FinishButton.IsEnabled = CanFinish;
        CancelButton.Content = _busy ? "Cancel operation" : "Cancel";
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task> action, string status, string cancelled)
    {
        if (_busy || _closed) return;
        _busy = true;
        OperationStatus.Text = status;
        UpdateControls();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _activeOperationCancellation = operation;
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_closed)
            {
                MarkInterruptedChecks("Action required — Check cancelled; retry when ready.");
                OperationStatus.Text = cancelled;
            }
        }
        catch (Exception ex) when (SetupReadinessService.IsExpectedFailure(ex))
        {
            if (!_closed)
            {
                if (ex is WorkIqEulaChangedException)
                {
                    EulaCheck.IsChecked = false;
                    _local = null;
                }
                MarkInterruptedChecks("Failed — Check did not complete. Retry; see technical details.");
                OperationStatus.Text = $"Failed — {ex.Message}";
                ShowDetails(ex.ToString());
            }
        }
        finally
        {
            _activeOperationCancellation = null;
            _busy = false;
            UpdateControls();
        }
    }

    private void MarkInterruptedChecks(string status)
    {
        foreach (var text in new[] { NodeStatus, NpmStatus, PowerShellStatus, CopilotCliStatus,
            InstallationStatus, CopilotAccessStatus, AccountVerifiedStatus, MailAccessStatus })
        {
            if (text.Text.StartsWith("Checking", StringComparison.Ordinal)) text.Text = status;
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
        if (_busy)
        {
            _activeOperationCancellation?.Cancel();
            return;
        }
        DialogResult = false;
    }
}
