using System;
using System.Threading;
using System.Security.Principal;

namespace Peggy.Tray;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly Mutex? _compatibilityMutex;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceGuard(string? name = null)
        : this(name ?? CurrentUserName, name is null ? @"Local\Peggy.Tray" : null)
    {
    }

    internal SingleInstanceGuard(string name, string? compatibilityName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "A single-instance mutex name is required.",
                nameof(name));
        }

        _mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (compatibilityName is not null)
                _compatibilityMutex = new Mutex(initiallyOwned: false, compatibilityName);
        }
        catch { _mutex.Dispose(); throw; }
    }

    private static string CurrentUserName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return @"Global\Peggy.Tray." + identity.User!.Value;
        }
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex)
        {
            return true;
        }

        if (!TryWait(_mutex)) return false;
        try
        {
            // Also retain the original session mutex so shipped Peggy versions
            // cannot start while this app/setup owns the new per-user guard.
            if (_compatibilityMutex is not null && !TryWait(_compatibilityMutex))
            {
                _mutex.ReleaseMutex();
                return false;
            }
            _ownsMutex = true;
        }
        catch { _mutex.ReleaseMutex(); throw; }
        return _ownsMutex;
    }

    private static bool TryWait(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsMutex)
        {
            _compatibilityMutex?.ReleaseMutex();
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
        _compatibilityMutex?.Dispose();
        _disposed = true;
    }
}
