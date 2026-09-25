extern alias setup;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Peggy.Core;
using setup::Peggy.SetupBootstrapper;

namespace Peggy.Core.Tests;

internal static class SetupAppLauncherSmokeTests
{
    internal static async Task RunAsync()
    {
        var logs = new List<string>();
        var diagnostics = new SetupDiagnostics(logs.Add, "synthetic-launch");
        var releases = 0;
        using (var missing = new SetupAppLauncher(diagnostics,
            _ => throw new Exception("Missing executable reached process start."), _ => false))
        {
            await ExpectFailureAsync(() => missing.LaunchAsync(() => releases++, CancellationToken.None), "unavailable");
            TestAssert.True(releases == 0, "Missing executable released the application guard.");
        }

        foreach (var failure in new[] { "throw", "null" })
        {
            var calls = 0;
            using var failed = new SetupAppLauncher(diagnostics, info =>
            {
                calls++;
                TestAssert.True(releases == 1 && info.FileName == SetupAppLauncher.Target &&
                    !info.UseShellExecute && string.IsNullOrEmpty(info.Verb) &&
                    info.WorkingDirectory == Path.GetDirectoryName(SetupAppLauncher.Target),
                    "Launch did not use the canonical per-user target after releasing the guard.");
                if (failure == "throw") throw new Win32Exception(5, "Synthetic process start failure.");
                return null;
            }, _ => true);
            releases = 0;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await failed.LaunchAsync(() => releases++, cancellation.Token);
                throw new Exception("Pre-cancelled launch was accepted.");
            }
            catch (OperationCanceledException) { }
            TestAssert.True(calls == 0 && releases == 0, "Cancellation released the guard or started a process.");
            try
            {
                await failed.LaunchAsync(() => releases++, CancellationToken.None);
                throw new Exception("Failed process start was accepted.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            TestAssert.True(calls == 1, "Process start failure was not reported.");
        }

        foreach (var exitCode in new[] { 0, 17 })
        {
            using var exited = new SetupAppLauncher(diagnostics, _ => Process.Start(new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "/d", "/c", $"exit {exitCode}" }
            }), _ => true);
            await ExpectFailureAsync(() => exited.LaunchAsync(() => { }, CancellationToken.None), $"code {exitCode}");
        }
        TestAssert.True(logs.Any(line => line.Contains("exited-during-handoff") && line.Contains("exitCode=17")),
            "Immediate exit diagnostics lost the child's exit code.");

        Process? child = null;
        Process? witness = null;
        try
        {
            var started = 0;
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var running = new SetupAppLauncher(diagnostics, _ =>
            {
                started++;
                child = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
                });
                witness = Process.GetProcessById(child!.Id);
                return child;
            }, _ => true, _ => observed.Task);
            using var cancellation = new CancellationTokenSource();
            var launching = running.LaunchAsync(() => { }, cancellation.Token);
            await ExpectFailureAsync(() => running.LaunchAsync(() => { }, CancellationToken.None), "already in progress");
            cancellation.Cancel();
            observed.SetResult(false);
            try
            {
                await launching;
                throw new Exception("Cancellation during launch observation was reported as completion.");
            }
            catch (OperationCanceledException) { }
            await running.LaunchAsync(() => throw new Exception("Retry released the guard again."), CancellationToken.None);
            TestAssert.True(started == 1 && !witness!.HasExited &&
                logs.Any(line => line.Contains("reusing-started-process")) &&
                logs.Any(line => line.Contains("uiReadinessVerified=False")),
                "Retry duplicated a live child, killed it on cancellation, or claimed unverified UI readiness.");
            running.Dispose();
            TestAssert.True(!witness!.HasExited, "Disposing setup's launch handle killed Peggy.");
        }
        finally
        {
            if (witness is not null)
            {
                if (!witness.HasExited) witness.Kill(entireProcessTree: true);
                witness.WaitForExit(5000);
                witness.Dispose();
            }
            child?.Dispose();
        }
    }

    private static async Task ExpectFailureAsync(Func<Task> action, string message)
    {
        try
        {
            await action();
            throw new Exception("Expected launch failure was not reported.");
        }
        catch (InvalidOperationException ex)
        {
            TestAssert.True(ex.Message.Contains(message, StringComparison.Ordinal),
                $"Unexpected launch failure: {ex.Message}");
        }
    }
}
