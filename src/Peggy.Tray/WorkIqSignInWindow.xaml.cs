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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _busy;

    public WorkIqSignInWindow(
        string? expectedAccount = null,
        WorkIqSetupService? setup = null)
        : this(expectedAccount, setup, previewMode: false)
    {
    }

    internal WorkIqSignInWindow(
        string? expectedAccount,
        WorkIqSetupService? setup,
        bool previewMode)
    {
        InitializeComponent();
        _setup = setup ?? new WorkIqSetupService();
        _expectedAccount = expectedAccount?.Trim() ?? string.Empty;
        AccountText.Text = _expectedAccount;
        if (_expectedAccount.Length > 0)
        {
            AccountText.IsReadOnly = true;
            AccountText.ToolTip =
                "Peggy is pinned to this Microsoft 365 account.";
            SignInButton.Content = "Sign in again";
        }

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
            var identity = await _setup.SignInAsync(
                AccountText.Text,
                operation.Token);
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
