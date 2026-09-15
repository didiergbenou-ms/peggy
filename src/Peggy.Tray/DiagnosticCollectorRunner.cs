using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

internal static class DiagnosticCollectorRunner
{
    private const string CollectorName = "Collect-PeggyDiagnostics.ps1";

    public static string GetCollectorPath()
        => Path.Combine(
            AppContext.BaseDirectory,
            "scripts",
            CollectorName);

    public static async Task<string> CreateAsync(
        CancellationToken ct = default)
    {
        var collector = GetCollectorPath();
        if (!File.Exists(collector))
        {
            throw new FileNotFoundException(
                "Peggy's diagnostic collector is not installed. Reinstall " +
                "Peggy or run scripts\\Collect-PeggyDiagnostics.ps1 from " +
                "the repository.",
                collector);
        }

        var outputDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(outputDirectory);
        var existing = Directory
            .EnumerateFiles(
                outputDirectory,
                "Peggy-Diagnostics-*.zip",
                SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var powershell = Path.Combine(
            Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-ExecutionPolicy",
                     "Bypass",
                     "-File",
                     collector,
                     "-OutputDirectory",
                     outputDirectory,
                     "-RunWorkIqTests",
                     "-NoEmail"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Peggy could not start the diagnostic collector.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                "Peggy's diagnostic collector did not finish within " +
                "10 minutes.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                "Peggy's diagnostic collector failed: " +
                MonitoringFailureInspector.Sanitize(detail));
        }

        var created = Directory
            .EnumerateFiles(
                outputDirectory,
                "Peggy-Diagnostics-*.zip",
                SearchOption.TopDirectoryOnly)
            .Where(path => !existing.Contains(path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return created ??
            throw new InvalidOperationException(
                "The diagnostic collector completed without creating a ZIP.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
