using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Peggy.PrerequisiteCheck;

internal enum PrerequisiteState { Checking, Ready, Missing, Error }

internal sealed record PrerequisiteResult(
    string Id, string Name, PrerequisiteState State, string Detail,
    string? Executable = null, Version? Version = null);

internal static class SetupMode
{
    // WiX 6.0.2 BootstrapperApplicationTypes.h: Unknown=0, Embedded=1,
    // None=2, Passive=3, Full=4. MSI UILevel uses different values.
    internal static bool IsInteractive(string[] args) =>
        args is ["--bundle", "--display", "4"];

    internal static bool IsCheckCommand(string[] args) =>
        args is ["--msi"] or ["--validate"] or ["--bundle"] ||
        args is ["--bundle", "--display", var display] &&
        int.TryParse(display, out var level) && level is >= 0 and <= 4;
}

internal static partial class PrerequisiteEngine
{
    internal static readonly string[] Ids = ["powershell", "node", "npm", "copilot"];
    internal static readonly string[] Names =
        ["PowerShell 7+", "Node.js 18+", "npm", "GitHub Copilot CLI 1.0.81+"];

    internal static async Task<IReadOnlyList<PrerequisiteResult>> CheckAllAsync(
        CancellationToken cancellationToken, Action<PrerequisiteResult>? report = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var copilot = CopilotPrerequisite.ResolveCandidates(local);
        var definitions = new[]
        {
            (Ids[0], Names[0], new[] { "pwsh.exe", "pwsh" },
                new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "$PSVersionTable.PSVersion.ToString()" }, new Version(7, 0)),
            (Ids[1], Names[1], new[] { "node.exe", "node" },
                new[] { "--version" }, new Version(18, 0)),
            (Ids[2], Names[2], new[] { "npm.cmd", "npm.exe", "npm" },
                new[] { "--version" }, new Version(0, 0)),
            (Ids[3], Names[3], copilot.Candidates.ToArray(),
                new[] { "--version" }, new Version(1, 0, 81))
        };
        var results = new List<PrerequisiteResult>();
        foreach (var (id, name, candidates, arguments, minimum) in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(new(id, name, PrerequisiteState.Checking, "Checking version…"));
            var result = id == "copilot" && copilot.Error is not null
                ? new PrerequisiteResult(id, name, PrerequisiteState.Error, copilot.Error)
                : await CheckAsync(id, name, candidates, arguments, minimum, cancellationToken);
            results.Add(result);
            report?.Invoke(result);
        }
        return results;
    }

    internal static async Task<PrerequisiteResult> CheckAsync(
        string id, string name, IEnumerable<string> candidates, string[] arguments,
        Version minimum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ResolveCommand(candidates);
        if (executable is null)
        {
            return new(id, name, PrerequisiteState.Missing, "Not found on this user's PATH.");
        }
        try
        {
            var output = await RunCheckAsync(executable, arguments, cancellationToken);
            return EvaluateVersion(id, name, executable, output, minimum);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(id, name, PrerequisiteState.Error,
                $"{executable}: {ex.Message}", executable);
        }
    }

    internal static PrerequisiteResult EvaluateVersion(
        string id, string name, string executable, string output, Version minimum)
    {
        var match = VersionRegex().Match(output);
        if (!match.Success || !Version.TryParse(match.Value, out var version))
        {
            return new(id, name, PrerequisiteState.Error,
                $"Could not determine the version reported by {executable}.", executable);
        }
        return new(id, name,
            version >= minimum ? PrerequisiteState.Ready : PrerequisiteState.Missing,
            $"Found {version} at {executable}" +
            (version < minimum ? $"; version {minimum}+ is required." : "."),
            executable, version);
    }

    private static async Task<string> RunCheckAsync(
        string executable, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CheckStartInfo(executable, arguments) };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("The command did not start.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + Environment.NewLine + await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Version check exited with code {process.ExitCode}.");
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Version check timed out after 15 seconds.");
        }
    }

    internal static ProcessStartInfo CheckStartInfo(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm.cmd cannot be started directly with redirected output.
            // Only the fixed version-query arguments are allowed through cmd.exe.
            if (arguments is not ["--version"])
            {
                throw new ArgumentException("Only --version is supported for command shims.");
            }
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.Arguments = $"/d /s /c \"\"{executable}\" --version\"";
        }
        else
        {
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static string? ResolveCommand(IEnumerable<string> candidates)
    {
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate))
            {
                if (File.Exists(candidate)) return candidate;
                continue;
            }
            foreach (var entry in entries)
            {
                var path = Path.Combine(entry.Trim('"'), candidate);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    internal static void RefreshEnvironment()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Environment.SetEnvironmentVariable("PATH", MergePaths(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"),
            Path.Combine(programs, "PowerShell", "7"),
            Path.Combine(programs, "nodejs"), Path.Combine(roaming, "npm"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
            Path.Combine(local, "Microsoft", "WindowsApps")));
    }

    internal static string MergePaths(params string?[] paths) => string.Join(
        Path.PathSeparator, paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => Environment.ExpandEnvironmentVariables(path!).Split(
                Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<!\d)\d+(?:\.\d+){1,3}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
