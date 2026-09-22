using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AlertNotificationSmokeTests
{
    public static void Run(App app)
    {
        TestBubbleLifecycle();
        var mascot = new MascotWindow();
        var bubble = new SpeechBubbleWindow();
        var center = new AlertCenterWindow();
        var conversationWindow = new ConversationWindow();
        var state = new PeggyStateManager(new EncryptedStateStore(
            Path.Combine(Path.GetTempPath(), $"peggy-bubble-{Guid.NewGuid():N}.dat")));
        var conversation = new ConversationController(
            conversationWindow,
            mascot,
            state,
            new AlertManager(state),
            () => throw new InvalidOperationException("No Copilot call expected."),
            () => new MockMessageProvider(),
            _ => Array.Empty<Message>(),
            () => false,
            CancellationToken.None);
        SetField(app, "_mascot", mascot);
        SetField(app, "_bubble", bubble);
        SetField(app, "_alertCenter", center);
        SetField(app, "_conversation", conversation);
        SetField(app, "_conversationWindow", conversationWindow);
        var previousManager = GetField(app, "_alertManager");
        SetField(app, "_alertManager", new AlertManager(state));
        var previousLog = Environment.GetEnvironmentVariable("PEGGY_LOG_PATH");
        var log = Path.Combine(Path.GetTempPath(), $"peggy-bubble-log-{Guid.NewGuid():N}.txt");
        Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", log);
        var surface = typeof(App).GetMethod(
            "SurfaceMaterialAlerts", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Alert notification method not found.");
        try
        {
            foreach (var tier in new[] { AlertTier.Attention, AlertTier.Critical })
            {
                var alert = new AlertRecord
                {
                    Tier = tier,
                    Summary = "A new work item",
                    LastActivityUtc = DateTimeOffset.UtcNow
                };
                surface.Invoke(app, [new List<AlertRecord> { alert }]);
                TestAssert.True(
                    bubble.IsVisible && !center.IsVisible,
                    "A material alert must show a bubble without opening Alerts.");
            }

            center.ShowNearTaskbar();
            center.Left = 20;
            center.Top = 20;
            surface.Invoke(app,
                [new List<AlertRecord> { new()
                {
                    Tier = AlertTier.Attention, LastActivityUtc = DateTimeOffset.UtcNow
                } }]);
            TestAssert.True(
                center.IsVisible && center.Left == 20 && center.Top == 20,
                "A bubble notification repositioned the user's already-open Alerts.");

            center.Hide();
            var target = new AlertRecord
            {
                Tier = AlertTier.Attention,
                Summary = "The specific alert behind the notification",
                LastActivityUtc = DateTimeOffset.UtcNow
            };
            state.State.Alerts.Add(new AlertRecord
            {
                Tier = AlertTier.Critical,
                Summary = "A different, higher-priority alert",
                LastActivityUtc = DateTimeOffset.UtcNow
            });
            state.State.Alerts.Add(target);
            surface.Invoke(app, [new List<AlertRecord> { target }]);
            Click(bubble, "CloseBubbleButton");
            TestAssert.True(!bubble.IsVisible && !center.IsVisible &&
                target.Status == AlertStatus.Active,
                "Closing the bubble must hide only the popup, not resolve its alert or open Alerts.");

            surface.Invoke(app, [new List<AlertRecord> { target }]);
            Click(bubble, "BubbleBodyButton");
            var card = Find<StackPanel>(center, "CardsPanel").Children.OfType<Border>()
                .Single(item => item.Tag as string == target.Id);
            TestAssert.True(center.IsVisible && !bubble.IsVisible &&
                card.BorderThickness == new Thickness(2) &&
                target.Status == AlertStatus.Active,
                "Clicking an alert bubble must open and highlight that exact active card without resolving it.");
            center.Hide();

            var privacy = (PrivacyGuard)GetField(app, "_privacy")!;
            privacy.SetSessionLocked(true);
            try
            {
                surface.Invoke(app, [new List<AlertRecord> { target }]);
                TestAssert.True(Find<TextBlock>(bubble, "Text").Text != target.Summary,
                    "A masked alert leaked its summary in the actionable bubble.");
                Click(bubble, "BubbleBodyButton");
                TestAssert.True(center.IsVisible &&
                    !ContainsText(center, target.Summary),
                    "Opening the alert from a bubble bypassed existing privacy masking.");
            }
            finally
            {
                privacy.SetSessionLocked(false);
                center.Hide();
            }

            surface.Invoke(app, [new List<AlertRecord> { target }]);
            SetField(app, "_accountSwitching", true);
            try
            {
                Click(bubble, "BubbleBodyButton");
                TestAssert.True(!center.IsVisible,
                    "An old account's bubble opened Alerts during an account switch.");
            }
            finally { SetField(app, "_accountSwitching", false); }

            var generation = (int)GetField(app, "_monitoringGeneration")!;
            surface.Invoke(app, [new List<AlertRecord> { target }]);
            SetField(app, "_monitoringGeneration", generation + 1);
            try
            {
                Click(bubble, "BubbleBodyButton");
                TestAssert.True(!center.IsVisible,
                    "A stale monitoring session's bubble opened Alerts.");
            }
            finally { SetField(app, "_monitoringGeneration", generation); }

            surface.Invoke(app, [new List<AlertRecord> { target }]);
            target.Status = AlertStatus.Resolved;
            Click(bubble, "BubbleBodyButton");
            TestAssert.True(center.IsVisible && target.Status == AlertStatus.Resolved &&
                !Find<StackPanel>(center, "CardsPanel").Children.OfType<Border>()
                    .Any(item => item.Tag as string == target.Id),
                "A stale bubble resurrected an alert that was already resolved.");
        }
        finally
        {
            SetField(app, "_alertManager", previousManager);
            Environment.SetEnvironmentVariable("PEGGY_LOG_PATH", previousLog);
            if (File.Exists(log)) File.Delete(log);
            var timer = typeof(App).GetField(
                "_idleTimer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(app) as DispatcherTimer;
            timer?.Stop();
            bubble.HideNow();
            bubble.Close();
            center.Close();
            conversationWindow.Close();
            mascot.Close();
            conversation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void TestBubbleLifecycle()
    {
        var bubble = new SpeechBubbleWindow();
        var anchor = new Point(420, 420);
        var first = 0;
        var second = 0;
        try
        {
            bubble.Say("First alert", anchor, 60, () => first++);
            TestAssert.True(!bubble.ShowActivated && bubble.IsHitTestVisible &&
                AutomationProperties.GetName(Find<Button>(bubble, "CloseBubbleButton")) == "Close bubble",
                "The bubble must be interactive and accessible without activating itself on arrival.");
            Click(bubble, "CloseBubbleButton");
            TestAssert.True(!bubble.IsVisible && first == 0,
                "The close button also activated the bubble body.");
            Click(bubble, "BubbleBodyButton");
            TestAssert.Equal(0, first, "A hidden bubble retained an executable action.");

            bubble.Say("First alert", anchor, 60, () => first++);
            bubble.Say("Replacement alert", anchor, 60, () => second++);
            Click(bubble, "BubbleBodyButton");
            TestAssert.True(first == 0 && second == 1 && !bubble.IsVisible,
                "Replacement notifications invoked a stale action or activated twice.");

            bubble.Say("Alert", anchor, 60, () => first++);
            bubble.Say("Ordinary Peggy greeting", anchor, 60);
            TestAssert.True(!Find<Button>(bubble, "BubbleBodyButton").IsEnabled,
                "A non-alert speech bubble retained the previous alert action.");
            Click(bubble, "BubbleBodyButton");
            TestAssert.True(first == 0 && bubble.IsVisible,
                "A greeting opened an unrelated alert.");
            Click(bubble, "CloseBubbleButton");

            bubble.Say("Keyboard dismissal", anchor, 60, () => first++);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(bubble), 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            bubble.RaiseEvent(key);
            TestAssert.True(key.Handled && !bubble.IsVisible && first == 0,
                "Escape did not close the bubble without activating its alert.");

            bubble.Say("Fading alert", anchor, 60, () => first++);
            typeof(SpeechBubbleWindow).GetMethod("FadeOut",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bubble, null);
            bubble.Say("New alert during fade", anchor, 60, () => second++);
            SetupStepIndicatorTests.Complete(Task.Delay(450));
            TestAssert.True(bubble.IsVisible && Find<Button>(bubble, "BubbleBodyButton").IsEnabled,
                "An old fade-out hid a replacement notification.");
            Click(bubble, "BubbleBodyButton");
            TestAssert.Equal(2, second, "A replacement notification lost its action.");

            bubble.Say("Expiring alert", anchor, 0.03, () => first++);
            SetupStepIndicatorTests.Complete(Task.Delay(500));
            TestAssert.True(!bubble.IsVisible && !Find<Button>(bubble, "BubbleBodyButton").IsEnabled,
                "Timed dismissal did not clear the alert action.");
        }
        finally { bubble.Close(); }
    }

    private static bool ContainsText(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text == text) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (ContainsText(VisualTreeHelper.GetChild(root, index), text)) return true;
        return false;
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement =>
        (T)window.FindName(name);

    private static void Click(Window window, string name) =>
        Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static object? GetField(App app, string name) =>
        typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app);

    private static void SetField(App app, string name, object? value)
    {
        var field = typeof(App).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Application field {name} not found.");
        field.SetValue(app, value);
    }
}
