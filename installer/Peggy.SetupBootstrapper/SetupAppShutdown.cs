using Peggy.Core;

namespace Peggy.SetupBootstrapper;

internal sealed record SetupProcessIdentity(int Pid, long StartFileTime, string Path,
    string Sid, int Session, string Version);

internal interface ISetupShutdownPlatform
{
    IReadOnlyList<SetupProcessIdentity> FindRunning();
    void Validate(SetupProcessIdentity process);
    Task<bool> RequestModernAsync(SetupProcessIdentity process, CancellationToken ct);
    Task RequestLegacyAsync(SetupProcessIdentity process, CancellationToken ct);
    Task WaitForExitAsync(SetupProcessIdentity process, CancellationToken ct);
    bool FilesUnlocked();
}

internal sealed class SetupAppShutdown(ISetupShutdownPlatform platform, SetupDiagnostics diagnostics,
    Action<string> status, TimeSpan? timeout = null)
{
    internal static bool IsKnownLegacy(string version) =>
        version.Split('+', 2)[0] is "0.1.13" or "0.1.14-beta.1";

    internal async Task AcquireAsync(Func<bool> acquireAppGuard, Action assertSetupOwnership,
        bool mayClose, CancellationToken ct)
    {
        assertSetupOwnership();
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            var processes = platform.FindRunning();
            // Validate the entire set before requesting any shutdown.
            foreach (var process in processes) platform.Validate(process);
            if (processes.Count != 0 && !mayClose)
                throw Blocked("Peggy is running. Quiet/maintenance setup does not close it automatically.");
            foreach (var process in processes)
            {
                assertSetupOwnership();
                deadline.Token.ThrowIfCancellationRequested();
                platform.Validate(process);
                status("Closing Peggy safely — waiting for current work, voice and saved settings. Cancel stops setup, not Peggy's save.");
                diagnostics.Event("app.shutdown", "requested", $"pid={process.Pid}; route=pipe");
                if (!await platform.RequestModernAsync(process, deadline.Token))
                {
                    if (!IsKnownLegacy(process.Version))
                        throw Blocked("Peggy's safe shutdown endpoint is unavailable. Wait for Peggy to finish starting, or choose Quit Peggy from its tray menu.");
                    platform.Validate(process);
                    assertSetupOwnership();
                    deadline.Token.ThrowIfCancellationRequested();
                    status("Closing the older Peggy through Windows. Older versions cannot confirm a final settings save; if they do not close, use Quit Peggy, then Retry.");
                    diagnostics.Event("app.shutdown", "requested", $"pid={process.Pid}; route=legacy-restart-manager");
                    await platform.RequestLegacyAsync(process, deadline.Token);
                }
                await platform.WaitForExitAsync(process, deadline.Token);
                diagnostics.Event("app.shutdown", "exited", $"pid={process.Pid}");
            }
            assertSetupOwnership();
            deadline.Token.ThrowIfCancellationRequested();
            if (!acquireAppGuard())
                throw Blocked("Peggy's application lock is still owned, or Peggy started again. No installation was started.");
            while (!platform.FilesUnlocked())
                await Task.Delay(100, deadline.Token);
            diagnostics.Event("app.guard", "owned", "filesUnlocked=True");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            diagnostics.Event("app.shutdown", "timeout");
            throw Blocked("Peggy has not finished closing or its files are still in use. No app was force-closed. Finish any open Peggy settings/account dialog, wait, or choose Quit Peggy from its tray menu.");
        }
        catch (OperationCanceledException)
        {
            diagnostics.Event("app.shutdown", "cancelled", "acceptedShutdownMayStillComplete=True");
            throw;
        }
        catch (InvalidOperationException ex)
        {
            diagnostics.Failure("app.shutdown", ex);
            throw;
        }
    }

    internal static InvalidOperationException Blocked(string message) =>
        new(message + " Choose Retry and then Install / update to try again, or Cancel to close setup.");
}
