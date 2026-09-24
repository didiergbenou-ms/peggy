using System.Security.Principal;

namespace Peggy.SetupBootstrapper;

// Unlike the app mutex, this lease covers preparation, Apply, onboarding and BA
// shutdown, not native CoreCleanup after BA exit (always suppressed separately).
// A dedicated thread keeps mutex ownership independent of await/COM.
internal sealed class SetupInstanceLease(string? name = null) : IDisposable
{
    private readonly string _name = name ?? CurrentUserName;
    private readonly ManualResetEventSlim _release = new();
    private Thread? _thread;
    private bool _disposed;
    internal bool IsOwned { get; private set; }
    internal static string CurrentUserName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return @"Global\Peggy.Setup." + identity.User!.Value;
        }
    }

    internal bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOwned) return true;
        _thread?.Join();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, _name);
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                ready.SetResult(acquired);
                if (!acquired) return;
                try { _release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Peggy setup ownership" };
        _thread.Start();
        return IsOwned = ready.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _release.Set();
        _thread?.Join();
        IsOwned = false;
        _release.Dispose();
        _disposed = true;
    }
}
