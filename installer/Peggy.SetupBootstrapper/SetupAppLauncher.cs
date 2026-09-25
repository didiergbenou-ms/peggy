using System.Diagnostics;
using System.IO;
using Peggy.Core;

namespace Peggy.SetupBootstrapper;

internal sealed class SetupAppLauncher(
    SetupDiagnostics diagnostics,
    Func<ProcessStartInfo, Process?>? start = null,
    Func<string, bool>? exists = null,
    Func<Process, Task<bool>>? observeExit = null) : IDisposable
{
    internal const int ObservationMilliseconds = 1500;
    private Process? _process;
    private bool _launching;

    internal static string Target => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Peggy", "peggy-tray.exe");

    internal async Task LaunchAsync(Action releaseGuard, CancellationToken ct)
    {
        if (_launching) throw new InvalidOperationException("Peggy launch is already in progress.");
        ct.ThrowIfCancellationRequested();
        _launching = true;
        try
        {
            if (_process?.HasExited == true) Dispose();
            if (_process is null)
            {
                if (!(exists ?? File.Exists)(Target))
                    throw new InvalidOperationException(
                        "Peggy is installed, but its executable is unavailable. Repair the installation, then retry launch or open Peggy from Start.");
                // Release on the owning WPF thread, not in the background observer.
                releaseGuard();
                ct.ThrowIfCancellationRequested();
                _process = diagnostics.Run("finish.start-process", () =>
                    (start ?? Process.Start)(new ProcessStartInfo(Target)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(Target)!
                    }) ?? throw new InvalidOperationException(
                        "Setup is saved, but Peggy could not start. Retry launch or open Peggy from Start."));
                diagnostics.Event("finish.child-process", "started", $"pid={_process.Id}");
            }
            else
            {
                diagnostics.Event("finish.child-process", "reusing-started-process", $"pid={_process.Id}");
            }

            var process = _process;
            var exited = await diagnostics.RunAsync("finish.observe-process", () =>
                observeExit?.Invoke(process) ?? Task.Run(() => process.WaitForExit(ObservationMilliseconds)));
            if (exited || process.HasExited)
            {
                var code = process.ExitCode;
                diagnostics.Event("finish.child-process", "exited-during-handoff",
                    $"pid={process.Id}; exitCode={code}; observationMilliseconds={ObservationMilliseconds}");
                Dispose();
                throw new InvalidOperationException(
                    $"Peggy exited during startup (code {code}). Setup is saved. Retry launch, or review Peggy's logs and open it from Start.");
            }
            diagnostics.Event("finish.child-process", "running-after-observation",
                $"pid={process.Id}; observationMilliseconds={ObservationMilliseconds}; uiReadinessVerified=False");
            // Cancellation never kills a launched app. Keep its handle so retry
            // observes the same process instead of starting a duplicate.
            ct.ThrowIfCancellationRequested();
        }
        finally { _launching = false; }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _process = null;
    }
}
