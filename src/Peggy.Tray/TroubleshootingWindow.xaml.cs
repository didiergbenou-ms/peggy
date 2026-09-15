using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Peggy.Core;

namespace Peggy.Tray;

public partial class TroubleshootingWindow : Window
{
    private readonly MonitoringFailureDetails _failure;
    private readonly Func<
        IProgress<TroubleshootingCheckResult>,
        CancellationToken,
        Task<MonitoringTroubleshootingReport>> _runDiagnostics;
    private readonly Func<CancellationToken, Task<string>> _createBundle;
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<string, TroubleshootingCheckResult> _checks =
        new(StringComparer.Ordinal);
    private MonitoringTroubleshootingReport? _report;
    private bool _running;

    internal TroubleshootingWindow(
        MonitoringFailureDetails failure,
        MonitoringTroubleshooter troubleshooter,
        Func<CancellationToken, Task<string>> createBundle)
        : this(failure, troubleshooter.RunAsync, createBundle)
    {
    }

    internal TroubleshootingWindow(
        MonitoringFailureDetails failure,
        Func<
            IProgress<TroubleshootingCheckResult>,
            CancellationToken,
            Task<MonitoringTroubleshootingReport>> runDiagnostics,
        Func<CancellationToken, Task<string>> createBundle)
    {
        InitializeComponent();
        _failure = failure;
        _runDiagnostics = runDiagnostics;
        _createBundle = createBundle;
        FailureTimeText.Text = failure.OccurredUtc.ToLocalTime().ToString("G");
        FailureStageText.Text = EmptyAsUnknown(failure.Stage);
        FailureCodeText.Text = EmptyAsUnknown(failure.Code);
        FailureStatusText.Text = failure.StatusCode is null
            ? "Not reported"
            : failure.StatusCode.Value.ToString();
        FailureMessageText.Text = EmptyAsUnknown(failure.Message);
        foreach (var name in MonitoringTroubleshooter.CheckNames)
        {
            _checks[name] = new TroubleshootingCheckResult(
                name,
                TroubleshootingCheckStatus.Pending,
                "Not started.");
        }
        RenderChecks();
        Loaded += async (_, _) =>
        {
            PeggyWindowLayout.ConstrainToWorkArea(this);
            await RunDiagnosticsAsync();
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
    }

    public event Action? SignInRequested;

    public event Action? OpenSettingsRequested;

    public event Action? RetryMonitoringRequested;

    private async void RunAgain_Click(object sender, RoutedEventArgs e)
        => await RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _report = null;
        RunAgainButton.IsEnabled = false;
        CopyDetailsButton.IsEnabled = false;
        SignInButton.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Collapsed;
        RetryMonitoringButton.Visibility = Visibility.Collapsed;
        RunStatusText.Text = "Running read-only checks...";
        foreach (var name in MonitoringTroubleshooter.CheckNames)
        {
            _checks[name] = new TroubleshootingCheckResult(
                name,
                TroubleshootingCheckStatus.Pending,
                "Waiting...");
        }
        RenderChecks();

        var progress = new Progress<TroubleshootingCheckResult>(result =>
        {
            _checks[result.Name] = result;
            RenderChecks();
        });
        try
        {
            _report = await _runDiagnostics(progress, _closing.Token);
            foreach (var result in _report.Checks)
            {
                _checks[result.Name] = result;
            }
            RenderChecks();
            RecommendationText.Text = _report.Recommendation.Summary;
            SignInButton.Visibility = _report.Recommendation.ShowSignIn
                ? Visibility.Visible
                : Visibility.Collapsed;
            SettingsButton.Visibility =
                _report.Recommendation.ShowSettings
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            RetryMonitoringButton.Visibility =
                _report.Recommendation.ShowRetry
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            CopyDetailsButton.IsEnabled = true;
            RunStatusText.Text =
                $"Checks completed at {DateTime.Now:T}.";
        }
        catch (OperationCanceledException) when (
            _closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RunStatusText.Text =
                $"Troubleshooting could not complete: {ex.Message}";
        }
        finally
        {
            _running = false;
            if (!_closing.IsCancellationRequested)
            {
                RunAgainButton.IsEnabled = true;
            }
        }
    }

    private void RenderChecks()
    {
        ChecksPanel.Children.Clear();
        foreach (var name in MonitoringTroubleshooter.CheckNames)
        {
            var result = _checks[name];
            ChecksPanel.Children.Add(CreateCheckRow(result));
        }
    }

    private static Border CreateCheckRow(TroubleshootingCheckResult result)
    {
        var color = result.Status switch
        {
            TroubleshootingCheckStatus.Passed => Color.FromRgb(39, 94, 58),
            TroubleshootingCheckStatus.Failed => Color.FromRgb(139, 26, 18),
            TroubleshootingCheckStatus.Running => Color.FromRgb(124, 82, 0),
            _ => Color.FromRgb(117, 105, 97)
        };
        var label = result.Status switch
        {
            TroubleshootingCheckStatus.NotReached => "Not reached",
            _ => result.Status.ToString()
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{label}: {result.Name}",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color)
        });
        panel.Children.Add(new TextBlock
        {
            Text = result.Detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 105, 97)),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0)
        });
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 247, 244)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 219, 211)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel
        };
        AutomationProperties.SetName(
            row,
            $"{result.Name}: {label}. {result.Detail}");
        return row;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }
        Clipboard.SetText(_report.ToTechnicalText());
        RunStatusText.Text = "Technical details copied.";
    }

    private async void CreateZip_Click(object sender, RoutedEventArgs e)
    {
        CreateZipButton.IsEnabled = false;
        RunStatusText.Text = "Creating diagnostic ZIP...";
        try
        {
            var path = await _createBundle(_closing.Token);
            RunStatusText.Text = $"Diagnostic ZIP created: {path}";
        }
        catch (OperationCanceledException) when (
            _closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Peggy diagnostics",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RunStatusText.Text = "Diagnostic ZIP could not be created.";
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
            {
                CreateZipButton.IsEnabled = true;
            }
        }
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
        => SignInRequested?.Invoke();

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenSettingsRequested?.Invoke();

    private void RetryMonitoring_Click(object sender, RoutedEventArgs e)
        => RetryMonitoringRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private static string EmptyAsUnknown(string value)
        => string.IsNullOrWhiteSpace(value) ? "Not reported" : value;
}
