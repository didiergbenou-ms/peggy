using System.IO;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class SettingsCatalogCacheSmokeTests
{
    public static async Task RunAsync()
    {
        await TestIdentityAsync();
        await TestFreshnessAndRetryAsync();
        await TestFailureClassificationAsync();
        await TestCancellationAsync();
        await TestConcurrencyAsync();
    }

    private static async Task TestIdentityAsync()
    {
        const string account = "catalog-cache@example.invalid";
        var key = new SettingsCatalogKey("teams");
        var first = SettingsCatalogCache.ForAccount(account, "principal-a");
        await first.RefreshAsync(key, _ => Result("private team"));
        TestAssert.True(
            ReferenceEquals(first, SettingsCatalogCache.ForAccount(
                " CATALOG-CACHE@example.invalid ", "PRINCIPAL-A")) &&
            SettingsCatalogCache.ForAccount("other@example.invalid", "principal-a")
                .Read<string>(key).Values is null &&
            SettingsCatalogCache.ForAccount(account, "principal-b")
                .Read<string>(key).Values is null &&
            SettingsCatalogCache.ForAccount(account).Read<string>(key).Values is null &&
            !ReferenceEquals(SettingsCatalogCache.ForAccount(null),
                SettingsCatalogCache.ForAccount(null)),
            "Catalog metadata crossed an account/principal boundary.");
        SettingsCatalogCache.InvalidateAccount(account);
        TestAssert.True(!first.IsValid && first.Read<string>(key).Values is null &&
            SettingsCatalogCache.ForAccount(account, "principal-a").Read<string>(key).Values is null,
            "Account invalidation retained reusable metadata.");
    }

    private static async Task TestFreshnessAndRetryAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new SettingsCatalogCache(() => now);
        var key = new SettingsCatalogKey("folders", "parent");
        var count = 0;
        Task<IReadOnlyList<string>> Load(CancellationToken _)
        {
            count++;
            return Result("saved folder");
        }
        await cache.RefreshAsync(key, Load);
        await cache.RefreshAsync(key, Load);
        TestAssert.Equal(1, count, "Reopening a fresh catalog fetched metadata again.");
        now += TimeSpan.FromMinutes(31);
        var delayed = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<string>> Refresh(CancellationToken _)
        {
            count++;
            return delayed.Task;
        }
        var background = cache.RefreshAsync(key, Refresh);
        var duplicate = cache.RefreshAsync(key, Refresh, force: true);
        TestAssert.True(!background.IsCompleted && count == 2 &&
            cache.Read<string>(key) is { IsStale: true, Values: ["saved folder"] },
            "Stale metadata was blanked or concurrent refreshes were not deduplicated.");
        delayed.SetResult(["new folder"]);
        await Task.WhenAll(background, duplicate);
        var successfulTime = cache.Read<string>(key).UpdatedUtc;
        now += TimeSpan.FromMinutes(1);
        var failed = await cache.RefreshAsync<string>(key,
            _ => throw new InvalidOperationException("access unavailable"), force: true);
        TestAssert.True(failed.Values is ["new folder"] &&
            failed.UpdatedUtc == successfulTime && failed.Error == "access unavailable" &&
            failed.IsStale,
            "A failed refresh erased stale metadata or advanced the successful timestamp.");
        var retried = await cache.RefreshAsync(key, _ => Result(), force: true);
        TestAssert.True(retried.Values is { Count: 0 } && retried.Error is null &&
            retried.UpdatedUtc == now && !retried.IsStale,
            "Retry did not clear the failure and timestamp for a successful empty result.");
        await cache.RefreshAsync(key, Load, force: true);
        TestAssert.Equal(3, count, "Forced refresh reused a fresh cached result.");
    }

    private static async Task TestCancellationAsync()
    {
        var cache = new SettingsCatalogCache();
        var key = new SettingsCatalogKey("channels", "same-team");
        await cache.RefreshAsync(key, _ => Result("original"));
        var delayed = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var closed = new CancellationTokenSource();
        var canceled = cache.RefreshAsync(key, _ => delayed.Task, true, closed.Token);
        closed.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => canceled, "Closing Settings did not cancel its wait.");
        var replacement = await cache.RefreshAsync(key, _ => Result("replacement"), true);
        delayed.SetResult(["late result"]);
        await Task.Yield();
        TestAssert.True(replacement.Values is ["replacement"] &&
            cache.Read<string>(key).Values is ["replacement"],
            "A canceled request overwrote newer metadata.");

        var shared = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var oneWindow = new CancellationTokenSource();
        var waiterOne = cache.RefreshAsync(key, _ => shared.Task, true, oneWindow.Token);
        var waiterTwo = cache.RefreshAsync<string>(key, _ => throw new Exception("duplicate"), true);
        oneWindow.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => waiterOne, "A canceled consumer remained attached.");
        shared.SetResult(["shared result"]);
        TestAssert.True((await waiterTwo).Values is ["shared result"],
            "Closing one Settings window canceled another window's shared refresh.");

        var invalidated = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldAccount = cache.RefreshAsync(key, _ => invalidated.Task, true);
        cache.Invalidate();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => oldAccount, "Account invalidation did not cancel pending metadata.");
        invalidated.SetResult(["wrong account"]);
        TestAssert.True(cache.Read<string>(key).Values is null,
            "Invalidated identity metadata remained visible.");
    }

    private static async Task TestFailureClassificationAsync()
    {
        var cache = new SettingsCatalogCache();
        var key = new SettingsCatalogKey("teams");
        await cache.RefreshAsync(key, _ => Result("retained"));
        Exception[] expectedFailures =
        [
            new IOException("transport"),
            new InvalidDataException("malformed response"),
            new InvalidOperationException("WorkIQ request failed"),
            new TimeoutException("process timeout"),
            new System.ComponentModel.Win32Exception("process start failed"),
            new UnauthorizedAccessException("access denied")
        ];
        foreach (var failure in expectedFailures)
        {
            var snapshot = await cache.RefreshAsync<string>(
                key, _ => throw failure, force: true);
            TestAssert.True(snapshot.Values is ["retained"] &&
                snapshot.Error == failure.Message,
                "An expected WorkIQ failure did not retain metadata with a visible error.");
        }
        await cache.RefreshAsync(key, _ => Result("retained"), force: true);
        var successfulTime = cache.Read<string>(key).UpdatedUtc;
        var programmingFailure = new NullReferenceException("programming defect");
        var observed = await TestAssert.ThrowsAsync<NullReferenceException>(
            () => cache.RefreshAsync<string>(key, _ => throw programmingFailure, true)
                .WaitAsync(TimeSpan.FromSeconds(5)),
            "An unexpected programming error was swallowed or left its waiter incomplete.");
        TestAssert.True(ReferenceEquals(observed, programmingFailure) &&
            cache.Read<string>(key) is { Values: ["retained"], Error: null } &&
            cache.Read<string>(key).UpdatedUtc == successfulTime,
            "An unexpected failure was converted to catalog status or mutated the cached snapshot.");
        TestAssert.True((await cache.RefreshAsync(key, _ => Result("recovered"), true))
            .Values is ["recovered"],
            "An unexpected error left the cache request slot unusable.");
    }

    private static async Task TestConcurrencyAsync()
    {
        var cache = new SettingsCatalogCache(concurrency: 2);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var gate = new object();
        async Task<IReadOnlyList<string>> Load(CancellationToken ct)
        {
            lock (gate)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == 2)
                {
                    twoStarted.TrySetResult();
                }
            }
            await release.Task.WaitAsync(ct);
            lock (gate) { active--; }
            return ["loaded"];
        }
        var loads = Enumerable.Range(0, 10)
            .Select(index => cache.RefreshAsync(
                new SettingsCatalogKey("channels", index.ToString()), Load))
            .ToArray();
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TestAssert.Equal(2, maximum, "Catalog fetch concurrency was not bounded.");
        release.SetResult();
        await Task.WhenAll(loads);
        TestAssert.True(maximum <= 2, "Rapid node requests exceeded the fetch limit.");
    }

    private static Task<IReadOnlyList<string>> Result(params string[] values)
        => Task.FromResult<IReadOnlyList<string>>(values);
}
