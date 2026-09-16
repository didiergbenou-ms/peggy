using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Peggy.Core;

public sealed record WorkIqAccountIdentity(
    string Account,
    string? Mail,
    string? UserPrincipalName)
{
    public string PrincipalId { get; init; } = string.Empty;

    public string EulaAcceptedVersion { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

public class WorkIqSetupException : InvalidOperationException
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

public sealed class WorkIqEulaChangedException : WorkIqSetupException
{
    public WorkIqEulaChangedException(string message)
        : base(message)
    {
    }
}

public interface IWorkIqSetupService
{
    bool TryGetExecutablePath(out string executablePath);
    Task<string> GetVersionAsync(CancellationToken ct = default);
    Task InstallAsync(CancellationToken ct = default);
    Task<WorkIqAccountIdentity> SignInAsync(
        string expectedAccount, string acceptedEulaVersion,
        CancellationToken ct = default);
    Task<WorkIqAccountIdentity> VerifyAccountAsync(
        string expectedAccount, CancellationToken ct = default);
    Task ProbeInboxAccessAsync(
        WorkIqAccountIdentity identity, CancellationToken ct = default);
}

public sealed class WorkIqSetupService : IWorkIqSetupService
{
    public const string EulaUrl =
        "https://github.com/microsoft/work-iq/tree/main/EULA";
    public const string PackageName = "@microsoft/workiq";

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(10);
    private readonly Func<string, string, CancellationToken, Task<JsonElement>> _fetch;

    public WorkIqSetupService()
        : this((account, url, ct) =>
            new WorkIqClient(account: account).FetchAsync<JsonElement>(url, ct))
    {
    }

    internal WorkIqSetupService(
        Func<string, string, CancellationToken, Task<JsonElement>> fetch)
    {
        _fetch = fetch;
    }

    public bool TryGetExecutablePath(out string executablePath)
        => WorkIqClient.TryResolveExecutable(out executablePath);

    public async Task<string> GetVersionAsync(
        CancellationToken ct = default)
    {
        var executable = RequireExecutable();
        return await GetVersionAsync(executable, ct);
    }

    private static async Task<string> GetVersionAsync(
        string executable,
        CancellationToken ct)
    {
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
        var version = result.StandardOutput.Trim();
        if (version.Length == 0)
        {
            throw new WorkIqSetupException("WorkIQ returned an empty version.");
        }
        return version;
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

    public async Task<string> AcceptEulaAsync(
        string? expectedVersion = null,
        CancellationToken ct = default)
    {
        var executable = RequireExecutable();
        using var executableLock = OpenExecutableLock(executable);
        var installedVersion = await GetVersionAsync(executable, ct);
        if (!string.IsNullOrWhiteSpace(expectedVersion) &&
            !string.Equals(
                installedVersion,
                expectedVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new WorkIqEulaChangedException(
                "The installed WorkIQ version changed. Review and accept its " +
                "current EULA before Peggy continues.");
        }

        await RunRequiredCommandAsync(
            executable,
            ["accept-eula"],
            TimeSpan.FromSeconds(30),
            "WorkIQ EULA acceptance failed",
            ct);
        var acceptedVersion = await GetVersionAsync(executable, ct);
        if (!string.Equals(
                installedVersion,
                acceptedVersion,
                StringComparison.Ordinal))
        {
            throw new WorkIqEulaChangedException(
                "The installed WorkIQ version changed during EULA acceptance. " +
                "Review and accept its current EULA before Peggy continues.");
        }
        return installedVersion;
    }

    private static FileStream OpenExecutableLock(string executable)
    {
        try
        {
            return new FileStream(
                executable,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkIqSetupException(
                "Peggy could not lock the installed WorkIQ build while " +
                "verifying its accepted EULA version.",
                ex);
        }
    }

    public async Task<bool> TryRestoreAcceptedEulaAsync(
        string? acceptedVersion,
        Exception failure,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (string.IsNullOrWhiteSpace(acceptedVersion) ||
            !IsEulaAcceptanceRequired(failure))
        {
            return false;
        }

        try
        {
            await AcceptEulaAsync(acceptedVersion, ct);
            return true;
        }
        catch (WorkIqSetupException)
        {
            return false;
        }
    }

    public static bool IsEulaAcceptanceRequired(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        for (Exception? current = failure;
             current is not null;
             current = current.InnerException)
        {
            if (IsEulaAcceptanceRequired(current.Message))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsEulaAcceptanceRequired(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains(
                "accept-eula",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "End User License",
                StringComparison.OrdinalIgnoreCase));

    public async Task<WorkIqAccountIdentity> SignInAsync(
        string expectedAccount,
        CancellationToken ct = default)
        => await SignInCoreAsync(expectedAccount, expectedVersion: null, ct);

    public async Task<WorkIqAccountIdentity> SignInAsync(
        string expectedAccount,
        string acceptedEulaVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(acceptedEulaVersion))
        {
            throw new WorkIqSetupException(
                "Check the installed WorkIQ version and explicitly accept its EULA.");
        }
        return await SignInCoreAsync(expectedAccount, acceptedEulaVersion, ct);
    }

    private async Task<WorkIqAccountIdentity> SignInCoreAsync(
        string expectedAccount,
        string? expectedVersion,
        CancellationToken ct)
    {
        var normalized = NormalizeAccount(expectedAccount);
        var executable = RequireExecutable();

        var acceptedVersion = await AcceptEulaAsync(expectedVersion, ct);
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

        var identity = await VerifyAccountAsync(normalized, ct);
        await AcceptEulaAsync(acceptedVersion, ct);
        return identity with
        {
            EulaAcceptedVersion = acceptedVersion
        };
    }

    public async Task<WorkIqAccountIdentity> VerifyAccountAsync(
        string expectedAccount,
        CancellationToken ct = default)
    {
        var normalized = NormalizeAccount(expectedAccount);
        try
        {
            var payload = await _fetch(
                normalized, "/me?$select=id,mail,userPrincipalName,displayName", ct);
            var profile = payload.Deserialize<WorkIqProfile>()
                ?? throw new InvalidDataException("WorkIQ returned no profile.");
            var mail = WorkIqMessageProvider.ResolveAuthenticatedAccount(
                profile.Mail,
                profile.UserPrincipalName,
                normalized);
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                throw new InvalidDataException(
                    "WorkIQ returned a profile without an immutable ID.");
            }
            return new WorkIqAccountIdentity(
                normalized,
                mail,
                profile.UserPrincipalName)
            {
                PrincipalId = profile.Id.Trim(),
                DisplayName = profile.DisplayName?.Trim() ?? string.Empty
            };
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
            InvalidDataException or
            JsonException or
            IOException or
            TimeoutException)
        {
            throw new WorkIqSetupException(
                "WorkIQ could not verify the signed-in Microsoft 365 account.",
                ex);
        }
    }

    public async Task ProbeInboxAccessAsync(
        WorkIqAccountIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.PrincipalId))
        {
            throw new WorkIqSetupException(
                "Verify the Microsoft 365 account before checking mail access.");
        }
        try
        {
            await VerifyPinnedIdentityAsync(identity, ct);
            // Address the verified principal, never a mutable global /me mailbox.
            var payload = await _fetch(
                NormalizeAccount(identity.Account),
                $"/users/{Uri.EscapeDataString(identity.PrincipalId)}/" +
                "mailFolders/inbox/messages?$select=id&$top=1",
                ct);
            if (!payload.TryGetProperty("value", out var messages) ||
                messages.ValueKind != JsonValueKind.Array ||
                messages.EnumerateArray().Any(message =>
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString())))
            {
                throw new InvalidDataException(
                    "WorkIQ returned an invalid Inbox access response.");
            }
            await VerifyPinnedIdentityAsync(identity, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            InvalidDataException or IOException or TimeoutException or JsonException)
        {
            throw new WorkIqSetupException(
                "Mail access is not ready. Check Microsoft 365 permissions " +
                "and mailbox availability, then retry.", ex);
        }
    }

    private async Task VerifyPinnedIdentityAsync(
        WorkIqAccountIdentity identity, CancellationToken ct)
    {
        var actual = await VerifyAccountAsync(identity.Account, ct);
        if (!RepresentsSameAccount(identity.Account, identity.Mail,
                identity.UserPrincipalName, identity.PrincipalId, actual))
        {
            throw new WorkIqSetupException(
                "WorkIQ changed accounts during the mail access check. Sign in again.");
        }
    }

    public static bool RepresentsSameAccount(
        string? pinnedAccount,
        string? pinnedMailbox,
        WorkIqAccountIdentity identity)
        => RepresentsSameAccount(
            pinnedAccount,
            pinnedMailbox,
            pinnedUserPrincipalName: null,
            pinnedPrincipalId: null,
            identity);

    public static bool RepresentsSameAccount(
        string? pinnedAccount,
        string? pinnedMailbox,
        string? pinnedUserPrincipalName,
        WorkIqAccountIdentity identity)
        => RepresentsSameAccount(
            pinnedAccount,
            pinnedMailbox,
            pinnedUserPrincipalName,
            pinnedPrincipalId: null,
            identity);

    public static bool RepresentsSameAccount(
        string? pinnedAccount,
        string? pinnedMailbox,
        string? pinnedUserPrincipalName,
        string? pinnedPrincipalId,
        WorkIqAccountIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.IsNullOrWhiteSpace(pinnedPrincipalId) &&
            !string.IsNullOrWhiteSpace(identity.PrincipalId))
        {
            return string.Equals(
                pinnedPrincipalId.Trim(),
                identity.PrincipalId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }
        if (string.IsNullOrWhiteSpace(pinnedPrincipalId) &&
            !string.IsNullOrWhiteSpace(identity.PrincipalId))
        {
            return false;
        }

        var pinnedAliases = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        AddAccountAlias(pinnedAliases, pinnedAccount);
        AddAccountAlias(pinnedAliases, pinnedMailbox);
        AddAccountAlias(pinnedAliases, pinnedUserPrincipalName);
        if (pinnedAliases.Count == 0)
        {
            return false;
        }

        return new[]
        {
            identity.Account,
            identity.Mail,
            identity.UserPrincipalName
        }.Any(alias =>
            !string.IsNullOrWhiteSpace(alias) &&
            pinnedAliases.Contains(alias.Trim()));
    }

    private static void AddAccountAlias(
        ISet<string> aliases,
        string? account)
    {
        if (!string.IsNullOrWhiteSpace(account))
        {
            aliases.Add(account.Trim());
        }
    }

    public static bool IsValidAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return false;
        }
        var trimmed = account.Trim();
        return !trimmed.Any(char.IsWhiteSpace) &&
            MailAddress.TryCreate(trimmed, out var parsed) &&
            string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase) &&
            parsed.Host.Contains('.') && !parsed.Host.StartsWith('.') &&
            !parsed.Host.EndsWith('.');
    }

    internal static string NormalizeAccount(string account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new WorkIqSetupException(
                "Enter the Microsoft 365 email address Peggy should use.");
        }

        if (!IsValidAccount(account))
        {
            throw new WorkIqSetupException(
                "Enter a valid Microsoft 365 email address.");
        }
        return new MailAddress(account.Trim()).Address;
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

        if (OperatingSystem.IsWindows() && name == "npm.cmd")
        {
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs", name);
            if (File.Exists(installed))
            {
                return installed;
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
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("mail")]
        public string? Mail { get; init; }

        [JsonPropertyName("userPrincipalName")]
        public string? UserPrincipalName { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }
    }
}
