using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Peggy.Core;

namespace Peggy.Tray;

public partial class WorkIqSignInWindow : Window
{
    private readonly WorkIqSetupService _setup;
    private readonly string _expectedAccount;
    private readonly string _expectedMailbox;
    private readonly string _expectedUserPrincipalName;
    private readonly string _expectedPrincipalId;
    private readonly bool _accountLocked;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _busy;

    public WorkIqSignInWindow(
        string? expectedAccount = null,
        WorkIqSetupService? setup = null,
        string? expectedMailbox = null,
        string? expectedUserPrincipalName = null,
        string? expectedPrincipalId = null)
        : this(
            expectedAccount,
            expectedMailbox,
            expectedUserPrincipalName,
            expectedPrincipalId,
            setup,
            previewMode: false)
    {
    }

    internal WorkIqSignInWindow(
        string? expectedAccount,
        WorkIqSetupService? setup,
        bool previewMode)
        : this(
            expectedAccount,
            expectedMailbox: null,
            expectedUserPrincipalName: null,
            expectedPrincipalId: null,
            setup,
            previewMode)
    {
    }

    private WorkIqSignInWindow(
        string? expectedAccount,
        string? expectedMailbox,
        string? expectedUserPrincipalName,
        string? expectedPrincipalId,
        WorkIqSetupService? setup,
        bool previewMode)
    {
        InitializeComponent();
        _setup = setup ?? new WorkIqSetupService();
        _expectedAccount = expectedAccount?.Trim() ?? string.Empty;
        _expectedMailbox = expectedMailbox?.Trim() ?? string.Empty;
        _expectedUserPrincipalName =
            expectedUserPrincipalName?.Trim() ?? string.Empty;
        _expectedPrincipalId = expectedPrincipalId?.Trim() ?? string.Empty;
        var configuredAccount = PeggyDefaults.ConfiguredWorkIqAccount;
        _accountLocked = !string.IsNullOrWhiteSpace(configuredAccount);
        AccountText.Text = _accountLocked
            ? configuredAccount
            : _expectedAccount;
        AccountText.IsReadOnly = _accountLocked;
        if (_accountLocked)
        {
            AccountText.ToolTip =
                "Controlled by the PEGGY_WORKIQ_ACCOUNT environment variable.";
        }
        UpdateAccountMode();

        Loaded += (_, _) =>
            PeggyWindowLayout.ConstrainToWorkArea(this);
        if (!previewMode)
        {
            Loaded += async (_, _) =>
                await RefreshInstallationStatusAsync();
        }
        Closed += (_, _) =>
        {
            _lifetimeCancellation.Cancel();
            _activeOperationCancellation?.Cancel();
            _lifetimeCancellation.Dispose();
        };
    }

    internal void SetPreviewInstallationState(
        string installationStatus,
        string operationStatus)
    {
        InstallationStatus.Text = installationStatus;
        OperationStatus.Text = operationStatus;
        InstallButton.Visibility = Visibility.Collapsed;
        SignInButton.IsEnabled = true;
    }

    public string? AuthenticatedAccount { get; private set; }

    public string? AuthenticatedMailbox { get; private set; }

    public string? AcceptedEulaVersion { get; private set; }

    public WorkIqAccountIdentity? AuthenticatedIdentity { get; private set; }

    public bool AuthenticationAttempted { get; private set; }

    private async Task RefreshInstallationStatusAsync()
    {
        if (!_setup.TryGetExecutablePath(out var executable))
        {
            InstallationStatus.Text =
                "Standalone WorkIQ is not installed. Install Node.js 18 or " +
                "later, then select Install WorkIQ.";
            InstallButton.Visibility = Visibility.Visible;
            SignInButton.IsEnabled = false;
            return;
        }

        InstallButton.Visibility = Visibility.Collapsed;
        SignInButton.IsEnabled = true;
        try
        {
            var version = await _setup.GetVersionAsync(
                _lifetimeCancellation.Token);
            InstallationStatus.Text =
                $"WorkIQ {version} is ready at {executable}.";
        }
        catch (OperationCanceledException) when (
            _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (WorkIqSetupException ex)
        {
            InstallationStatus.Text =
                $"WorkIQ was found at {executable}, but its version could not " +
                $"be read: {ex.Message}";
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Installing standalone WorkIQ with npm...");
        using var operation = BeginOperation();
        try
        {
            await _setup.InstallAsync(operation.Token);
            OperationStatus.Text = "WorkIQ installation completed.";
            await RefreshInstallationStatusAsync();
        }
        catch (OperationCanceledException) when (
            operation.IsCancellationRequested)
        {
            OperationStatus.Text = "WorkIQ installation was cancelled.";
        }
        catch (WorkIqSetupException ex)
        {
            ShowSetupError(ex);
        }
        finally
        {
            EndOperation(operation);
            SetBusy(false);
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (EulaCheck.IsChecked != true)
        {
            MessageBox.Show(
                this,
                "Review and accept the WorkIQ EULA before signing in.",
                "Connect Microsoft 365",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        SetBusy(
            true,
            "Opening Microsoft sign-in and permission consent. Complete the " +
            "steps in your browser.");
        AuthenticatedAccount = null;
        AuthenticatedMailbox = null;
        AcceptedEulaVersion = null;
        AuthenticatedIdentity = null;
        using var operation = BeginOperation();
        try
        {
            AuthenticationAttempted = true;
            var identity = await _setup.SignInAsync(
                AccountText.Text,
                operation.Token);
            if (_expectedAccount.Length > 0 &&
                !WorkIqSetupService.RepresentsSameAccount(
                    _expectedAccount,
                    _expectedMailbox,
                    _expectedUserPrincipalName,
                    _expectedPrincipalId,
                    identity) &&
                MessageBox.Show(
                    this,
                    $"Switch Peggy from {_expectedAccount} to " +
                    $"{identity.Account}?\n\nPeggy will clear the current " +
                    "account's local alerts, monitoring history, selected " +
                    "Outlook folders and Teams, Copilot conversation context, " +
                    "and action history. General app and speech settings will " +
                    "be kept.",
                    "Switch Microsoft 365 account",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                OperationStatus.Text =
                    $"Account switch cancelled. Peggy remains connected as " +
                    $"{_expectedAccount}.";
                return;
            }
            AuthenticatedAccount = identity.Account;
            AuthenticatedMailbox = identity.Mail;
            AcceptedEulaVersion = identity.EulaAcceptedVersion;
            AuthenticatedIdentity = identity;
            OperationStatus.Text =
                $"Verified Microsoft 365 account: {identity.Account}";
            DialogResult = true;
        }
        catch (OperationCanceledException) when (
            operation.IsCancellationRequested)
        {
            OperationStatus.Text = "Microsoft 365 sign-in was cancelled.";
        }
        catch (WorkIqSetupException ex)
        {
            if (ex is WorkIqEulaChangedException)
            {
                EulaCheck.IsChecked = false;
            }
            ShowSetupError(ex);
        }
        finally
        {
            EndOperation(operation);
            SetBusy(false);
        }
    }

    private void ReviewEula_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WorkIqSetupService.EulaUrl,
                UseShellExecute = true
            });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(
                this,
                $"The WorkIQ EULA could not be opened: {ex.Message}",
                "Connect Microsoft 365",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        AccountText.IsEnabled = !busy;
        EulaCheck.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        SignInButton.IsEnabled =
            !busy && _setup.TryGetExecutablePath(out _);
        CancelButton.Content = busy ? "Cancel operation" : "Cancel";
        if (status is not null)
        {
            OperationStatus.Text = status;
        }
    }

    private bool IsAccountSwitchRequested()
        => _expectedAccount.Length > 0 &&
           !_accountLocked &&
           !string.IsNullOrWhiteSpace(AccountText.Text) &&
           !string.Equals(
               _expectedAccount,
               AccountText.Text.Trim(),
               StringComparison.OrdinalIgnoreCase);

    private void AccountText_Changed(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
        => UpdateAccountMode();

    private void UpdateAccountMode()
    {
        if (_accountLocked)
        {
            SignInButton.Content = "Sign in again";
            AccountChangeNotice.Text =
                "This account is controlled by PEGGY_WORKIQ_ACCOUNT. Remove or " +
                "update that environment variable before switching accounts.";
            AccountChangeNotice.Visibility = Visibility.Visible;
            return;
        }

        if (_expectedAccount.Length == 0)
        {
            SignInButton.Content = "Sign in";
            AccountChangeNotice.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.IsNullOrWhiteSpace(AccountText.Text))
        {
            SignInButton.Content = "Switch account";
            AccountChangeNotice.Text =
                "Enter the Microsoft 365 email address Peggy should use.";
            AccountChangeNotice.Visibility = Visibility.Visible;
            return;
        }

        if (IsAccountSwitchRequested())
        {
            SignInButton.Content = "Switch account";
            AccountChangeNotice.Text =
                "Switching accounts clears local data tied to the current " +
                "account after the new identity is verified.";
        }
        else
        {
            SignInButton.Content = "Sign in again";
            AccountChangeNotice.Text =
                "Edit the address above to switch to another Microsoft 365 " +
                "account.";
        }
        AccountChangeNotice.Visibility = Visibility.Visible;
    }

    private void ShowSetupError(WorkIqSetupException exception)
    {
        OperationStatus.Text = exception.Message;
        MessageBox.Show(
            this,
            exception.Message,
            "Connect Microsoft 365",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private CancellationTokenSource BeginOperation()
    {
        var operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
        _activeOperationCancellation = operation;
        return operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeOperationCancellation, operation))
        {
            _activeOperationCancellation = null;
        }
    }
}
