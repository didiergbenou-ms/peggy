using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Peggy.Core;

namespace Peggy.Tray;

public partial class AlertContextWindow : Window
{
    private readonly DispatcherTimer? _privacyTimer;
    private string _assistantName;

    public AlertContextWindow(
        AlertRecord alert,
        MessageThread? thread,
        bool canOpenOriginal,
        Func<bool>? shouldMask = null,
        string? resolvedContext = null,
        string assistantName = "Peggy")
    {
        InitializeComponent();
        Loaded += (_, _) =>
            PeggyWindowLayout.ConstrainToWorkArea(this);
        _assistantName = assistantName;
        Title = $"{assistantName} alert context";
        ReasonHeading.Text = $"Why {assistantName} alerted you";

        TitleText.Text = string.IsNullOrWhiteSpace(thread?.Subject)
            ? "Alert context"
            : thread.Subject;
        MetaText.Text =
            $"{GetSourceName(alert.Source)} | {alert.Tier} | " +
            $"{alert.LastActivityUtc.ToLocalTime():g}";
        SummaryText.Text = alert.Summary;
        ReasonText.Text = alert.Reason;
        ReasonText.Visibility = string.IsNullOrWhiteSpace(alert.Reason)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpenOriginalButton.Visibility = canOpenOriginal
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenOriginalButton.Content =
            $"Open in {GetSourceName(alert.Source)}";

        PopulateMessages(thread, resolvedContext);
        if (shouldMask is not null)
        {
            _privacyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _privacyTimer.Tick += (_, _) =>
            {
                if (!shouldMask())
                {
                    return;
                }

                _privacyTimer.Stop();
                Close();
            };
            Loaded += (_, _) => _privacyTimer.Start();
            Closed += (_, _) => _privacyTimer.Stop();
        }
    }

    public event Action? OpenOriginalRequested;

    private void PopulateMessages(
        MessageThread? thread,
        string? resolvedContext)
    {
        MessagesPanel.Children.Clear();
        var messages = thread?.Messages
            .OrderBy(message => message.ReceivedAt)
            .ToList();
        if (messages is not { Count: > 0 })
        {
            ContextStatus.Text = !string.IsNullOrWhiteSpace(resolvedContext)
                ? resolvedContext
                : $"{_assistantName} could not retrieve the original thread. The saved " +
                  "summary and flagging reason are shown above.";
            ContextStatus.Visibility = Visibility.Visible;
            ContextStatusCard.Visibility = Visibility.Visible;
            return;
        }

        ContextStatus.Visibility = Visibility.Collapsed;
        ContextStatusCard.Visibility = Visibility.Collapsed;
        foreach (var message in messages)
        {
            MessagesPanel.Children.Add(CreateMessageCard(message));
        }
    }

    private Border CreateMessageCard(Message message)
    {
        var card = new Border
        {
            Background = TryFindResource("PeggySurfaceBrush") as Brush
                         ?? Brushes.White,
            BorderBrush = TryFindResource("PeggyBorderBrush") as Brush
                          ?? new SolidColorBrush(
                              Color.FromRgb(229, 219, 211)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 13),
            Margin = new Thickness(0, 0, 0, 11)
        };
        card.SetResourceReference(
            UIElement.EffectProperty,
            "PeggyCardShadow");
        var panel = new StackPanel();
        card.Child = panel;

        var header = new DockPanel();
        var time = new TextBlock
        {
            Text = message.ReceivedAt.ToLocalTime().ToString("g"),
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);
        header.Children.Add(new TextBlock
        {
            Text = message.From,
            Foreground = new SolidColorBrush(
                Color.FromRgb(81, 59, 50)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0)
        });
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = message.Preview,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(
                Color.FromRgb(45, 42, 39)),
            FontSize = 13,
            LineHeight = 19,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return card;
    }

    private static string GetSourceName(MessageSource source)
        => source == MessageSource.Email
            ? "Outlook"
            : "Microsoft Teams";

    private void OpenOriginal_Click(object sender, RoutedEventArgs e)
        => OpenOriginalRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
