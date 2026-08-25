using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json.Serialization;

namespace Peggy.Core;

public sealed record WorkIqAccountIdentity(
    string Account,
    string? Mail,
    string? UserPrincipalName);

public sealed class WorkIqSetupException : InvalidOperationException
{
    public WorkIqSetupException(string message)
        : base(message)
    {
    }

    public WorkIqSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WorkIqSetupService
{
    public const string EulaUrl =
        "https://github.com/microsoft/work-iq/tree/main/EULA";
    public const string PackageName = "@microsoft/workiq";

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);

    public bool TryGetExecutablePath(out string executablePath)
        => WorkIqClient.TryResolveExecutable(out executablePath);

    public async Task<string> GetVersionAsync(
        CancellationToken ct = default)
    {
        var executable = RequireExecutable();
        var result = await RunProcessAsync(
            executable,
            ["version"],
            TimeSpan.FromSeconds(30),
            ct);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure(
                "WorkIQ version check failed",
                result);
        }
        return result.StandardOutput.Trim();
    }

    public async Task InstallAsync(CancellationToken ct = default)
    {
        var npm = ResolveCommandOnPath(
            OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
        if (npm is null)
        {
            throw new WorkIqSetupException(
                "Node.js 18 or later with npm is required to install " +
                "standalone Microsoft WorkIQ.");
        }

        ProcessResult result;
        if (OperatingSystem.IsWindows() &&
            npm.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command =
                $"/d /s /c \"\"{npm}\" install -g {PackageName}\"";
            result = await RunProcessAsync(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                command,
                SetupTimeout,
                ct);
        }
        else
        {
            result = await RunProcessAsync(
                npm,
                ["install", "-g", PackageName],
                SetupTimeout,
                ct);
        }

        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure(
                "Standalone Microsoft WorkIQ installation failed",
                result);
        }
        if (!TryGetExecutablePath(out _))
        {
            throw new WorkIqSetupException(
                "npm completed, but the standalone WorkIQ executable could " +
                "not be found. Restart Peggy or set PEGGY_WORKIQ_PATH.");
        }
    }

    public async Task<WorkIqAccountIdentity> SignInAsync(
        string expectedAccount,
        CancellationToken ct = default)
    {
        var normalized = NormalizeAccount(expectedAccount);
        var executable = RequireExecutable();

        await RunRequiredCommandAsync(
            executable,
            ["accept-eula"],
            TimeSpan.FromSeconds(30),
            "WorkIQ EULA acceptance failed",
            ct);
        await RunRequiredCommandAsync(
            executable,
            ["auth", "login", "--account", normalized],
            SetupTimeout,
            "Microsoft 365 sign-in failed",
            ct);
        await RunRequiredCommandAsync(
            executable,
            ["auth", "consent", "--account", normalized],
            SetupTimeout,
            "Microsoft 365 permission consent failed",
            ct);

        return await VerifyAccountAsync(normalized, ct);
    }

    public async Task<WorkIqAccountIdentity> VerifyAccountAsync(
        string expectedAccount,
        CancellationToken ct = default)
    {
        var normalized = NormalizeAccount(expectedAccount);
        var executable = RequireExecutable();
        try
        {
            var profile = await new WorkIqClient(
                    executable,
                    normalized)
                .FetchAsync<WorkIqProfile>(
                    "/me?$select=id,mail,userPrincipalName",
                    ct);
            var mail = WorkIqMessageProvider.ResolveAuthenticatedAccount(
                profile.Mail,
                profile.UserPrincipalName,
                normalized);
            return new WorkIqAccountIdentity(
                normalized,
                mail,
                profile.UserPrincipalName);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            InvalidDataException or
            IOException or
            TimeoutException)
        {
            throw new WorkIqSetupException(
                "WorkIQ could not verify the signed-in Microsoft 365 account.",
                ex);
        }
    }

    internal static string NormalizeAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new WorkIqSetupException(
                "Enter the Microsoft 365 email address Peggy should use.");
        }

        try
        {
            return new MailAddress(account.Trim()).Address;
        }
        catch (FormatException ex)
        {
            throw new WorkIqSetupException(
                "Enter a valid Microsoft 365 email address.",
                ex);
        }
    }

    private static async Task RunRequiredCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken ct)
    {
        var result = await RunProcessAsync(
            executable,
            arguments,
            timeout,
            ct);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure(failureMessage, result);
        }
    }

    private static WorkIqSetupException CreateCommandFailure(
        string message,
        ProcessResult result)
    {
        var detail = NormalizeOutput(
            !string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardError
                : result.StandardOutput);
        return new WorkIqSetupException(
            detail.Length == 0
                ? $"{message}. Exit code: {result.ExitCode}."
                : $"{message}: {detail}");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
        => await RunProcessAsync(
            executable,
            arguments: arguments,
            rawArguments: null,
            timeout: timeout,
            ct: ct);

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        string rawArguments,
        TimeSpan timeout,
        CancellationToken ct)
        => await RunProcessAsync(
            executable,
            arguments: null,
            rawArguments: rawArguments,
            timeout: timeout,
            ct: ct);

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string>? arguments,
        string? rawArguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (rawArguments is not null)
        {
            startInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (var argument in arguments ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new WorkIqSetupException(
                $"Could not start '{executable}'.",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw new WorkIqSetupException(
                $"'{Path.GetFileName(executable)}' did not finish within " +
                $"{timeout.TotalMinutes:0.#} minutes.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string RequireExecutable()
        => WorkIqClient.TryResolveExecutable(out var executable)
            ? executable
            : throw new WorkIqSetupException(
                "Standalone Microsoft WorkIQ is not installed. Install it " +
                "with npm or select Install WorkIQ.");

    private static string? ResolveCommandOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(
                    directory.Trim().Trim('"'),
                    name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string NormalizeOutput(string value)
    {
        var normalized = string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        return normalized.Length <= 600
            ? normalized
            : normalized[..600] + "...";
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

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class WorkIqProfile
    {
        [JsonPropertyName("mail")]
        public string? Mail { get; init; }

        [JsonPropertyName("userPrincipalName")]
        public string? UserPrincipalName { get; init; }
    }
}
