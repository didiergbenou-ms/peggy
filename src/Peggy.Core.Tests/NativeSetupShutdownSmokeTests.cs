extern alias setup;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using setup::Peggy.SetupBootstrapper;

namespace Peggy.Core.Tests;

internal static class NativeSetupShutdownSmokeTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts",
            "native-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SyntheticChild? target = null, unrelated = null;
        try
        {
            target = await SyntheticChild.StartAsync(directory, "target");
            unrelated = await SyntheticChild.StartAsync(directory, "unrelated");
            using var current = WindowsIdentity.GetCurrent();
            var identity = new SetupProcessIdentity(target.Process.Id,
                target.Process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                target.Process.MainModule!.FileName, current.User!.Value,
                target.Process.SessionId, "synthetic");
            var validations = 0;
            void ValidateChild()
            {
                validations++;
                TestAssert.True(!target.Process.HasExited &&
                    target.Process.Id == identity.Pid &&
                    target.Process.StartTime.ToUniversalTime().ToFileTimeUtc() == identity.StartFileTime &&
                    target.Process.MainModule!.FileName == identity.Path &&
                    target.Process.SessionId == Process.GetCurrentProcess().SessionId &&
                    target.Ready,
                    "Only the retained, independently launched synthetic child may receive native shutdown.");
            }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try
                {
                    await RestartManagerShutdown.RequestAsync(identity, ValidateChild, cancelled.Token);
                    throw new InvalidOperationException("Cancelled native shutdown request was accepted.");
                }
                catch (OperationCanceledException) { }
                TestAssert.True(!target.Process.HasExited && !unrelated.Process.HasExited,
                    "Cancellation before native shutdown closed a synthetic child.");
            }
            validations = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await RestartManagerShutdown.RequestAsync(identity, ValidateChild, timeout.Token);
            await target.Process.WaitForExitAsync(timeout.Token);
            var output = await target.Output;
            var errors = await target.Errors;
            TestAssert.True(target.Process.ExitCode == 0 && errors.Length == 0 &&
                output.Contains("RM_QUERY " + target.Nonce, StringComparison.Ordinal) &&
                output.Contains("EXIT " + target.Nonce, StringComparison.Ordinal) &&
                !output.Contains("WATCHDOG_EXIT", StringComparison.Ordinal) &&
                !output.Contains("CONTROL_EXIT", StringComparison.Ordinal) && validations >= 2,
                "Native non-forceful Restart Manager did not gracefully close its sole registered child. " +
                $"exit={target.Process.ExitCode}; output={output}; errors={errors}");
            TestAssert.True(!unrelated.Process.HasExited,
                "Process-only Restart Manager registration also closed the unrelated synthetic child.");
            Console.WriteLine("Native Restart Manager smoke passed: registered child exited gracefully; unrelated child stayed alive.");
        }
        finally
        {
            try { if (target is not null) await target.StopAsync(); }
            finally
            {
                try { if (unrelated is not null) await unrelated.StopAsync(); }
                finally { await UnifiedSetupSmokeTests.DeleteSyntheticDirectoryAsync(directory); }
            }
        }
    }

    private sealed class SyntheticChild(Process process, string stopPath, string nonce)
    {
        internal Process Process { get; } = process;
        internal string Nonce { get; } = nonce;
        internal bool Ready { get; private set; }
        internal Task<string> Output { get; private set; } = Task.FromResult("");
        internal Task<string> Errors { get; } = process.StandardError.ReadToEndAsync();

        internal static async Task<SyntheticChild> StartAsync(string directory, string name)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var stop = Path.Combine(directory, name + ".stop");
            var script = ChildScript.Replace("__NONCE__", nonce, StringComparison.Ordinal)
                .Replace("__STOP__", Convert.ToBase64String(Encoding.Unicode.GetBytes(stop)), StringComparison.Ordinal);
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Sta", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                start.ArgumentList.Add(argument);
            var child = new SyntheticChild(Process.Start(start)
                ?? throw new InvalidOperationException("Could not start isolated synthetic WPF child."), stop, nonce);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var line = await child.Process.StandardOutput.ReadLineAsync(timeout.Token);
                TestAssert.True(line == $"READY {child.Process.Id} {nonce}",
                    $"Synthetic WPF child did not initialize: {line}");
                child.Ready = true;
                child.Output = child.Process.StandardOutput.ReadToEndAsync();
                return child;
            }
            catch
            {
                await child.StopAsync();
                throw;
            }
        }

        internal async Task StopAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    await File.WriteAllTextAsync(stopPath, Nonce);
                    // Cleanup is cooperative, with an independent child watchdog.
                    // No process-name discovery, TerminateProcess or Process.Kill.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
                    await Process.WaitForExitAsync(timeout.Token);
                }
                var errors = await Errors;
                TestAssert.True(Process.ExitCode == 0 && errors.Length == 0,
                    $"Synthetic child failed: exit={Process.ExitCode}; errors={errors}");
            }
            finally { Process.Dispose(); }
        }
    }

    private const string ChildScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        Add-Type -AssemblyName PresentationFramework
        $app = [System.Windows.Application]::new()
        $app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
        $window = [System.Windows.Window]::new()
        $window.Title = 'Peggy synthetic Restart Manager test __NONCE__'
        $window.Width = 1
        $window.Height = 1
        $window.Left = -32000
        $window.Top = -32000
        $window.Opacity = 0
        $window.ShowInTaskbar = $false
        $window.ShowActivated = $false
        $app.add_SessionEnding({
            [Console]::WriteLine('RM_QUERY __NONCE__')
            [Console]::Out.Flush()
        })
        $window.add_Loaded({
            [Console]::WriteLine('READY ' + $PID + ' __NONCE__')
            [Console]::Out.Flush()
        })
        $stop = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__STOP__'))
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        $timer = [System.Windows.Threading.DispatcherTimer]::new()
        $timer.Interval = [TimeSpan]::FromMilliseconds(100)
        $timer.add_Tick({
            if ([IO.File]::Exists($stop)) {
                [Console]::WriteLine('CONTROL_EXIT __NONCE__')
                $timer.Stop()
                $app.Shutdown()
            } elseif ($elapsed.Elapsed.TotalSeconds -ge 90) {
                [Console]::WriteLine('WATCHDOG_EXIT __NONCE__')
                $timer.Stop()
                $app.Shutdown()
            }
        })
        $timer.Start()
        $app.Run($window) | Out-Null
        [Console]::WriteLine('EXIT __NONCE__')
        [Console]::Out.Flush()
        """;
}
