using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class DeviceCodeSignInSmokeTests
{
    private const string Prompt =
        "To authenticate, visit https://github.com/login/device and enter code ABCD-1234";
    private const string Secret = "ghp_NEVER_SHOW_THIS_SECRET";

    public static async Task RunAsync()
    {
        TestStartInfo();
        TestParser();
        await TestCommandsAndCompletionAsync();
        await TestFailuresAsync();
        await TestCancellationAndTimeoutAsync();
        await TestCleanupFailuresAsync();
        await TestNativeProcessLifetimeAsync();
    }

    private static void TestStartInfo()
    {
        var info = CopilotDeviceCodeProcess.CreateStartInfo(@"C:\Test Path\copilot.exe",
            ["login", "--device-code"]);
        TestAssert.True(info.FileName == @"C:\Test Path\copilot.exe" &&
            info.ArgumentList.SequenceEqual(new[] { "login", "--device-code" }) &&
            !info.UseShellExecute && info.CreateNoWindow &&
            info.RedirectStandardOutput && info.RedirectStandardError && info.RedirectStandardInput,
            "Device login must use literal official arguments and redirected non-console streams.");
        var inherited = new ProcessStartInfo().Environment;
        TestAssert.True(info.Environment.Count == inherited.Count &&
            inherited.All(entry => info.Environment.TryGetValue(entry.Key, out var value) && value == entry.Value),
            "Device login modified inherited CLI credentials or environment.");
        TestAssert.True(CopilotDeviceCodeProcess.BuildCommandLine(info).ToString() ==
            "\"C:\\Test Path\\copilot.exe\" \"login\" \"--device-code\"",
            "Native job startup did not preserve the exact literal CLI arguments.");
    }

    private static void TestParser()
    {
        var samples = new[]
        {
            Prompt + "\nWaiting for authorization...",
            "Waiting for authorization...\nEnter one-time code: ABCD-1234 at https://github.com/login/device\nPress any key to copy to clipboard and open browser...",
            "\x1b[32m" + Prompt + "\x1b[0m\rWaiting for authorization...",
            Prompt,
            "Enter one-time code: ABCD-1234 at https://github.com/login/device",
            "\x1b]0;ignored " + Secret + "\x1b\\" + Prompt
        };
        foreach (var sample in samples)
        {
            foreach (var errorStream in new[] { false, true })
            {
                for (var split = 0; split <= sample.Length; split++)
                {
                    var progress = new Recorder();
                    using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
                    parser.Append(errorStream, sample.AsSpan(0, split));
                    parser.Append(errorStream, sample.AsSpan(split), true);
                    TestAssert.True(progress.Values.Count == 1 &&
                        progress.Values[0].UserCode == "ABCD-1234" &&
                        progress.Values[0].VerificationUri.AbsoluteUri == "https://github.com/login/device" &&
                        parser.Failure == CopilotDeviceCodeFailure.None,
                        "An official fragmented, ANSI, CR, or unterminated device prompt was not parsed exactly once.");
                }
            }
        }

        foreach (var uri in new[]
        {
            "http://github.com/login/device", "https://github.com.evil.example/login/device",
            "https://evil.example/login/device", "https://github.com@evil.example/login/device",
            "https://person:password@github.com/login/device", "https://github.com/login/device?redirect=evil",
            "https://github.com/login/device#redirect", "https://github.com/login/device/other",
            "https://github.com/login/device/", "https://github.com:443/login/device",
            "https://github.com/login/%64evice", "https://github.com\\@evil.example/login/device"
        })
        {
            foreach (var text in new[]
            {
                $"To authenticate, visit {uri} and enter code ABCD-1234\n",
                $"Enter one-time code: ABCD-1234 at {uri}\n"
            })
            {
                var progress = new Recorder();
                using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
                foreach (var ch in text) parser.Append(false, new[] { ch });
                parser.Append(false, [], true);
                TestAssert.True(progress.Values.Count == 0 &&
                    parser.Failure == CopilotDeviceCodeFailure.UnsafePrompt,
                    "A fragmented untrusted host/path/query/userinfo/redirect was accepted.");
            }
        }
        foreach (var code in new[] { "12345678", Secret, "ABCD-12345", "abcd-1234", "ABCD-1234?secret" })
        {
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
            parser.Append(false, Prompt.Replace("ABCD-1234", code).AsSpan(), true);
            TestAssert.True(progress.Values.Count == 0, "An invalid user code was displayed.");
        }
        foreach (var suffix in new[] { "5", "?secret", "-MORE", "X" })
        {
            foreach (var error in new[] { false, true })
            {
                var progress = new Recorder();
                using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
                parser.Append(error, Prompt.AsSpan());
                TestAssert.True(progress.Values.Count == 0,
                    "An arbitrary chunk boundary was treated as a complete modern code.");
                parser.Append(error, (suffix + "\n").AsSpan(), true);
                TestAssert.True(progress.Values.Count == 0 &&
                    parser.Failure == CopilotDeviceCodeFailure.UnsafePrompt,
                    "A fragmented extended code or token suffix was displayed.");
            }
        }
        {
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
            parser.Append(false, Prompt.AsSpan());
            parser.Append(false, " ".AsSpan());
            TestAssert.True(progress.Values.Count == 1,
                "Confirmed whitespace without a newline did not complete the modern code.");
        }
        {
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
            parser.Append(false, (Prompt + "\n").AsSpan());
            parser.Append(true, (Prompt + "\n").AsSpan(), true);
            TestAssert.True(progress.Values.Count == 1, "Repeated stdout/stderr challenge was displayed twice.");
            parser.Dispose();
            parser.Append(false, Prompt.AsSpan(), true);
            TestAssert.True(!parser.HasChallenge && progress.Values.Count == 1,
                "Disposed parser retained a code or published late progress.");
        }
        {
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
            parser.Append(false, "To authenticate, visit https://github.com/login/".AsSpan());
            parser.Append(true, "device and enter code ABCD-1234\n".AsSpan(), true);
            parser.Append(false, [], true);
            TestAssert.True(progress.Values.Count == 0, "Separate process streams were concatenated.");
        }
        {
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, CancellationToken.None);
            parser.Append(false, (new string('X', 100_000) + Prompt).AsSpan(), true);
            TestAssert.True(progress.Values.Count == 0 && parser.HasVisibleOutput,
                "Oversized output was interpreted as a prompt or as empty existing authentication.");
            parser.Append(false, (Prompt + "\n").AsSpan(), true);
            TestAssert.True(progress.Values.Count == 1, "Bounded decoder did not recover after EOF.");
        }
        {
            using var cancelled = new CancellationTokenSource();
            var progress = new Recorder();
            using var parser = new CopilotDeviceCodeOutput(progress, cancelled.Token);
            cancelled.Cancel();
            parser.Append(false, Prompt.AsSpan(), true);
            TestAssert.True(progress.Values.Count == 0, "Cancelled parser published a device code.");
        }
        {
            using var parser = new CopilotDeviceCodeOutput(null, CancellationToken.None);
            parser.Append(false, "  --device-code".AsSpan());
            parser.Append(false, "-different-option\n".AsSpan(), true);
            TestAssert.True(!parser.SupportsDeviceCode, "A longer fragmented option passed feature detection.");
        }
    }

    private static async Task TestCommandsAndCompletionAsync()
    {
        foreach (var loginOutput in new[] { Prompt, "", "Already logged in as test-user", "Using GITHUB_TOKEN environment variable" })
        {
            var help = FakeProcess.Completed("Usage: login\n  --device-code  Use device flow\n");
            var login = FakeProcess.Completed(loginOutput);
            var calls = new List<string>();
            var service = Create(help, login, calls);
            var progress = new Recorder();
            await service.SignInAsync("fake-copilot.exe", CancellationToken.None, progress);
            TestAssert.True(calls.SequenceEqual(new[] { "login --help", "login --device-code" }),
                "CLI device sign-in changed arguments or skipped feature detection.");
            TestAssert.True(progress.Values.Count == (loginOutput == Prompt ? 1 : 0),
                "Existing auth without a challenge did not complete for separate access verification.");
            AssertClean(help);
            AssertClean(login);
        }

        var pendingHelp = FakeProcess.Completed("  --device-code\n");
        var pendingLogin = new FakeProcess();
        var challenge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Create(pendingHelp, pendingLogin).SignInAsync("fake", CancellationToken.None,
            new Recorder(_ => challenge.TrySetResult()));
        pendingLogin.Output.Write(Prompt + "\n");
        await challenge.Task.WaitAsync(TimeSpan.FromSeconds(2));
        TestAssert.True(!pending.IsCompleted, "Sign-in returned before the CLI process completed.");
        pendingLogin.Complete(0);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        AssertClean(pendingLogin);
    }

    private static async Task TestFailuresAsync()
    {
        foreach (var helpText in new[] { "Usage: login", "Text mentioning --device-code in a description" })
        {
            var help = FakeProcess.Completed(helpText);
            var login = FakeProcess.Completed(Prompt);
            var calls = new List<string>();
            var error = await FailureAsync(Create(help, login, calls));
            TestAssert.True(error.Message.Contains("Update GitHub Copilot CLI") && calls.Count == 1,
                "Unsupported CLI did not stop before login with update guidance.");
            AssertClean(help);
        }
        foreach (var (text, exit, expected) in new[]
        {
            ("expired_token " + Secret, 1, "expired"),
            ("Authorization was denied " + Secret, 1, "denied"),
            ("unknown option --device-code " + Secret, 1, "Update"),
            ("Error " + Secret, 7, "code 7"),
            ("Unexpected " + Secret, 0, "no recognized device prompt"),
            ("Store token in plaintext? " + Secret, 0, "secure Copilot CLI credential storage"),
            ("To authenticate, visit https://evil.example/login/device and enter code ABCD-1234", 0, "GitHub.com only"),
            (new string('X', 100_000), 0, "no recognized device prompt"),
            (Prompt + "\naccess_denied " + Secret, 0, "denied")
        })
        {
            var login = FakeProcess.Completed(text, exit, errorStream: true);
            var error = await FailureAsync(Create(FakeProcess.Completed("  --device-code\n"), login));
            TestAssert.True(error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                !error.ToString().Contains(Secret) && !error.ToString().Contains("ABCD-1234") &&
                !error.ToString().Contains("evil.example") && error.InnerException is null &&
                error.Message.Length < 500,
                "Sign-in failed without the expected sanitized bounded error.");
            AssertClean(login);
        }
        var startFailure = new CopilotDeviceCodeSignIn((_, _) =>
            throw new IOException(Secret), TimeSpan.FromSeconds(2));
        TestAssert.True(!(await FailureAsync(startFailure)).ToString().Contains(Secret),
            "Process start failure exposed credentials.");

        var waiting = new FakeProcess();
        var rejected = FailureAsync(Create(FakeProcess.Completed(" --device-code\n"), waiting));
        waiting.Output.Write("To authenticate, visit https://evil.example/login/device and enter code ABCD-1234\n");
        var rejection = await rejected.WaitAsync(TimeSpan.FromSeconds(2));
        TestAssert.True(rejection.Message.Contains("GitHub.com only"),
            "An unsafe prompt was left awaiting authorization rather than stopped.");
        AssertClean(waiting);
    }

    private static async Task TestCancellationAndTimeoutAsync()
    {
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var starts = 0;
            var service = new CopilotDeviceCodeSignIn((_, _) =>
            {
                starts++;
                return new FakeProcess();
            }, TimeSpan.FromSeconds(2));
            await TestAssert.ThrowsAsync<OperationCanceledException>(
                () => service.SignInAsync("fake", cancelled.Token), "Pre-cancelled sign-in started a CLI.");
            TestAssert.True(starts == 0, "Cancellation did not fence process creation.");
        }
        foreach (var cancelDuringHelp in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var help = cancelDuringHelp ? new FakeProcess() : FakeProcess.Completed(" --device-code\n");
            var login = new FakeProcess();
            var progress = new Recorder(_ => cancellation.Cancel());
            var pending = Create(help, login).SignInAsync("fake", cancellation.Token, progress);
            if (cancelDuringHelp) cancellation.Cancel();
            else login.Output.Write(Prompt + "\n");
            await TestAssert.ThrowsAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(3)), "Cancellation was swallowed or cleanup hung.");
            AssertClean(help);
            if (!cancelDuringHelp)
            {
                AssertClean(login);
                TestAssert.True(progress.Values.Count == 1, "Cancellation did not fence subsequent progress.");
            }
        }
        foreach (var timeoutHelp in new[] { false, true })
        {
            var help = timeoutHelp ? new FakeProcess() : FakeProcess.Completed(" --device-code\n");
            var login = new FakeProcess();
            var service = Create(help, login, duration: TimeSpan.FromMilliseconds(30));
            await TestAssert.ThrowsAsync<TimeoutException>(
                () => service.SignInAsync("fake", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)),
                "A stalled process or stream did not time out.");
            AssertClean(help);
            if (!timeoutHelp) AssertClean(login);
        }
        {
            var login = new FakeProcess();
            login.ExitWithoutClosingStreams(0);
            var service = Create(FakeProcess.Completed(" --device-code\n"), login,
                duration: TimeSpan.FromMilliseconds(30));
            await TestAssert.ThrowsAsync<TimeoutException>(
                () => service.SignInAsync("fake", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)),
                "A completed process with inherited open pipe handles hung the sign-in.");
            AssertClean(login);
        }
    }

    private static async Task TestCleanupFailuresAsync()
    {
        foreach (var stage in new[] { "kill", "close", "dispose", "all" })
        {
            var login = FakeProcess.Completed(Prompt + "\n");
            login.FailKill = stage is "kill" or "all";
            login.FailClose = stage is "close" or "all";
            login.FailDispose = stage is "dispose" or "all";
            var failure = await FailureAsync(Create(FakeProcess.Completed(" --device-code\n"), login));
            TestAssert.True(!failure.ToString().Contains(Secret) && failure.InnerException is null &&
                failure.Message.Contains("cleanup"),
                "Termination failure leaked a raw exception or was reported as success.");
            AssertClean(login);
        }
        using var cancellation = new CancellationTokenSource();
        var cancelledLogin = new FakeProcess { FailKill = true, FailClose = true, FailDispose = true };
        var signIn = Create(FakeProcess.Completed(" --device-code\n"), cancelledLogin)
            .SignInAsync("fake", cancellation.Token);
        cancellation.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => signIn.WaitAsync(TimeSpan.FromSeconds(3)),
            "Throwing cleanup replaced explicit cancellation or hung.");
        AssertClean(cancelledLogin);
    }

    private static async Task TestNativeProcessLifetimeAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var shell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var childScript = "[Console]::Out.WriteLine('child:' + $PID); [Console]::Out.Flush(); Start-Sleep -Seconds 60";
        var encodedChild = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
        var rootScript = $"Start-Process -FilePath '{shell.Replace("'", "''")}' -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','{encodedChild}' -NoNewWindow; exit 0";
        var encodedRoot = Convert.ToBase64String(Encoding.Unicode.GetBytes(rootScript));
        foreach (var disposeOnly in new[] { false, true })
        {
            using var process = new CopilotDeviceCodeProcess(shell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedRoot]);
            using var lifetimeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Process? child = null;
            Task<string>? drain = null;
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(lifetimeTimeout.Token);
                TestAssert.True(line is not null && line.StartsWith("child:", StringComparison.Ordinal) &&
                    int.TryParse(line["child:".Length..], out _),
                    "Native fixture did not inherit the job's stdout.");
                child = Process.GetProcessById(int.Parse(line!["child:".Length..]));
                _ = child.Handle;
                TestAssert.True(await process.WaitForExitAsync(lifetimeTimeout.Token) == 0 && !child.HasExited,
                    "Native fixture did not leave a live descendant after its parent exited.");
                drain = process.StandardOutput.ReadToEndAsync(lifetimeTimeout.Token);
                TestAssert.True(!drain.IsCompleted, "Descendant did not retain the inherited stdout pipe.");
                if (disposeOnly) process.Dispose();
                else process.Kill();
                await child.WaitForExitAsync(lifetimeTimeout.Token);
                try { await drain.WaitAsync(lifetimeTimeout.Token); }
                catch (Exception ex) when (disposeOnly && ex is IOException or ObjectDisposedException) { }
                TestAssert.True(child.HasExited, "Closing the private job left an orphan after root exit.");
            }
            finally
            {
                try { process.Kill(); }
                finally
                {
                    try { process.CloseStreams(); }
                    finally
                    {
                        child?.Dispose();
                        if (drain is not null)
                            _ = drain.ContinueWith(task => { _ = task.Exception; },
                                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    }
                }
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var input = new CopilotDeviceCodeProcess(shell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "if ([Console]::In.Read() -eq -1) { [Console]::Out.Write('eof'); [Console]::Error.Write('stderr'); exit 0 } else { exit 2 }"]);
        var stdout = input.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = input.StandardError.ReadToEndAsync(timeout.Token);
        TestAssert.True(await input.WaitForExitAsync(timeout.Token) == 0 &&
            await stdout == "eof" && await stderr == "stderr",
            "Native job startup did not preserve stdin EOF or separate stdout/stderr.");
    }

    private static CopilotDeviceCodeSignIn Create(FakeProcess help, FakeProcess login,
        List<string>? calls = null, TimeSpan? duration = null)
        => new((_, args) =>
        {
            calls?.Add(string.Join(" ", args));
            return args.SequenceEqual(new[] { "login", "--help" }) ? help : login;
        }, duration ?? TimeSpan.FromSeconds(3), duration);

    private static async Task<InvalidOperationException> FailureAsync(CopilotDeviceCodeSignIn service)
    {
        try { await service.SignInAsync("fake", CancellationToken.None); }
        catch (InvalidOperationException ex) { return ex; }
        throw new InvalidOperationException("Expected device sign-in to fail.");
    }

    private static void AssertClean(FakeProcess process)
        => TestAssert.True(process.Killed && process.Closed && process.Disposed &&
            process.Output.ActiveReads == 0 && process.Error.ActiveReads == 0 && process.Waiters == 0,
            "Process completion left a process, open streams, or background reader/waiter.");

    private sealed class Recorder(Action<CopilotDeviceCodeChallenge>? onReport = null)
        : IProgress<CopilotDeviceCodeChallenge>
    {
        public List<CopilotDeviceCodeChallenge> Values { get; } = [];
        public void Report(CopilotDeviceCodeChallenge value)
        {
            Values.Add(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class FakeProcess : ICopilotDeviceCodeProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChunkReader Output { get; } = new();
        public ChunkReader Error { get; } = new();
        public TextReader StandardOutput => Output;
        public TextReader StandardError => Error;
        public bool Killed { get; private set; }
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailKill { get; set; }
        public bool FailClose { get; set; }
        public bool FailDispose { get; set; }
        public int Waiters;
        public static FakeProcess Completed(string text, int exit = 0, bool errorStream = false)
        {
            var process = new FakeProcess();
            (errorStream ? process.Error : process.Output).Write(text);
            process.Complete(exit);
            return process;
        }
        public void Complete(int exit)
        {
            Output.Complete();
            Error.Complete();
            _exit.TrySetResult(exit);
        }
        public void ExitWithoutClosingStreams(int exit) => _exit.TrySetResult(exit);
        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Waiters);
            try { return await _exit.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref Waiters); }
        }
        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(-1);
            if (FailKill) throw new AggregateException(new IOException(Secret));
        }
        public void CloseStreams()
        {
            Closed = true;
            Output.Complete();
            Error.Complete();
            if (FailClose) throw new IOException(Secret);
        }
        public void Dispose()
        {
            Disposed = true;
            if (FailDispose) throw new InvalidOperationException(Secret);
        }
    }

    private sealed class ChunkReader : TextReader
    {
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>();
        private string _current = "";
        private int _offset;
        public int ActiveReads;
        public void Write(string value) { if (value.Length > 0) _chunks.Writer.TryWrite(value); }
        public void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ActiveReads);
            try
            {
                if (_offset == _current.Length)
                {
                    if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
                    _current = await _chunks.Reader.ReadAsync(ct);
                    _offset = 0;
                }
                var count = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            finally { Interlocked.Decrement(ref ActiveReads); }
        }
    }
}
