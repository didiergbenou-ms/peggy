using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class SetupStepIndicatorTests
{
    public static void RunDesktop()
    {
        var first = new SetupStepIndicator { State = SetupStepState.Running };
        var second = new SetupStepIndicator { State = SetupStepState.Running };
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        var host = new Window { Content = panel, Width = 180, Height = 120, ShowInTaskbar = false };
        try
        {
            TestAssert.True(!first.IsAnimating, "An unloaded setup indicator started an animation.");
            host.Show();
            DrainDispatcher();
            TestAssert.True(first.IsAnimating == SystemParameters.ClientAreaAnimation &&
                second.IsAnimating == SystemParameters.ClientAreaAnimation,
                "Loaded independent setup indicators did not respect system animation settings.");
            first.State = SetupStepState.Succeeded;
            TestAssert.True(!first.IsAnimating && second.State == SetupStepState.Running &&
                second.IsAnimating == SystemParameters.ClientAreaAnimation,
                "Completing one setup step stopped another active step.");
            TestAssert.True(AutomationProperties.GetName(first).Contains("Succeeded"),
                "Successful setup was communicated through color alone.");
            second.AnimationEnabled = false;
            TestAssert.True(!second.IsAnimating && second.State == SetupStepState.Running &&
                AutomationProperties.GetItemStatus(second) == "Running",
                "Reduced motion removed the running status or retained animation.");
            second.AnimationEnabled = true;
            second.Visibility = Visibility.Collapsed;
            TestAssert.True(!second.IsAnimating, "A hidden setup step retained an animation.");
            second.Visibility = Visibility.Visible;
            foreach (var state in new[] { SetupStepState.WaitingForUser, SetupStepState.Failed,
                SetupStepState.Skipped, SetupStepState.Idle })
            {
                second.State = state;
                TestAssert.True(!second.IsAnimating && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(second)),
                    $"Setup {state} retained an animation or lost its accessible status.");
            }
            second.State = SetupStepState.Running;
            panel.Children.Remove(second);
            DrainDispatcher();
            TestAssert.True(!second.IsAnimating, "An unloaded setup step retained an animation.");
            first.State = SetupStepState.Running;
            host.Close();
            DrainDispatcher();
            TestAssert.True(!first.IsAnimating, "Closing setup retained an animation.");
        }
        finally { host.Close(); }
    }

    internal static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void Complete(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(10))
            DrainDispatcher();
        TestAssert.True(task.IsCompleted, "A fake setup operation did not complete.");
        task.GetAwaiter().GetResult();
        DrainDispatcher();
    }
}
