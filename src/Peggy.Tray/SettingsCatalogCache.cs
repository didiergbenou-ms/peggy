using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Peggy.Tray;

internal readonly record struct SettingsCatalogKey(
    string Kind,
    string Id = "",
    string Path = "");

internal sealed record SettingsCatalogSnapshot<T>(
    IReadOnlyList<T>? Values,
    DateTimeOffset? UpdatedUtc,
    string? Error,
    bool IsStale);

// Metadata only: this cache must never be used to authorize monitoring reads.
internal sealed class SettingsCatalogCache
{
    private static readonly object AccountsLock = new();
    private static readonly Dictionary<(string Account, string Principal),
        SettingsCatalogCache> Accounts = new();
    private readonly object _gate = new();
    private readonly Dictionary<SettingsCatalogKey, object> _entries = new();
    private readonly SemaphoreSlim _fetchSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _ttl;
    private bool _invalidated;

    internal SettingsCatalogCache(
        Func<DateTimeOffset>? now = null,
        TimeSpan? ttl = null,
        int concurrency = 4)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? TimeSpan.FromMinutes(30);
        _fetchSlots = new SemaphoreSlim(concurrency, concurrency);
    }

    internal static SettingsCatalogCache ForAccount(
        string? verifiedAccount,
        string? principalId = null)
    {
        if (string.IsNullOrWhiteSpace(verifiedAccount))
        {
            return new SettingsCatalogCache();
        }
        var key = (
            verifiedAccount.Trim().ToUpperInvariant(),
            principalId?.Trim().ToUpperInvariant() ?? "");
        lock (AccountsLock)
        {
            if (!Accounts.TryGetValue(key, out var cache))
            {
                cache = new SettingsCatalogCache();
                Accounts.Add(key, cache);
            }
            return cache;
        }
    }

    internal static void InvalidateAccount(string verifiedAccount)
    {
        var account = verifiedAccount.Trim().ToUpperInvariant();
        SettingsCatalogCache[] removed;
        lock (AccountsLock)
        {
            var keys = Accounts.Keys.Where(key => key.Account == account).ToArray();
            removed = keys.Select(key => Accounts[key]).ToArray();
            foreach (var key in keys)
            {
                Accounts.Remove(key);
            }
        }
        foreach (var cache in removed)
        {
            cache.Invalidate();
        }
    }

    internal bool IsValid
    {
        get { lock (_gate) { return !_invalidated; } }
    }

    internal SettingsCatalogSnapshot<T> Read<T>(SettingsCatalogKey key)
    {
        lock (_gate)
        {
            return _invalidated
                ? new(null, null, null, true)
                : Snapshot(GetEntry<T>(key));
        }
    }

    internal async Task<SettingsCatalogSnapshot<T>> RefreshAsync<T>(
        SettingsCatalogKey key,
        Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        bool force = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Entry<T> entry;
        Pending<T> pending;
        bool start;
        lock (_gate)
        {
            if (_invalidated)
            {
                throw new OperationCanceledException("The account catalog was invalidated.");
            }
            entry = GetEntry<T>(key);
            if (!force && entry.Pending is null && !Snapshot(entry).IsStale)
            {
                return Snapshot(entry);
            }
            start = entry.Pending is null;
            pending = entry.Pending ??= new Pending<T>(_lifetime.Token);
            pending.Waiters++;
        }
        if (start)
        {
            _ = FetchAsync(entry, pending, load);
        }
        try
        {
            return await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (_gate)
            {
                pending.Waiters--;
                if (pending.Waiters == 0 && ReferenceEquals(entry.Pending, pending))
                {
                    entry.Pending = null;
                    cancel = true;
                }
            }
            if (cancel)
            {
                try { pending.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
            _entries.Clear();
        }
        _lifetime.Cancel();
    }

    private async Task FetchAsync<T>(
        Entry<T> entry,
        Pending<T> pending,
        Func<CancellationToken, Task<IReadOnlyList<T>>> load)
    {
        try
        {
            IReadOnlyList<T>? values = null;
            string? error = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                pending.Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var result = await LoadBoundedAsync(load, timeout.Token).WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                values = Array.AsReadOnly(result.ToArray());
            }
            catch (OperationCanceledException)
            {
                error = "The metadata request was canceled or took too long. Retry to try again.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                InvalidOperationException or TimeoutException or Win32Exception or
                UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            lock (_gate)
            {
                if (_invalidated || pending.Cancellation.IsCancellationRequested ||
                    !ReferenceEquals(entry.Pending, pending))
                {
                    pending.Completion.TrySetCanceled();
                }
                else
                {
                    var updatedUtc = values is not null ? _now() : entry.UpdatedUtc;
                    entry.Pending = null;
                    if (values is not null)
                    {
                        entry.Values = values;
                        entry.UpdatedUtc = updatedUtc;
                    }
                    entry.Error = error;
                    pending.Completion.TrySetResult(Snapshot(entry));
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(entry.Pending, pending))
                {
                    entry.Pending = null;
                }
                pending.Completion.TrySetException(ex);
            }
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    private async Task<IReadOnlyList<T>> LoadBoundedAsync<T>(
        Func<CancellationToken, Task<IReadOnlyList<T>>> load,
        CancellationToken ct)
    {
        await _fetchSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await load(ct).ConfigureAwait(false);
        }
        finally
        {
            // Keep the slot until even a cancellation-ignoring provider has finished.
            _fetchSlots.Release();
        }
    }

    private Entry<T> GetEntry<T>(SettingsCatalogKey key)
    {
        if (!_entries.TryGetValue(key, out var value))
        {
            value = new Entry<T>();
            _entries.Add(key, value);
        }
        return (Entry<T>)value;
    }

    private SettingsCatalogSnapshot<T> Snapshot<T>(Entry<T> entry)
        => new(
            entry.Values,
            entry.UpdatedUtc,
            entry.Error,
            entry.Values is null || entry.Error is not null ||
            entry.UpdatedUtc is null || _now() - entry.UpdatedUtc.Value >= _ttl);

    private sealed class Entry<T>
    {
        public IReadOnlyList<T>? Values;
        public DateTimeOffset? UpdatedUtc;
        public string? Error;
        public Pending<T>? Pending;
    }

    private sealed class Pending<T>(CancellationToken lifetime)
    {
        public readonly TaskCompletionSource<SettingsCatalogSnapshot<T>> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public int Waiters;
    }
}
