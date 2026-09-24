using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Peggy.Tray;

public partial class App
{
    private SetupShutdownPipe? _setupShutdown;
    private Task? _quitTask;
    private bool _runtimeStoppedForQuit;
    private readonly ShutdownTransactionTracker _settingsTransactions = new();

    private void StartSetupShutdownEndpoint()
    {
        using var process = Process.GetCurrentProcess();
        _setupShutdown = new SetupShutdownPipe(
            SetupShutdownPipe.Name(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc()),
            process.SessionId,
            () => Dispatcher.InvokeAsync(RequestSetupShutdownAsync).Task.Unwrap(),
            ex => Log(ex is SetupShutdownRequestRejectedException rejected
                ? $"SETUP SHUTDOWN REJECTED reason={rejected.Reason}"
                : $"SETUP SHUTDOWN ERROR type={ex.GetType().Name}"));
    }

    private async Task RequestSetupShutdownAsync()
    {
        // A modal identity/settings transaction can commit independently of the
        // runtime cancellation token. Let it finish, never tear it down mid-save.
        while (_accountSwitching || _settingsWindow is { IsVisible: true })
            await Task.Delay(100);
        await QuitAppAsync();
    }

    private async Task StopRuntimeForQuitAsync()
    {
        if (_runtimeStoppedForQuit) return;
        while (_accountSwitching || _settingsWindow is { IsVisible: true })
            await Task.Delay(100);
        _quitting = true;
        _poll?.Stop();
        _scheduleTimer?.Stop();
        _idleTimer?.Stop();
        _alertRevealTimer?.Stop();
        _wakeCheckTimer?.Stop();
        foreach (Window window in Windows) window.IsEnabled = false;
        if (_tray?.ContextMenuStrip is { } menu)
        {
            foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
                item.Enabled = ReferenceEquals(item, _quitMenu);
        }
        _bubble?.HideNow();
        // Hiding Settings does not mean its accepted save is done. Drain the
        // runtime transaction before cancelling its token. Do not hold this gate
        // while draining alert context, which acquires gates in the other order.
        await GracefulShutdownSequence.CancelAfterRuntimeDrainAsync(_settingsTransactions, _runtimeGate, _shutdown);
        await _alertContextGate.WaitAsync();
        _alertContextGate.Release();
        if (_conversation is not null) await _conversation.DisposeAsync();
        await ObserveStoppedWorkAsync(_activeCheck);
        await ObserveStoppedWorkAsync(_copilotWarmup);
        if (_copilot is not null) await _copilot.DisposeAsync();
        _runtimeStoppedForQuit = true;
    }

    private static async Task ObserveStoppedWorkAsync(Task work)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"SHUTDOWN WORK ERROR type={ex.GetType().Name}"); }
    }
}

internal static class GracefulShutdownSequence
{
    internal static async Task CancelAfterRuntimeDrainAsync(ShutdownTransactionTracker settings,
        SemaphoreSlim runtimeGate, CancellationTokenSource shutdown)
    {
        await settings.StopAndDrainAsync();
        await runtimeGate.WaitAsync();
        try { shutdown.Cancel(); }
        finally { runtimeGate.Release(); }
    }

    internal static async Task RunAsync(Func<Task> stopAndDrain, Func<Task> save, Action exit)
    {
        await stopAndDrain();
        await save(); // A failed final save must leave the process/guard alive for retry.
        exit();
    }
}
