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
        VersionText.Text = PeggyReleaseVersion.Display;
        Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
    }

    public event Action<string>? ResolveRequested;

    public event Action<string>? DismissRequested;

    public event Action? DismissAllRequested;

    public event Action<string>? SnoozeRequested;

    public event Action<string, FeedbackKind>? FeedbackRequested;

    public event Action<string, AlertTier>? TierChanged;

    public event Action<string>? ChatRequested;

    public event Action<string>? ContextRequested;

    public event Action? RevealRequested;

    public event Action? RetryRequested;

    public event Action? TroubleshootRequested;

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
        StatusText.Text = active.Count == 0
            ? "No active alerts"
            : $"{persistentCount} active, {awarenessCount} awareness";
        ListCaptionText.Text = active.Count == 0
            ? $"{_assistantName} is still watching"
            : $"{active.Count} " +
              $"{(active.Count == 1 ? "item" : "items")}, " +
              "highest priority first";
        DismissAllButton.IsEnabled = active.Count > 0;
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
            CardsPanel.Children.Add(CreateEmptyState());
            return;
        }

        foreach (var alert in active)
        {
            CardsPanel.Children.Add(CreateCard(alert, maskContent));
        }
    }

    public void ShowNearTaskbar()
    {
        var workArea = SystemParameters.WorkArea;
        var availableHeight = Math.Max(320, workArea.Height - 16);
        var availableWidth = Math.Max(360, workArea.Width - 16);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxHeight = availableHeight;
        MinWidth = Math.Min(MinWidth, availableWidth);
        MaxWidth = availableWidth;
        if (Height > availableHeight)
        {
            Height = availableHeight;
        }
        if (Width > availableWidth)
        {
            Width = availableWidth;
        }
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Max(
            workArea.Left + 8,
            workArea.Right - width - 16);
        Top = Math.Max(
            workArea.Top + 8,
            workArea.Bottom - height - 16);
        Activate();
    }

    public bool FocusAlert(string alertId)
    {
        var cards = CardsPanel.Children.OfType<Border>()
            .Where(card => card.Tag is string).ToList();
        var target = cards.FirstOrDefault(card =>
            string.Equals(card.Tag as string, alertId, StringComparison.Ordinal));
        if (target is null) return false;
        foreach (var card in cards)
        {
            var selected = ReferenceEquals(card, target);
            card.BorderBrush = GetResourceBrush(
                selected ? "BrandBrush" : "BorderBrush", Brushes.Gray);
            card.BorderThickness = new Thickness(selected ? 2 : 1);
        }
        UpdateLayout();
        target.Focusable = true;
        target.BringIntoView();
        target.Focus();
        return true;
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

        HealthTitle.Text = interrupted
            ? "Monitoring interrupted"
            : "Monitoring notice";
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
        var tierBrush = GetTierBrush(alert.Tier);
        var border = new Border
        {
            Background = GetResourceBrush("SurfaceBrush", Brushes.White),
            BorderBrush = GetResourceBrush(
                "BorderBrush",
                new SolidColorBrush(Color.FromRgb(229, 219, 211))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 14),
            SnapsToDevicePixels = true,
            Tag = alert.Id
        };
        border.SetResourceReference(
            UIElement.EffectProperty,
            "CardShadow");

        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(5) });
        cardGrid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        cardGrid.Children.Add(new Border
        {
            Background = tierBrush,
            CornerRadius = new CornerRadius(14, 0, 0, 14)
        });
        var panel = new StackPanel
        {
            Margin = new Thickness(17, 16, 17, 15)
        };
        Grid.SetColumn(panel, 1);
        cardGrid.Children.Add(panel);
        border.Child = cardGrid;

        var header = new Grid();
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        panel.Children.Add(header);

        header.Children.Add(CreateBadge(
            alert.Tier.ToString(),
            tierBrush,
            GetTierSurfaceBrush(alert.Tier)));
        var sourceBadge = CreateBadge(
            GetSourceName(alert.Source),
            GetSourceBrush(alert.Source),
            GetSourceSurfaceBrush(alert.Source));
        sourceBadge.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(sourceBadge, 1);
        header.Children.Add(sourceBadge);

        var time = new TextBlock
        {
            Text = FormatActivityTime(alert.LastActivityUtc),
            Foreground = GetResourceBrush(
                "MutedTextBrush",
                Brushes.DimGray),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 3);
        header.Children.Add(time);

        panel.Children.Add(new TextBlock
        {
            Text = maskContent
                ? "Work item details are hidden while your screen may be visible."
                : alert.Summary,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 22,
            Foreground = GetResourceBrush(
                "TextBrush",
                new SolidColorBrush(Color.FromRgb(45, 42, 39))),
            Margin = new Thickness(0, 12, 0, 0)
        });

        if (!maskContent && !string.IsNullOrWhiteSpace(alert.Reason))
        {
            var explanation = new StackPanel();
            explanation.Children.Add(new TextBlock
            {
                Text = $"Why {_assistantName} flagged this",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = tierBrush
            });
            explanation.Children.Add(new TextBlock
            {
                Text = alert.Reason,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 18,
                Foreground = GetResourceBrush(
                    "MutedTextBrush",
                    Brushes.DimGray),
                Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(
                    Color.FromRgb(250, 247, 244)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 9, 11, 10),
                Margin = new Thickness(0, 12, 0, 0),
                Child = explanation
            });
        }

        var tierRow = new DockPanel
        {
            Margin = new Thickness(0, 13, 0, 0),
            LastChildFill = false
        };
        var tierLabel = new TextBlock
        {
            Text = "Priority",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetResourceBrush(
                "MutedTextBrush",
                Brushes.DimGray),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 9, 0)
        };
        DockPanel.SetDock(tierLabel, Dock.Left);
        tierRow.Children.Add(tierLabel);
        var tier = new ComboBox
        {
            Width = 126,
            ItemsSource = Enum.GetValues<AlertTier>(),
            SelectedItem = alert.Tier,
            Tag = alert.Id
        };
        tier.SetResourceReference(
            FrameworkElement.StyleProperty,
            "PriorityComboStyle");
        tier.SelectionChanged += Tier_SelectionChanged;
        tierRow.Children.Add(tier);
        panel.Children.Add(tierRow);

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = GetResourceBrush(
                "BorderBrush",
                new SolidColorBrush(Color.FromRgb(229, 219, 211))),
            Margin = new Thickness(0, 14, 0, 13)
        });

        var actions = new WrapPanel();
        panel.Children.Add(actions);
        if (maskContent)
        {
            actions.Children.Add(CreateButton(
                "Reveal details",
                alert.Id,
                (_, _) => RevealRequested?.Invoke(),
                "PrimaryActionButtonStyle"));
        }
        var sourceName = GetSourceName(alert.Source);
        if (AlertSourceLink.TryCreate(alert.SourceReference, out _))
        {
            var contextButton = CreateButton(
                $"Open in {sourceName}",
                alert.Id,
                (_, id) => ContextRequested?.Invoke(id),
                "PrimaryActionButtonStyle");
            actions.Children.Add(contextButton);
        }
        actions.Children.Add(CreateButton(
            $"Talk to {_assistantName}",
            alert.Id,
            (_, id) => ChatRequested?.Invoke(id),
            "SecondaryActionButtonStyle"));
        actions.Children.Add(CreateButton(
            "Resolve",
            alert.Id,
            (_, id) => ResolveRequested?.Invoke(id),
            "PositiveActionButtonStyle"));

        var secondaryActions = new WrapPanel
        {
            Margin = new Thickness(0, 4, 0, 0)
        };
        panel.Children.Add(secondaryActions);
        secondaryActions.Children.Add(CreateButton(
            "Snooze 1 hour",
            alert.Id,
            (_, id) => SnoozeRequested?.Invoke(id),
            "SubtleActionButtonStyle"));
        secondaryActions.Children.Add(CreateButton(
            "Dismiss",
            alert.Id,
            (_, id) => DismissRequested?.Invoke(id),
            "SubtleActionButtonStyle"));

        var feedbackActions = new WrapPanel
        {
            Margin = new Thickness(0, 7, 0, 0)
        };
        feedbackActions.Children.Add(new TextBlock
        {
            Text = $"Tune {_assistantName}:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetResourceBrush(
                "MutedTextBrush",
                Brushes.DimGray),
            FontSize = 11,
            Margin = new Thickness(0, 0, 3, 7)
        });
        feedbackActions.Children.Add(CreateButton(
            "Less like this",
            alert.Id,
            (_, id) => FeedbackRequested?.Invoke(
                id,
                FeedbackKind.LessLikeThis),
            "TextActionButtonStyle"));
        feedbackActions.Children.Add(CreateButton(
            "Always alert",
            alert.Id,
            (_, id) => FeedbackRequested?.Invoke(
                id,
                FeedbackKind.AlwaysAlert),
            "TextActionButtonStyle"));
        panel.Children.Add(feedbackActions);
        return border;
    }

    private Button CreateButton(
        string text,
        string alertId,
        Action<Button, string> action,
        string styleKey)
    {
        var button = new Button
        {
            Content = text,
            Tag = alertId,
            Margin = new Thickness(0, 0, 8, 7)
        };
        button.SetResourceReference(
            FrameworkElement.StyleProperty,
            styleKey);
        button.Click += (_, _) => action(button, alertId);
        return button;
    }

    private Border CreateEmptyState()
    {
        var iconGlyph = new DecorativeTextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = GetResourceBrush(
                "BrandBrush",
                new SolidColorBrush(Color.FromRgb(214, 95, 61))),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(27),
            Background = new SolidColorBrush(
                Color.FromRgb(255, 240, 233)),
            Child = iconGlyph
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = "You're all caught up",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetResourceBrush(
                "TextBrush",
                new SolidColorBrush(Color.FromRgb(45, 41, 38))),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 13, 0, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{_assistantName} will keep watching for what matters.",
            FontSize = 12,
            Foreground = GetResourceBrush(
                "MutedTextBrush",
                Brushes.DimGray),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        });

        var border = new Border
        {
            Height = 230,
            Background = GetResourceBrush("SurfaceBrush", Brushes.White),
            BorderBrush = GetResourceBrush(
                "BorderBrush",
                new SolidColorBrush(Color.FromRgb(229, 219, 211))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = content
        };
        border.SetResourceReference(
            UIElement.EffectProperty,
            "CardShadow");
        return border;
    }

    private static Border CreateBadge(
        string text,
        Brush foreground,
        Brush background)
        => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 4, 8, 4),
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };

    private Brush GetResourceBrush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    private static string FormatActivityTime(
        DateTimeOffset activityUtc)
    {
        var local = activityUtc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today)
        {
            return $"Today, {local:t}";
        }
        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday, {local:t}";
        }
        return local.ToString("g");
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

    private void DismissAll_Click(object sender, RoutedEventArgs e)
        => DismissAllRequested?.Invoke();

    private void Retry_Click(object sender, RoutedEventArgs e)
        => RetryRequested?.Invoke();

    private void Troubleshoot_Click(object sender, RoutedEventArgs e)
        => TroubleshootRequested?.Invoke();

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
                new SolidColorBrush(Color.FromRgb(154, 85, 20)),
            AlertTier.Awareness =>
                new SolidColorBrush(Color.FromRgb(45, 104, 142)),
            _ => Brushes.Gray
        };

    private static Brush GetTierSurfaceBrush(AlertTier tier)
        => tier switch
        {
            AlertTier.Critical =>
                new SolidColorBrush(Color.FromRgb(253, 236, 234)),
            AlertTier.Attention =>
                new SolidColorBrush(Color.FromRgb(255, 241, 221)),
            AlertTier.Awareness =>
                new SolidColorBrush(Color.FromRgb(234, 242, 248)),
            _ => new SolidColorBrush(Color.FromRgb(242, 239, 236))
        };

    private static Brush GetSourceBrush(MessageSource source)
        => source == MessageSource.Email
            ? new SolidColorBrush(Color.FromRgb(55, 86, 139))
            : new SolidColorBrush(Color.FromRgb(90, 78, 170));

    private static Brush GetSourceSurfaceBrush(MessageSource source)
        => source == MessageSource.Email
            ? new SolidColorBrush(Color.FromRgb(235, 241, 250))
            : new SolidColorBrush(Color.FromRgb(240, 237, 252));

    private static string GetSourceName(MessageSource source)
        => source == MessageSource.Email
            ? "Outlook"
            : "Teams";
}
