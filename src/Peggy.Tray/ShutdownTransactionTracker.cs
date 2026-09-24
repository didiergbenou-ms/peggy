using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Peggy.Tray;

internal sealed class ShutdownTransactionTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private bool _stopping;

    internal Task RunAsync(Func<Task> operation)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) return Task.CompletedTask;
            _active.Add(drained.Task);
        }
        return RunTrackedAsync(operation, drained);
    }

    private async Task RunTrackedAsync(Func<Task> operation, TaskCompletionSource drained)
    {
        try { await operation(); }
        finally
        {
            lock (_gate) _active.Remove(drained.Task);
            drained.SetResult();
        }
    }

    internal Task StopAndDrainAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return Task.WhenAll(_active);
        }
    }
}
