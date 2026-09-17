using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Peggy.Tray;

internal sealed record CopilotDeviceCodeChallenge(string UserCode, Uri VerificationUri);

internal interface ICopilotDeviceCodeProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    Task<int> WaitForExitAsync(CancellationToken ct);
    void Kill();
    void CloseStreams();
}

internal sealed class CopilotDeviceCodeSignIn
{
    private const string UpdateGuidance =
        "This Copilot CLI does not support device-code sign-in. Update GitHub Copilot CLI using Peggy setup.exe, then retry.";
    private readonly Func<string, IReadOnlyList<string>, ICopilotDeviceCodeProcess> _start;
    private readonly TimeSpan _signInTimeout;
    private readonly TimeSpan _helpTimeout;

    public CopilotDeviceCodeSignIn()
        : this((path, args) => new CopilotDeviceCodeProcess(path, args),
            TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15)) { }

    internal CopilotDeviceCodeSignIn(
        Func<string, IReadOnlyList<string>, ICopilotDeviceCodeProcess> start,
        TimeSpan signInTimeout, TimeSpan? helpTimeout = null)
    {
        _start = start;
        _signInTimeout = signInTimeout;
        _helpTimeout = helpTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task SignInAsync(string executable, CancellationToken ct,
        IProgress<CopilotDeviceCodeChallenge>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        using var help = new CopilotDeviceCodeOutput(null, ct);
        var helpExit = await RunAsync(executable, ["login", "--help"], help, _helpTimeout, ct);
        ct.ThrowIfCancellationRequested();
        if (helpExit != 0 || !help.SupportsDeviceCode)
            throw new InvalidOperationException(UpdateGuidance);

        using var output = new CopilotDeviceCodeOutput(progress, ct);
        var exit = await RunAsync(executable, ["login", "--device-code"], output, _signInTimeout, ct);
        ct.ThrowIfCancellationRequested();
        if (output.Failure == CopilotDeviceCodeFailure.Unsupported)
            throw new InvalidOperationException(UpdateGuidance);
        if (exit != 0 || output.Failure != CopilotDeviceCodeFailure.None)
        {
            var reason = output.Failure switch
            {
                CopilotDeviceCodeFailure.Expired => "The device code expired. Retry to get a new code.",
                CopilotDeviceCodeFailure.Denied => "Authorization was denied. Retry when ready.",
                CopilotDeviceCodeFailure.UnsafePrompt =>
                    "The device prompt was not recognized. This setup supports GitHub.com only.",
                CopilotDeviceCodeFailure.Plaintext =>
                    "Credential storage needs attention. Configure secure Copilot CLI credential storage, then retry.",
                _ => "Authorization did not complete. Check your connection and retry."
            };
            var outcome = exit.HasValue ? $"exited with code {exit}" : "stopped";
            throw new InvalidOperationException($"GitHub Copilot sign-in {outcome}. {reason}");
        }
        // An empty/known existing-auth exit is not authentication proof. The caller verifies
        // the model catalog separately, including when an inherited CLI token bypasses login.
        if (!output.HasChallenge && output.HasVisibleOutput && !output.ExistingAuthentication)
            throw new InvalidOperationException(
                "GitHub Copilot returned no recognized device prompt. Verify existing Copilot access or update the CLI and retry. This setup supports GitHub.com only.");
    }

    private async Task<int?> RunAsync(string executable, IReadOnlyList<string> arguments,
        CopilotDeviceCodeOutput output, TimeSpan duration, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        ICopilotDeviceCodeProcess? process = null;
        Task? pending = null;
        Task stdout = Task.CompletedTask;
        Task stderr = Task.CompletedTask;
        Task exitWait = Task.CompletedTask;
        var rejectedPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptingOutput = true;
        var gate = new object();
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            process = _start(executable, arguments);
            stdout = PumpAsync(process.StandardOutput, false);
            stderr = PumpAsync(process.StandardError, true);
            var exit = process.WaitForExitAsync(timeout.Token);
            exitWait = exit;
            pending = Task.WhenAll(stdout, stderr, exit);
            var completed = await Task.WhenAny(pending, rejectedPrompt.Task)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (completed == rejectedPrompt.Task && !pending.IsCompleted)
                return null;
            await pending.ConfigureAwait(false);
            return await exit.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub Copilot sign-in timed out. Retry to get a new device code.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or System.Security.SecurityException or AggregateException)
        {
            // Do not attach the original exception: command errors can contain credentials.
            throw new InvalidOperationException("Could not complete GitHub Copilot device-code sign-in. Check the CLI installation and retry.");
        }
        finally
        {
            lock (gate) acceptingOutput = false;
            var cleanupFailed = false;
            var timedOut = timeout.IsCancellationRequested;
            try { timeout.Cancel(); }
            catch (Exception) { cleanupFailed = true; }
            if (process is not null)
            {
                Task? drained = null;
                try
                {
                    try { process.Kill(); }
                    catch (Exception) { cleanupFailed = true; }
                    finally
                    {
                        try { process.CloseStreams(); }
                        catch (Exception) { cleanupFailed = true; }
                        finally
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            try
                            {
                                drained = Task.WhenAll(stdout, stderr, exitWait,
                                    process.WaitForExitAsync(cleanup.Token));
                                await drained.WaitAsync(cleanup.Token).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // A cancelled original read/wait is expected during cleanup.
                                if (drained is null || !drained.IsCompleted) cleanupFailed = true;
                            }
                        }
                    }
                }
                finally
                {
                    try { process.Dispose(); }
                    catch (Exception) { cleanupFailed = true; }
                    finally
                    {
                        Observe(Task.WhenAll(stdout, stderr, exitWait));
                        if (pending is not null) Observe(pending);
                        if (drained is not null) Observe(drained);
                    }
                }
            }
            if (cleanupFailed)
            {
                ct.ThrowIfCancellationRequested();
                if (timedOut)
                    throw new TimeoutException("GitHub Copilot sign-in stopped, but process cleanup did not finish normally. Retry after checking the CLI.");
                throw new InvalidOperationException("GitHub Copilot sign-in could not safely finish process cleanup. Retry after checking the CLI.");
            }
        }

        async Task PumpAsync(TextReader reader, bool error)
        {
            var buffer = new char[512];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (!acceptingOutput || timeout.IsCancellationRequested) return;
                        output.Append(error, buffer.AsSpan(0, count), count == 0);
                        if (arguments[1] == "--device-code" && output.Failure != CopilotDeviceCodeFailure.None)
                            rejectedPrompt.TrySetResult();
                    }
                    if (count == 0) return;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                rejectedPrompt.TrySetResult();
                throw;
            }
            finally { Array.Clear(buffer); }
        }

        static void Observe(Task task)
            => _ = task.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

internal enum CopilotDeviceCodeFailure { None, Expired, Denied, Unsupported, UnsafePrompt, Plaintext }

// Each stream has its own bounded terminal decoder so interleaved stderr cannot complete
// a partial stdout URL, and ANSI/OSC fragments never become displayed text.
internal sealed class CopilotDeviceCodeOutput : IDisposable
{
    internal const int MaximumLineLength = 2048;
    private static readonly Uri VerificationUri = new("https://github.com/login/device");
    private static readonly Regex Modern = new(
        @"^To authenticate, visit (?<uri>\S+) and enter code (?<code>[A-Z0-9]{4}-[A-Z0-9]{4})(?=\s|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Legacy = new(
        @"^Enter one-time code: (?<code>[A-Z0-9]{4}-[A-Z0-9]{4}) at (?<uri>\S+)(?=\s|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DeviceCodeOption = new(
        @"^\s*(?:-[a-zA-Z],\s*)?--device-code(?:\s|$)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly TerminalLine _stdout = new();
    private readonly TerminalLine _stderr = new();
    private readonly IProgress<CopilotDeviceCodeChallenge>? _progress;
    private readonly CancellationToken _ct;
    private string? _code;
    private bool _disposed;

    public CopilotDeviceCodeOutput(IProgress<CopilotDeviceCodeChallenge>? progress, CancellationToken ct)
    {
        _progress = progress;
        _ct = ct;
    }

    public bool SupportsDeviceCode { get; private set; }
    public bool HasChallenge => _code is not null;
    public bool HasVisibleOutput { get; private set; }
    public bool ExistingAuthentication { get; private set; }
    public CopilotDeviceCodeFailure Failure { get; private set; }

    public void Append(bool error, ReadOnlySpan<char> chunk, bool endOfStream = false)
    {
        if (_disposed || _ct.IsCancellationRequested) return;
        var terminal = error ? _stderr : _stdout;
        foreach (var character in chunk)
        {
            if (!char.IsWhiteSpace(character)) HasVisibleOutput = true;
            if (terminal.Consume(character, out var complete))
                Inspect(complete!, true);
        }
        // Neither the modern code nor a legacy trailing URL is complete at an arbitrary
        // chunk boundary: a suffix can still arrive before whitespace or EOF.
        if (!terminal.Overflowed)
            Inspect(terminal.Text.ToString(), endOfStream);
        if (endOfStream) terminal.Clear();
    }

    private void Inspect(string line, bool complete)
    {
        var trailingDelimiter = line.Length > 0 && char.IsWhiteSpace(line[^1]);
        line = line.Trim();
        if (line.Length == 0) return;
        HasVisibleOutput = true;
        var option = DeviceCodeOption.Match(line);
        if (option.Success && (complete || trailingDelimiter || option.Length < line.Length))
            SupportsDeviceCode = true;
        if (line.Contains("expired_token", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("code expired", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("code has expired", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("device flow expired", StringComparison.OrdinalIgnoreCase))
            Failure = CopilotDeviceCodeFailure.Expired;
        if (line.Contains("access_denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("authorization denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("authorization was denied", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("authorization request denied", StringComparison.OrdinalIgnoreCase))
            Failure = CopilotDeviceCodeFailure.Denied;
        if (line.Contains("--device-code", StringComparison.Ordinal) &&
            (line.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)))
            Failure = CopilotDeviceCodeFailure.Unsupported;
        if (line.Contains("plaintext", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("plain text", StringComparison.OrdinalIgnoreCase))
            Failure = CopilotDeviceCodeFailure.Plaintext;
        if (line.StartsWith("Already logged in", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Already authenticated", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Using GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Using GH_TOKEN", StringComparison.OrdinalIgnoreCase))
            ExistingAuthentication = true;

        var match = Modern.Match(line);
        if (match.Success)
        {
            if (!complete && !trailingDelimiter &&
                match.Groups["code"].Index + match.Groups["code"].Length == line.Length)
                return;
        }
        else
        {
            match = Legacy.Match(line);
            if (match.Success && !complete && !trailingDelimiter &&
                match.Groups["uri"].Index + match.Groups["uri"].Length == line.Length)
                return;
        }
        if (!match.Success)
        {
            if (complete && (line.StartsWith("To authenticate, visit ", StringComparison.Ordinal) ||
                line.StartsWith("Enter one-time code:", StringComparison.Ordinal)))
                Failure = CopilotDeviceCodeFailure.UnsafePrompt;
            return;
        }
        if (!string.Equals(match.Groups["uri"].Value, VerificationUri.AbsoluteUri, StringComparison.Ordinal))
        {
            Failure = CopilotDeviceCodeFailure.UnsafePrompt;
            return;
        }
        var code = match.Groups["code"].Value;
        if (_code == code || Failure != CopilotDeviceCodeFailure.None || _ct.IsCancellationRequested) return;
        if (_code is not null)
        {
            Failure = CopilotDeviceCodeFailure.UnsafePrompt;
            return;
        }
        _code = code;
        _progress?.Report(new(code, VerificationUri));
    }

    public void Dispose()
    {
        _disposed = true;
        _code = null;
        _stdout.Clear();
        _stderr.Clear();
    }

    private sealed class TerminalLine
    {
        public StringBuilder Text { get; } = new();
        public bool Overflowed { get; private set; }
        private int _escape;

        public bool Consume(char value, out string? complete)
        {
            complete = null;
            if (_escape != 0)
            {
                _escape = _escape switch
                {
                    1 => value == '[' ? 2 : value == ']' ? 3 : 0,
                    2 => value is >= '@' and <= '~' ? 0 : 2,
                    3 => value == '\a' ? 0 : value == '\x1b' ? 4 : 3,
                    _ => value == '\\' ? 0 : 3
                };
                return false;
            }
            if (value == '\x1b') { _escape = 1; return false; }
            if (value is '\r' or '\n')
            {
                complete = Overflowed ? "" : Text.ToString();
                Clear();
                return true;
            }
            if ((value >= ' ' || value == '\t') && !Overflowed)
            {
                if (Text.Length == MaximumLineLength)
                {
                    Text.Clear();
                    Overflowed = true;
                }
                else Text.Append(value);
            }
            return false;
        }

        public void Clear()
        {
            Text.Clear();
            Overflowed = false;
            _escape = 0;
        }
    }
}
