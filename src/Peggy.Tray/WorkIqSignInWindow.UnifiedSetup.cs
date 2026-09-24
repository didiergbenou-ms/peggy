using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Peggy.Core;

namespace Peggy.Tray;

public partial class WorkIqSignInWindow
{
    private IUnifiedSetupHost? _installer;
    private bool _installerBusy;
    private bool _installerCommitted;
    private bool _installerRestart;
    private bool _installerFinished;
    private bool _requirementsReady;
    private CancellationTokenSource? _installerCancellation;

    private bool InstallerAllowsOnboarding => _installer is null ||
        (_installerCommitted && !_installerRestart && _installer.RequiresOnboarding && !_installerBusy);

    internal WorkIqSignInWindow(
        IUnifiedSetupHost installer, IWorkIqSetupService? setup = null,
        ISetupReadinessService? readiness = null,
        Func<string, bool, CancellationToken, Task>? installVoice = null,
        Func<string, bool>? voiceInstalled = null)
        : this(installer.InitialState.PinnedAccount, setup ?? new WorkIqSetupService(),
            readiness, previewMode: true, installer.InitialState.PinnedMailbox,
            installer.InitialState.PinnedUserPrincipalName, installer.InitialState.PinnedPrincipalId,
            installer.InitialState.Settings.OutlookScope != OutlookMonitoringScope.Off,
            installVoice, voiceInstalled)
    {
        _installer = installer;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        InstallerGroup.Visibility = InstallerRequirements.Visibility = Visibility.Visible;
        FirstRunRequirements.Visibility = Visibility.Collapsed;
        InstallerApplyButton.Content = installer.ActionLabel;
        CopilotAccessGroup.Header = "3. GitHub Copilot access";
        MicrosoftConnectionGroup.Header = "4. Microsoft 365 connection";
        VoiceSetupGroup.Header = "5. Optional English voice";
        installer.Progress += OnInstallerProgress;
        Loaded += async (_, _) => await PrepareInstallerAsync();
        Closing += InstallerClosing;
        Closed += (_, _) =>
        {
            installer.Progress -= OnInstallerProgress;
            if (!_installerFinished) installer.Close(launch: false);
        };
        UpdateControls();
    }

    private void OnInstallerProgress(SetupInstallProgress progress)
    {
        void Deliver()
        {
            if (_closed) return;
            InstallerStatus.Text = progress.Message;
            InstallerProgress.Value = Math.Clamp(progress.Percentage, 0, 100);
        }
        if (Dispatcher.CheckAccess()) Deliver();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Deliver));
    }

    internal Task PrepareInstallerAsync() => RunInstallerOperationAsync(async ct =>
    {
        _requirementsReady = false;
        var requirements = await _installer!.PrepareAsync(ct);
        InstallerApplyButton.Content = _installer.ActionLabel;
        InstallerGroup.Header = $"2. {_installer.ActionLabel}";
        ShowInstallerRequirements(requirements);
        _requirementsReady = requirements.All(requirement => requirement.Ready);
        InstallerStatus.Text = _requirementsReady
            ? $"Ready — Choose {_installer.ActionLabel}. Account setup starts only after installation completes."
            : "Action required — Install or repair the missing requirements above, then continue. Vendor prompts may open.";
    }, "Checking requirements. Peggy will remain running until you choose Install / update.");

    private void ShowInstallerRequirements(IReadOnlyList<SetupRequirement> requirements)
    {
        InstallerRequirements.Children.Clear();
        foreach (var requirement in requirements)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock { Text = requirement.Name, FontWeight = FontWeights.SemiBold });
            var status = new DockPanel();
            status.Children.Add(new SetupStepIndicator
            {
                State = requirement.Ready ? SetupStepState.Succeeded : SetupStepState.Failed
            });
            status.Children.Add(new TextBlock
            {
                Text = (requirement.Ready ? "Ready — " : "Action required — ") + requirement.Detail,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(status);
            if (!requirement.Ready)
            {
                var install = new Button
                {
                    Content = $"Install / repair {requirement.Name}",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                install.Click += async (_, _) => await InstallRequirementAsync(requirement.Id);
                panel.Children.Add(install);
            }
            InstallerRequirements.Children.Add(panel);
        }
    }

    internal async Task InstallRequirementAsync(string id)
    {
        await RunInstallerOperationAsync(ct => _installer!.InstallRequirementAsync(id, ct),
            "Waiting for the vendor installer. Complete its prompts; cancel there if needed. Peggy will wait safely.");
        if (!_closed) await PrepareInstallerAsync();
    }

    internal async Task ApplyInstallerAsync()
    {
        if (!_requirementsReady || _installerCommitted) return;
        await RunInstallerOperationAsync(async ct =>
        {
            var result = await _installer!.ApplyAsync(new WindowInteropHelper(this).Handle, ct);
            _installerCommitted = result.Succeeded;
            _installerRestart = result.RestartRequired;
            if (result.Succeeded && _installer.RequiresOnboarding) RefreshInstallerState();
            InstallerStatus.Text = result.Message;
            InstallerProgress.Value = result.Succeeded ? 100 : 0;
            OperationStatus.Text = result.Message;
        }, "Preparing installation. If Peggy is running, setup will ask it to close safely before updating files.");
        if (!InstallerAllowsOnboarding) return;
        await CheckLocalAsync();
        await RunOperationAsync(async ct =>
        {
            var identity = await _installer!.VerifySavedIdentityAsync(ct);
            if (identity is null)
            {
                OperationStatus.Text = "Peggy is installed. Check GitHub Copilot access and connect Microsoft 365 below, then choose Finish.";
                return;
            }
            if (_local?.WorkIq.IsReady != true ||
                !string.Equals(_local.WorkIq.Version, identity.EulaAcceptedVersion, StringComparison.Ordinal))
            {
                OperationStatus.Text = "Peggy is installed. Retry local checks and review the current WorkIQ EULA before connecting your account.";
                return;
            }
            _candidateIdentity = identity;
            ConnectedName.Text = string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Account : identity.DisplayName;
            ConnectedEmail.Text = identity.Mail ?? identity.Account;
            AccountEditor.Visibility = Visibility.Collapsed;
            ConnectedCard.Visibility = Visibility.Visible;
            SetStep(MicrosoftSignInIndicator, MicrosoftSignInStatus, SetupStepState.Succeeded, "Ready — Existing sign-in verified");
            SetStep(MicrosoftConsentIndicator, MicrosoftConsentStatus, SetupStepState.Succeeded, "Ready — Existing permission consent");
            SetStep(AccountVerifiedIndicator, AccountVerifiedStatus, SetupStepState.Succeeded, "Ready — Saved account verified");
            if (_requireMailAccess) await ProbeMailAsync(ct);
            OperationStatus.Text = "Peggy was updated. Your account and settings are kept. Choose Finish to launch.";
        }, "Checking the saved Microsoft 365 connection without signing in again.",
            "Saved account verification cancelled. You can reconnect above.");
    }

    private void RefreshInstallerState()
    {
        var state = _installer!.InitialState;
        _expectedAccount = state.PinnedAccount ?? "";
        _expectedMailbox = state.PinnedMailbox ?? "";
        _expectedUserPrincipalName = state.PinnedUserPrincipalName ?? "";
        _expectedPrincipalId = state.PinnedPrincipalId ?? "";
        _firstRun = _expectedAccount.Length == 0 && _expectedMailbox.Length == 0;
        _requireMailAccess = _firstRun || state.Settings.OutlookScope != OutlookMonitoringScope.Off;
        AccountText.Text = _expectedAccount.Length > 0 ? _expectedAccount : _expectedMailbox;
        CopilotAccessGroup.Visibility = VoiceSetupGroup.Visibility = _firstRun ? Visibility.Visible : Visibility.Collapsed;
        _candidateIdentity = null;
        _mailReady = false;
        ConfirmInstallerAccountSwitch.IsChecked = false;
        if (!_requireMailAccess)
            SetStep(MailAccessIndicator, MailAccessStatus, SetupStepState.Skipped,
                "Optional / skipped — Mail access: Outlook monitoring is Off.");
        UpdateMonitoringSummary();
    }

    private async Task RunInstallerOperationAsync(Func<CancellationToken, Task> operation, string status,
        [System.Runtime.CompilerServices.CallerMemberName] string stage = "")
    {
        if (_installer is null || _installerBusy || _busy || _closed || _installerCommitted) return;
        _installerBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _installerCancellation = cancellation;
        InstallerStatus.Text = OperationStatus.Text = status;
        UpdateControls();
        try
        {
            await _installer.Diagnostics.RunAsync($"ui.{stage}", () => operation(cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            InstallerStatus.Text = OperationStatus.Text = "Cancelled — No account was saved. Retry when ready.";
        }
        catch (Exception ex) when (SetupReadinessService.IsExpectedFailure(ex))
        {
            _installer.ReportFailure(ex);
            InstallerStatus.Text = OperationStatus.Text = $"Action required — {ex.Message}";
            ShowDetails(ex.Message);
        }
        finally
        {
            _installerCancellation = null;
            _installerBusy = false;
            UpdateControls();
        }
    }

    private void UpdateInstallerControls()
    {
        if (_installer is null) return;
        InstallerAccountSwitchConfirmation.Visibility =
            _candidateIdentity is not null && IsDifferentAccount(_candidateIdentity)
                ? Visibility.Visible : Visibility.Collapsed;
        WorkIqLocalControls.IsEnabled = CopilotAccessGroup.IsEnabled =
            MicrosoftConnectionGroup.IsEnabled = VoiceSetupGroup.IsEnabled = InstallerAllowsOnboarding;
        WorkIqLocalControls.Visibility = _installerCommitted ? Visibility.Visible : Visibility.Collapsed;
        InstallerRequirements.IsEnabled = !_installerBusy && !_installerCommitted;
        InstallerRetryButton.IsEnabled = !_installerBusy && !_installerCommitted;
        InstallerApplyButton.IsEnabled = !_installerBusy && _requirementsReady && !_installerCommitted;
        if (_installerCommitted && (_installerRestart || !_installer.RequiresOnboarding))
        {
            FinishButton.IsEnabled = !_installerBusy;
            FinishButton.Content = "Finish";
        }
        else FinishButton.Content = "Finish and launch";
        if (_installerBusy) CancelButton.Content = "Cancel operation";
        else if (_installerCommitted) CancelButton.Content = "Close setup";
    }

    private bool FinishInstallerWithoutOnboarding()
    {
        if (_installer is null || !_installerCommitted ||
            (!_installerRestart && _installer.RequiresOnboarding)) return false;
        _installerFinished = true;
        _installer.Close(launch: false);
        Close();
        return true;
    }

    private bool HandleInstallerCancel()
    {
        if (_installerBusy)
        {
            _installerCancellation?.Cancel();
            _installer!.Cancel();
            InstallerStatus.Text = OperationStatus.Text =
                "Cancellation requested. Waiting for the installer to finish safely; cancel any vendor prompts there.";
        }
        else if (_busy) CancelActiveOperation();
        else Close();
        return true;
    }

    private void InstallerClosing(object? sender, CancelEventArgs e)
    {
        if (_installerBusy)
        {
            e.Cancel = true;
            HandleInstallerCancel();
        }
        else if (_busy && !_installerFinished)
        {
            e.Cancel = true;
            CancelActiveOperation();
            OperationStatus.Text = _installerCommitted
                ? "Cancelling account setup. Peggy remains installed; close after cancellation completes and finish later."
                : "Waiting for cancellation to complete.";
        }
    }

    private async void InstallerRetry_Click(object sender, RoutedEventArgs e) => await PrepareInstallerAsync();
    private async void InstallerApply_Click(object sender, RoutedEventArgs e) => await ApplyInstallerAsync();
}
