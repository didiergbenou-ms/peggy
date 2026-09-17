using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

internal enum SetupCheckState
{
    Checking,
    Ready,
    ActionRequired,
    Failed,
    Skipped
}

internal sealed record SetupCheck(
    SetupCheckState State, string Summary, string Details = "", string Version = "")
{
    public bool IsReady => State == SetupCheckState.Ready;

    public string StatusText => $"{State switch
    {
        SetupCheckState.Checking => "Checking",
        SetupCheckState.Ready => "Ready",
        SetupCheckState.ActionRequired => "Action required",
        SetupCheckState.Failed => "Failed",
        _ => "Optional / skipped"
    }} — {Summary}";
}

internal sealed record SetupLocalReadiness(
    SetupCheck Node, SetupCheck Npm, SetupCheck PowerShell, SetupCheck Copilot,
    SetupCheck WorkIq)
{
    public bool IsReady =>
        Node.IsReady && Npm.IsReady && PowerShell.IsReady &&
        Copilot.IsReady && WorkIq.IsReady;

    public IEnumerable<SetupCheck> Checks => [Node, Npm, PowerShell, Copilot, WorkIq];
}

internal enum SetupLocalCheck { Node, Npm, PowerShell, Copilot, WorkIq }

internal sealed record SetupLocalCheckUpdate(SetupLocalCheck Check, SetupCheck Result);

internal interface ISetupReadinessService
{
    Task<SetupLocalReadiness> CheckLocalAsync(
        CancellationToken ct, IProgress<SetupLocalCheckUpdate>? progress = null);
    Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct);
    Task SignInCopilotAsync(
        CancellationToken ct, IProgress<CopilotDeviceCodeChallenge>? progress = null);
}

internal sealed class SetupReadinessService : ISetupReadinessService
{
    private readonly IWorkIqSetupService _workIq;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> _run;
    private readonly Func<string, string?> _resolve;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CopilotModelOption>>> _models;

    public SetupReadinessService(IWorkIqSetupService workIq)
        : this(workIq, ReadCommandAsync, ResolveCommand,
            ct => CopilotRunner.ListAvailableModelsAsync(CopilotRunner.ResolveCopilot(), ct))
    {
    }

    internal SetupReadinessService(
        IWorkIqSetupService workIq,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> run,
        Func<string, string?> resolve,
        Func<CancellationToken, Task<IReadOnlyList<CopilotModelOption>>> models)
    {
        _workIq = workIq;
        _run = run;
        _resolve = resolve;
        _models = models;
    }

    public async Task<SetupLocalReadiness> CheckLocalAsync(
        CancellationToken ct, IProgress<SetupLocalCheckUpdate>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        // Version commands are local only; network access is a separate user action.
        var node = ReportAsync(SetupLocalCheck.Node,
            CheckCommandAsync("node.exe", ["--version"], new Version(18, 0, 0), ct));
        var npm = ReportAsync(SetupLocalCheck.Npm,
            CheckCommandAsync("npm.cmd", ["--version"], new Version(1, 0, 0), ct));
        var powerShell = ReportAsync(SetupLocalCheck.PowerShell, CheckCommandAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "$PSVersionTable.PSVersion.ToString()"], new Version(7, 0, 0), ct));
        var copilot = ReportAsync(SetupLocalCheck.Copilot,
            CheckCommandAsync("copilot.exe", ["--version"], new Version(1, 0, 81), ct));
        var workIq = ReportAsync(SetupLocalCheck.WorkIq, CheckWorkIqAsync(ct));
        await Task.WhenAll(node, npm, powerShell, copilot, workIq);
        ct.ThrowIfCancellationRequested();
        return new(await node, await npm, await powerShell, await copilot, await workIq);

        async Task<SetupCheck> ReportAsync(SetupLocalCheck check, Task<SetupCheck> pending)
        {
            var result = await pending;
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(check, result));
            return result;
        }
    }

    private async Task<SetupCheck> CheckCommandAsync(
        string name, IReadOnlyList<string> args, Version minimum, CancellationToken ct)
    {
        try
        {
            var path = _resolve(name);
            if (path is null)
            {
                return new(SetupCheckState.ActionRequired,
                    $"Not found. Run Peggy setup.exe to install prerequisites ({minimum}+).");
            }
            var output = await _run(path, args, ct);
            var match = Regex.Match(output, @"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
            {
                return new(SetupCheckState.Failed, "Could not read the installed version.",
                    $"{path}\n{output}");
            }
            return version < minimum
                ? new(SetupCheckState.ActionRequired,
                    $"{version} is too old; {minimum}+ required. Run Peggy setup.exe.",
                    path, version.ToString())
                : new(SetupCheckState.Ready, $"Version {version}", path, version.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return new(SetupCheckState.Failed,
                "Could not check this requirement. Repair with Peggy setup.exe, then retry.",
                ex.ToString());
        }
    }

    private async Task<SetupCheck> CheckWorkIqAsync(CancellationToken ct)
    {
        try
        {
            if (!_workIq.TryGetExecutablePath(out var path))
            {
                return new(SetupCheckState.ActionRequired,
                    "Not installed. Use Install WorkIQ after Node.js and npm are ready.");
            }
            var version = await _workIq.GetVersionAsync(ct);
            return new(SetupCheckState.Ready, $"Standalone WorkIQ {version}", path, version);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return new(SetupCheckState.Failed, "Standalone WorkIQ version check failed.", ex.ToString());
        }
    }

    public async Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var models = await _models(timeout.Token);
            return models.Any(model => model.IsAvailable)
                ? new(SetupCheckState.Ready, "GitHub Copilot model catalog is available.")
                : new(SetupCheckState.ActionRequired,
                    "No available models. Sign in with a GitHub account that has Copilot access.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new(SetupCheckState.ActionRequired,
                "The model catalog check timed out. Check your network, then retry.");
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return new(SetupCheckState.ActionRequired,
                "Could not access the model catalog. Sign in or check your Copilot subscription and network, then retry.");
        }
    }

    public Task SignInCopilotAsync(
        CancellationToken ct, IProgress<CopilotDeviceCodeChallenge>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var executable = _resolve("copilot.exe")
            ?? throw new InvalidOperationException("Install GitHub Copilot CLI using Peggy setup.exe first.");
        return new CopilotDeviceCodeSignIn().SignInAsync(executable, ct, progress);
    }

    private static string? ResolveCommand(string name)
    {
        if (name == "copilot.exe")
        {
            return CopilotRunner.ResolveCopilot();
        }
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"')).ToList();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        paths.Add(Path.Combine(programFiles, "nodejs"));
        paths.Add(Path.Combine(programFiles, "PowerShell", "7"));
        foreach (var directory in paths)
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static async Task<string> ReadCommandAsync(
        string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count != 1 || args[0] != "--version" ||
                executable.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            {
                throw new InvalidOperationException("Unsafe npm version command.");
            }
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"\"{executable}\" --version\"";
        }
        else
        {
            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Version check exited with code {process.ExitCode}: {stderr}");
            }
            return stdout.Trim();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The {Path.GetFileName(executable)} version check timed out.", ex);
        }
        finally { TryKill(process); }
    }

    internal static bool IsExpectedFailure(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or TimeoutException or HttpRequestException or
            JsonException or SecurityException or NotSupportedException ||
            (exception is InvalidOperationException && exception is not ObjectDisposedException) ||
            (exception is ArgumentException &&
                exception is not ArgumentNullException and not ArgumentOutOfRangeException) ||
            // The SDK's remote-error type is internal; match that exact type, not all SDK exceptions.
            (exception.GetType().FullName == "GitHub.Copilot.RemoteRpcException" &&
                exception.GetType().Assembly == typeof(GitHub.Copilot.CopilotClient).Assembly);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
}
