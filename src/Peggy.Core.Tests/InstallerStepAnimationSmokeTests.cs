using System.Windows;
using System.Windows.Threading;
using Peggy.PrerequisiteCheck;

namespace Peggy.Core.Tests;

internal static class InstallerStepAnimationSmokeTests
{
    internal static void RunVisualPreview()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new PrerequisiteWindow(async (ct, report) =>
        {
            var results = new List<PrerequisiteResult>();
            foreach (var id in PrerequisiteEngine.Ids)
            {
                report(new(id, id, PrerequisiteState.Checking, "Synthetic visual test"));
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
                var result = new PrerequisiteResult(id, id,
                    id == "npm" ? PrerequisiteState.Error : PrerequisiteState.Ready,
                    id == "npm" ? "Synthetic failure: Retry checks." : "Synthetic success.");
                results.Add(result);
                report(result);
            }
            return results;
        }, allowDependencyChanges: false);
        window.Closed += (_, _) => application.Shutdown();
        application.Run(window);
    }

    internal static void RunDesktop()
    {
        var finish = new TaskCompletionSource<IReadOnlyList<PrerequisiteResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Action<PrerequisiteResult>? report = null;
        CancellationToken lifetime = default;
        var window = new PrerequisiteWindow((ct, callback) =>
        {
            lifetime = ct;
            report = callback;
            return finish.Task.WaitAsync(ct);
        });
        try
        {
            window.Show();
            PumpUntil(() => report is not null);
            foreach (var id in PrerequisiteEngine.Ids)
            {
                TestAssert.Equal(SetupStepState.Idle, window.GetStepIndicator(id).State,
                    "An unstarted prerequisite displayed a running animation.");
            }
            report!(new("node", "Node.js", PrerequisiteState.Checking, "Checking"));
            report(new("npm", "npm", PrerequisiteState.Checking, "Checking"));
            PumpUntil(() => window.GetStepIndicator("node").IsAnimating ||
                !SystemParameters.ClientAreaAnimation);
            TestAssert.True(
                window.GetStepIndicator("node").State == SetupStepState.Running &&
                window.GetStepIndicator("npm").State == SetupStepState.Running,
                "Independent prerequisite operations did not show their running state.");
            report(new("node", "Node.js", PrerequisiteState.Ready, "Ready"));
            TestAssert.True(
                window.GetStepIndicator("node").State == SetupStepState.Succeeded &&
                !window.GetStepIndicator("node").IsAnimating &&
                window.GetStepIndicator("npm").State == SetupStepState.Running,
                "Completing one prerequisite stopped another pending step.");
            report(new("npm", "npm", PrerequisiteState.Error, "Synthetic error"));
            TestAssert.True(
                window.GetStepIndicator("npm").State == SetupStepState.Failed &&
                !window.GetStepIndicator("npm").IsAnimating,
                "A failed prerequisite kept its busy animation.");
            report(new("copilot", "Copilot", PrerequisiteState.Checking, "Checking"));
            window.Close();
            PumpUntil(() => lifetime.IsCancellationRequested);
            TestAssert.True(
                PrerequisiteEngine.Ids.All(id => !window.GetStepIndicator(id).IsAnimating),
                "Closing prerequisite setup left animation clocks active.");
        }
        finally
        {
            window.Close();
            finish.TrySetCanceled();
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Installer UI transition timed out.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
