using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Peggy.Core;

namespace Peggy.Tray;

public partial class AlertCenterWindow : Window
{
    private string? _criticalAlertId;
    private string _assistantName = "Peggy";

    public AlertCenterWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
    }

    public event Action<string>? ResolveRequested;

    public event Action<string>? DismissRequested;

    public event Action<string>? SnoozeRequested;

    public event Action<string, FeedbackKind>? FeedbackRequested;

    public event Action<string, AlertTier>? TierChanged;

    public event Action<string>? ChatRequested;

    public event Action<string>? ContextRequested;

    public event Action? RevealRequested;

    public event Action? RetryRequested;

    public event Action<string>? CriticalSwitchRequested;

    public void UpdateAlerts(
        IReadOnlyList<AlertRecord> alerts,
        bool maskContent)
    {
        CardsPanel.Children.Clear();
        var active = alerts
            .Where(alert => alert.Status == AlertStatus.Active)
            .OrderBy(alert => alert.Tier)
            .ThenByDescending(alert => alert.LastActivityUtc)
            .ToList();
        var persistentCount = active.Count(alert =>
            alert.Tier is AlertTier.Critical or AlertTier.Attention);
        var awarenessCount = active.Count(alert =>
            alert.Tier == AlertTier.Awareness);
        StatusText.Text =
            $"{persistentCount} active, {awarenessCount} awareness";
        if (_criticalAlertId is not null &&
            !active.Any(alert =>
                alert.Tier == AlertTier.Critical &&
                string.Equals(
                    alert.Id,
                    _criticalAlertId,
                    StringComparison.Ordinal)))
        {
            ClearCriticalBanner();
        }

        if (active.Count == 0)
        {
            CardsPanel.Children.Add(new TextBlock
            {
                Text = "Nothing needs your attention.",
                Foreground = Brushes.DimGray,
                FontSize = 15,
                Margin = new Thickness(4, 18, 4, 4)
            });
            return;
        }

        foreach (var alert in active)
        {
            CardsPanel.Children.Add(CreateCard(alert, maskContent));
        }
    }

    public void ShowNearTaskbar()
    {
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Max(
            SystemParameters.WorkArea.Left + 8,
            SystemParameters.WorkArea.Right - width - 16);
        Top = Math.Max(
            SystemParameters.WorkArea.Top + 8,
            SystemParameters.WorkArea.Bottom - height - 16);
        Activate();
    }

    public void SetMonitoringInterrupted(
        bool interrupted,
        int failureCount,
        DateTimeOffset? lastSuccessUtc,
        string? remediation = null)
    {
        HealthBanner.Visibility =
            interrupted || !string.IsNullOrWhiteSpace(remediation)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!interrupted &&
            string.IsNullOrWhiteSpace(remediation))
        {
            return;
        }

        var lastSuccess = lastSuccessUtc is null
            ? "No successful check has completed yet."
            : $"Last successful check: {lastSuccessUtc.Value.ToLocalTime():g}.";
        HealthDetail.Text = !string.IsNullOrWhiteSpace(remediation)
            ? $"{remediation} {lastSuccess}"
            : $"{failureCount} consecutive monitoring attempts failed. " +
              $"{lastSuccess} {_assistantName} will keep retrying.";
    }

    public void ShowCriticalBanner(AlertRecord alert, bool maskContent)
    {
        _criticalAlertId = alert.Id;
        CriticalText.Text = maskContent
            ? "A critical work item needs your attention."
            : alert.Summary;
        CriticalBanner.Visibility = Visibility.Visible;
    }

    public void ClearCriticalBanner()
    {
        _criticalAlertId = null;
        CriticalBanner.Visibility = Visibility.Collapsed;
    }

    private Border CreateCard(AlertRecord alert, bool maskContent)
    {
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = GetTierBrush(alert.Tier),
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var panel = new StackPanel();
        border.Child = panel;

        var header = new DockPanel();
        panel.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = $"{alert.Tier} | {GetSourceName(alert.Source)}",
            Foreground = GetTierBrush(alert.Tier),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        var time = new TextBlock
        {
            Text = alert.LastActivityUtc.ToLocalTime().ToString("g"),
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);

        panel.Children.Add(new TextBlock
        {
            Text = maskContent
                ? "Work item details are hidden while your screen may be visible."
                : alert.Summary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 42, 39)),
            Margin = new Thickness(0, 8, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Why {_assistantName} flagged this",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetTierBrush(alert.Tier),
            Margin = new Thickness(0, 4, 0, 3),
            Visibility = maskContent ||
                         string.IsNullOrWhiteSpace(alert.Reason)
                ? Visibility.Collapsed
                : Visibility.Visible
        });
        panel.Children.Add(new TextBlock
        {
            Text = maskContent ? string.Empty : alert.Reason,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var tierRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        tierRow.Children.Add(new TextBlock
        {
            Text = "Correct tier:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var tier = new ComboBox
        {
            Width = 120,
            ItemsSource = Enum.GetValues<AlertTier>(),
            SelectedItem = alert.Tier,
            Tag = alert.Id
        };
        tier.SelectionChanged += Tier_SelectionChanged;
        tierRow.Children.Add(tier);
        panel.Children.Add(tierRow);

        var actions = new WrapPanel();
        panel.Children.Add(actions);
        if (maskContent)
        {
            actions.Children.Add(CreateButton(
                "Reveal details",
                alert.Id,
                (_, _) => RevealRequested?.Invoke()));
        }
        var sourceName = GetSourceName(alert.Source);
        if (AlertSourceLink.TryCreate(alert.SourceReference, out _))
        {
            var contextButton = CreateButton(
                $"Open in {sourceName}",
                alert.Id,
                (_, id) => ContextRequested?.Invoke(id));
            contextButton.Foreground = new SolidColorBrush(
                Color.FromRgb(45, 104, 142));
            contextButton.FontWeight = FontWeights.SemiBold;
            actions.Children.Add(contextButton);
        }
        actions.Children.Add(CreateButton(
            $"Talk to {_assistantName}",
            alert.Id,
            (_, id) => ChatRequested?.Invoke(id)));
        actions.Children.Add(CreateButton(
            "Resolve",
            alert.Id,
            (_, id) => ResolveRequested?.Invoke(id)));
        actions.Children.Add(CreateButton(
            "Snooze 1 hour",
            alert.Id,
            (_, id) => SnoozeRequested?.Invoke(id)));
        actions.Children.Add(CreateButton(
            "Dismiss",
            alert.Id,
            (_, id) => DismissRequested?.Invoke(id)));
        actions.Children.Add(CreateButton(
            "Less like this",
            alert.Id,
            (_, id) => FeedbackRequested?.Invoke(
                id,
                FeedbackKind.LessLikeThis)));
        actions.Children.Add(CreateButton(
            "Always alert",
            alert.Id,
            (_, id) => FeedbackRequested?.Invoke(
                id,
                FeedbackKind.AlwaysAlert)));
        return border;
    }

    private static Button CreateButton(
        string text,
        string alertId,
        Action<Button, string> action)
    {
        var button = new Button
        {
            Content = text,
            Tag = alertId,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 7, 7)
        };
        button.Click += (_, _) => action(button, alertId);
        return button;
    }

    private void Tier_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is ComboBox
            {
                Tag: string alertId,
                SelectedItem: AlertTier tier
            })
        {
            TierChanged?.Invoke(alertId, tier);
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
        => Hide();

    private void Retry_Click(object sender, RoutedEventArgs e)
        => RetryRequested?.Invoke();

    private void SwitchCritical_Click(object sender, RoutedEventArgs e)
    {
        if (_criticalAlertId is not null)
        {
            CriticalSwitchRequested?.Invoke(_criticalAlertId);
        }
        ClearCriticalBanner();
    }

    private static Brush GetTierBrush(AlertTier tier)
        => tier switch
        {
            AlertTier.Critical =>
                new SolidColorBrush(Color.FromRgb(180, 35, 24)),
            AlertTier.Attention =>
                new SolidColorBrush(Color.FromRgb(194, 107, 17)),
            AlertTier.Awareness =>
                new SolidColorBrush(Color.FromRgb(45, 104, 142)),
            _ => Brushes.Gray
        };

    private static string GetSourceName(MessageSource source)
        => source == MessageSource.Email
            ? "Outlook"
            : "Teams";
}
