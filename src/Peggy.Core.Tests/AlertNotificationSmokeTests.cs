using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class AlertNotificationSmokeTests
{
    public static void Run(App app)
    {
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
                [new List<AlertRecord> { new() { Tier = AlertTier.Attention } }]);
            TestAssert.True(
                center.IsVisible && center.Left == 20 && center.Top == 20,
                "A bubble notification repositioned the user's already-open Alerts.");
        }
        finally
        {
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

    private static void SetField(App app, string name, object value)
    {
        var field = typeof(App).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Application field {name} not found.");
        field.SetValue(app, value);
    }
}
