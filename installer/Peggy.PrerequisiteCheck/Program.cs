using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Peggy.PrerequisiteCheck;

internal static partial class Program
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private static int Main()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            return Fail("Peggy requires 64-bit Windows.");
        }

        var failures = new List<string>();
        CheckMinimumVersion(
            failures,
            ["pwsh.exe", "pwsh"],
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$PSVersionTable.PSVersion.ToString()"
            ],
            new Version(7, 0),
            "PowerShell 7");
        CheckMinimumVersion(
            failures,
            ["node.exe", "node"],
            ["--version"],
            new Version(18, 0),
            "Node.js 18");
        CheckAvailable(failures, ["npm.cmd", "npm.exe", "npm"], "npm");
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        CheckMinimumVersion(
            failures,
            [
                "copilot.exe",
                "copilot",
                Path.Combine(
                    localAppData,
                    "Microsoft",
                    "WinGet",
                    "Links",
                    "copilot.exe"),
                Path.Combine(
                    localAppData,
                    "Microsoft",
                    "WindowsApps",
                    "copilot.exe")
            ],
            ["--version"],
            new Version(1, 0, 81),
            "GitHub Copilot CLI 1.0.81");

        if (failures.Count == 0)
        {
            Console.WriteLine("Peggy prerequisite check passed.");
            return 0;
        }

        Console.Error.WriteLine("Peggy cannot be installed:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Install or update these prerequisites, then run the installer again.");
        Console.Error.WriteLine(
            "Alternatively, download install.ps1 and run it with " +
            "-InstallPrerequisites to use WinGet explicitly.");
        Console.Error.WriteLine(
            "WorkIQ is installed only with your approval during Peggy first-run setup.");
        return 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void CheckAvailable(
        ICollection<string> failures,
        string[] candidates,
        string displayName)
    {
        if (ResolveCommand(candidates) is null)
        {
            failures.Add($"{displayName} is not available on PATH.");
        }
    }

    private static void CheckMinimumVersion(
        ICollection<string> failures,
        string[] candidates,
        string[] arguments,
        Version minimum,
        string displayName)
    {
        var executable = ResolveCommand(candidates);
        if (executable is null)
        {
            failures.Add($"{displayName} or later is not available on PATH.");
            return;
        }

        try
        {
            var output = Run(executable, arguments);
            var match = VersionRegex().Match(output);
            if (!match.Success || !Version.TryParse(match.Value, out var version))
            {
                failures.Add($"{displayName} version could not be determined.");
            }
            else if (version < minimum)
            {
                failures.Add(
                    $"{displayName} or later is required; found {version}.");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{displayName} check failed: {ex.Message}");
        }
    }

    private static string Run(string executable, IReadOnlyCollection<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("The command did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The command timed out.");
        }

        Task.WaitAll(standardOutput, standardError);
        var output = string.Join(
            Environment.NewLine,
            standardOutput.Result,
            standardError.Result);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The command exited with code {process.ExitCode}.");
        }
        return output;
    }

    private static string? ResolveCommand(IEnumerable<string> candidates)
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            foreach (var pathEntry in pathEntries)
            {
                var commandPath = Path.Combine(pathEntry, candidate);
                if (File.Exists(commandPath))
                {
                    return commandPath;
                }
            }
        }
        return null;
    }

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
