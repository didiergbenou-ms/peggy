using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class SetupExperienceSmokeTests
{
    public static async Task RunAsync()
    {
        TestEmailValidation();
        await TestReadinessAsync();
        await TestReadinessProgressAsync();
        await TestPinnedInboxProbeAsync();
    }

    internal static void RunVisualPreview()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The setup preview must run on an STA thread.");
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var readiness = new FakeReadiness();
            readiness.LocalOperation = async (ct, progress) =>
            {
                var local = new SetupLocalReadiness(
                    new(SetupCheckState.Ready, "Node.js 22.0.0 (simulated)", Version: "22.0.0"),
                    new(SetupCheckState.Ready, "npm 10.8.0 (simulated)", Version: "10.8.0"),
                    new(SetupCheckState.Ready, "PowerShell 7.4.0 (simulated)", Version: "7.4.0"),
                    new(SetupCheckState.Ready, "Copilot CLI 1.0.81 (simulated)", Version: "1.0.81"),
                    new(SetupCheckState.Ready, "Standalone WorkIQ (simulated)", Version: readiness.Version));
                var checks = new[]
                {
                    new SetupLocalCheckUpdate(SetupLocalCheck.Node, local.Node),
                    new SetupLocalCheckUpdate(SetupLocalCheck.Npm, local.Npm),
                    new SetupLocalCheckUpdate(SetupLocalCheck.PowerShell, local.PowerShell),
                    new SetupLocalCheckUpdate(SetupLocalCheck.Copilot, local.Copilot),
                    new SetupLocalCheckUpdate(SetupLocalCheck.WorkIq, local.WorkIq)
                };
                foreach (var check in checks)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
                    progress?.Report(check);
                }
                return local;
            };
            readiness.SignInOperation = async (ct, progress) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(900), ct);
                progress?.Report(new("ABCD-1234", new Uri("https://github.com/login/device")));
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                throw new TimeoutException("The simulated device-code preview expired.");
            };
            var window = new WorkIqSignInWindow(null, new FakeWorkIq(), readiness, previewMode: true,
                installVoice: (_, _, _) => Task.FromException(new InvalidOperationException("Voice is disabled in the preview.")),
                voiceInstalled: _ => false)
            {
                Title = "Peggy setup — simulated preview (no authentication or installation)"
            };
            Find<GroupBox>(window, "VoiceSetupGroup").IsEnabled = false;
            foreach (var name in new[] { "ReviewEulaButton", "OpenGitHubButton" })
            {
                var link = Find<Button>(window, name);
                var parent = (Panel)link.Parent;
                var position = parent.Children.IndexOf(link);
                parent.Children.Remove(link);
                parent.Children.Insert(position, new Border { IsEnabled = false, Child = link });
            }
            window.Loaded += async (_, _) =>
            {
                await window.CheckLocalAsync();
                if (Find<Button>(window, "SignInCopilotButton").IsEnabled)
                    await window.CheckCopilotAsync(signIn: true);
            };
            application.Run(window);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            application.Shutdown();
        }
    }

    // Register in DesktopSmokeTests.RunOnStaThread, alongside the other WPF checks.
    public static void RunDesktop()
    {
        var previousAccount = Environment.GetEnvironmentVariable(PeggyDefaults.WorkIqAccountEnvironmentVariable);
        var previousVoice = Environment.GetEnvironmentVariable("PEGGY_SPEECH_MODELS");
        try
        {
            Environment.SetEnvironmentVariable(PeggyDefaults.WorkIqAccountEnvironmentVariable, "unverified@example.com");
            Environment.SetEnvironmentVariable("PEGGY_SPEECH_MODELS",
                System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts",
                    $"setup-test-no-voice-{Guid.NewGuid():N}"));
            TestFirstRunWindow();
            TestReconnectAndCancellation();
            TestExplicitFinish();
            TestProgressFeedback();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PeggyDefaults.WorkIqAccountEnvironmentVariable, previousAccount);
            Environment.SetEnvironmentVariable("PEGGY_SPEECH_MODELS", previousVoice);
        }
    }

    private static void TestEmailValidation()
    {
        foreach (var invalid in new[] { "", " ", "person", "person@local", "Name <person@example.com>",
            "person @example.com", "one@example.com;two@example.com", "person@example.com\nother" })
        {
            TestAssert.True(!WorkIqSetupService.IsValidAccount(invalid), $"Accepted invalid setup email: {invalid}");
        }
        TestAssert.True(WorkIqSetupService.IsValidAccount(" Person+work@Example.com "),
            "A valid work email was rejected.");
    }

    private static async Task TestReadinessAsync()
    {
        var workIq = new FakeWorkIq();
        var commands = new List<string>();
        var modelCalls = 0;
        var copilotVersion = "1.0.80";
        var nodeVersion = "17.9.0";
        var service = new SetupReadinessService(workIq, (path, args, ct) =>
        {
            commands.Add($"{path} {string.Join(" ", args)}");
            return Task.FromResult(path switch
            {
                "node.exe" => $"v{nodeVersion}",
                "npm.cmd" => "10.8.0",
                "pwsh.exe" => "7.4.0",
                _ => $"GitHub Copilot CLI {copilotVersion}"
            });
        }, name => name, ct =>
        {
            modelCalls++;
            return Task.FromResult<IReadOnlyList<CopilotModelOption>>([new("test-model", "Test model")]);
        });
        var old = await service.CheckLocalAsync(CancellationToken.None);
        TestAssert.True(!old.IsReady && old.Node.State == SetupCheckState.ActionRequired &&
            old.Copilot.State == SetupCheckState.ActionRequired,
            "Setup accepted outdated Node.js or Copilot CLI.");
        TestAssert.True(modelCalls == 0 && workIq.SignIns == 0 && workIq.Installs == 0,
            "Automatic local checks performed network authentication or installation.");
        TestAssert.True(commands.Count == 4 && commands.All(command =>
            command.Contains("--version") || command.Contains("$PSVersionTable.PSVersion")),
            "Local checks ran something other than version discovery.");
        nodeVersion = "22.0.0";
        copilotVersion = "1.0.81";
        TestAssert.True((await service.CheckLocalAsync(CancellationToken.None)).IsReady,
            "The exact minimum Copilot CLI was rejected.");
        TestAssert.True((await service.CheckCopilotAccessAsync(CancellationToken.None)).IsReady && modelCalls == 1,
            "The explicit Copilot readiness check did not use the model catalog.");
        var missing = new SetupReadinessService(workIq,
            (_, _, _) => throw new InvalidOperationException("Must not run."),
            _ => null,
            _ => Task.FromResult<IReadOnlyList<CopilotModelOption>>([]));
        TestAssert.True(!(await missing.CheckLocalAsync(CancellationToken.None)).IsReady &&
            (await missing.CheckCopilotAccessAsync(CancellationToken.None)).State == SetupCheckState.ActionRequired,
            "Missing prerequisites or an empty model catalog was reported ready.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new SetupReadinessService(workIq,
            (_, _, ct) => Task.FromCanceled<string>(ct), name => name,
            ct => Task.FromCanceled<IReadOnlyList<CopilotModelOption>>(ct));
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => cancelled.CheckLocalAsync(cancellation.Token),
            "Readiness swallowed cancellation.");
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => cancelled.CheckCopilotAccessAsync(cancellation.Token),
            "Model-catalog readiness swallowed explicit cancellation.");
        var defective = new SetupReadinessService(workIq,
            (_, _, _) => Task.FromException<string>(new NullReferenceException("Test defect")),
            name => name,
            _ => Task.FromException<IReadOnlyList<CopilotModelOption>>(new NullReferenceException("Test defect")));
        await TestAssert.ThrowsAsync<NullReferenceException>(
            () => defective.CheckLocalAsync(CancellationToken.None),
            "Local readiness swallowed a programmer defect.");
        await TestAssert.ThrowsAsync<NullReferenceException>(
            () => defective.CheckCopilotAccessAsync(CancellationToken.None),
            "Model-catalog readiness swallowed a programmer defect.");
        workIq.VersionFailure = new NullReferenceException("Test defect");
        await TestAssert.ThrowsAsync<NullReferenceException>(
            () => service.CheckLocalAsync(CancellationToken.None),
            "WorkIQ readiness swallowed a programmer defect.");
        workIq.VersionFailure = new System.IO.IOException("Test process failure");
        TestAssert.True((await service.CheckLocalAsync(CancellationToken.None)).WorkIq.State == SetupCheckState.Failed,
            "An expected WorkIQ process failure was not surfaced inline.");
        workIq.VersionFailure = null;
    }

    private static async Task TestPinnedInboxProbeAsync()
    {
        var calls = new List<(string Account, string Url)>();
        var profileId = "verified-principal";
        var changeAfterRead = false;
        var malformedMail = false;
        var service = new WorkIqSetupService((account, url, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            calls.Add((account, url));
            if (url.StartsWith("/me?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    id = profileId,
                    mail = "mailbox@example.com",
                    userPrincipalName = "signin@tenant.example",
                    displayName = "Verified Person"
                }));
            }
            if (changeAfterRead) profileId = "wrong-principal";
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                value = malformedMail ? null : Array.Empty<object>()
            }));
        });
        var identity = await service.VerifyAccountAsync("signin@tenant.example");
        TestAssert.True(identity.DisplayName == "Verified Person" && identity.Mail == "mailbox@example.com",
            "Setup did not preserve the verified display name and mailbox alias.");
        calls.Clear();
        await service.ProbeInboxAccessAsync(identity);
        TestAssert.True(calls.Count == 3 &&
            calls.All(call => call.Account == "signin@tenant.example") &&
            calls[0].Url.StartsWith("/me?") && calls[2].Url.StartsWith("/me?") &&
            calls[1].Url == "/users/verified-principal/mailFolders/inbox/messages?$select=id&$top=1",
            "Inbox readiness did not bracket a minimal pinned-identity message-id probe with identity checks.");
        TestAssert.True(calls.All(call => !call.Url.Contains("childFolders") &&
            !call.Url.Contains("body", StringComparison.OrdinalIgnoreCase)),
            "Inbox readiness read message bodies or enumerated child folders.");
        changeAfterRead = true;
        await TestAssert.ThrowsAsync<WorkIqSetupException>(() => service.ProbeInboxAccessAsync(identity),
            "Inbox readiness accepted an account change during the probe.");
        profileId = "verified-principal";
        changeAfterRead = false;
        malformedMail = true;
        await TestAssert.ThrowsAsync<WorkIqSetupException>(() => service.ProbeInboxAccessAsync(identity),
            "An invalid Inbox response was reported ready.");
        await TestAssert.ThrowsAsync<WorkIqSetupException>(() => service.VerifyAccountAsync("wrong@example.com"),
            "Setup accepted a typed email unrelated to the returned identity.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => service.ProbeInboxAccessAsync(identity, cancellation.Token),
            "Inbox readiness swallowed cancellation.");
    }

    private static async Task TestReadinessProgressAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var pending = Enum.GetValues<SetupLocalCheck>().ToDictionary(
                check => check, _ => new TaskCompletionSource<string>());
            var reported = Enum.GetValues<SetupLocalCheck>().ToDictionary(
                check => check, _ => new TaskCompletionSource<SetupLocalCheckUpdate>());
            var order = new System.Collections.Concurrent.ConcurrentQueue<SetupLocalCheck>();
            var setup = new FakeWorkIq { VersionOperation = _ => pending[SetupLocalCheck.WorkIq].Task };
            var readiness = new SetupReadinessService(setup, (path, _, _) => pending[path switch
            {
                "node.exe" => SetupLocalCheck.Node,
                "npm.cmd" => SetupLocalCheck.Npm,
                "pwsh.exe" => SetupLocalCheck.PowerShell,
                _ => SetupLocalCheck.Copilot
            }].Task, name => name, _ => throw new InvalidOperationException("Local checks cannot request models."));
            var operation = readiness.CheckLocalAsync(cancellation.Token,
                new InlineProgress<SetupLocalCheckUpdate>(update =>
                {
                    order.Enqueue(update.Check);
                    reported[update.Check].SetResult(update);
                }));
            pending[SetupLocalCheck.Npm].SetResult("10.8.0");
            var first = await reported[SetupLocalCheck.Npm].Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestAssert.True(first.Result.IsReady && !operation.IsCompleted && order.Count == 1,
                "A completed local check waited for other checks before reporting progress.");
            if (cancel) cancellation.Cancel();
            var remaining = new[]
            {
                (SetupLocalCheck.Node, "22.0.0"), (SetupLocalCheck.WorkIq, "test-workiq-1"),
                (SetupLocalCheck.Copilot, "1.0.81"), (SetupLocalCheck.PowerShell, "7.4.0")
            };
            foreach (var (check, version) in remaining)
            {
                pending[check].SetResult(version);
                if (!cancel)
                    await reported[check].Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (cancel)
            {
                await TestAssert.ThrowsAsync<OperationCanceledException>(() => operation,
                    "Cancelled local checks returned a readiness result.");
                TestAssert.True(order.Count == 1, "A local check reported late progress after cancellation.");
            }
            else
            {
                TestAssert.True((await operation).IsReady && order.SequenceEqual(new[]
                {
                    SetupLocalCheck.Npm, SetupLocalCheck.Node, SetupLocalCheck.WorkIq,
                    SetupLocalCheck.Copilot, SetupLocalCheck.PowerShell
                }), "Per-check readiness progress did not preserve actual completion order.");
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static void TestFirstRunWindow()
    {
        var setup = new FakeWorkIq { FailMail = true };
        var readiness = new FakeReadiness();
        var window = new WorkIqSignInWindow(null, setup, readiness, previewMode: true);
        try
        {
            var email = Find<TextBox>(window, "AccountText");
            TestAssert.True(email.Text == "" && AutomationProperties.GetName(email) == "Work email" &&
                !Find<Button>(window, "SignInButton").IsEnabled &&
                !Find<Button>(window, "FinishButton").IsEnabled,
                "Setup prefilled an unverified environment account or enabled actions prematurely.");
            window.CheckLocalAsync().GetAwaiter().GetResult();
            TestAssert.True(readiness.AccessChecks == 0 && setup.SignIns == 0,
                "Local readiness caused a network/authentication check.");
            email.Text = "name";
            Find<CheckBox>(window, "EulaCheck").IsChecked = true;
            TestAssert.True(!Find<Button>(window, "SignInButton").IsEnabled, "Invalid email enabled sign-in.");
            email.Text = "signin@tenant.example";
            Find<CheckBox>(window, "EulaCheck").IsChecked = false;
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(setup.SignIns == 0, "Setup bypassed explicit terms acceptance.");
            Find<CheckBox>(window, "EulaCheck").IsChecked = true;
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(setup.SignIns == 1 && setup.AcceptedVersion == "test-workiq-1" &&
                Find<Border>(window, "ConnectedCard").Visibility == Visibility.Visible &&
                Find<TextBlock>(window, "ConnectedName").Text == "Verified Person" &&
                Find<TextBlock>(window, "ConnectedEmail").Text == "mailbox@example.com" &&
                Find<TextBlock>(window, "AccountVerifiedStatus").Text.StartsWith("Ready") &&
                Find<TextBlock>(window, "MailAccessStatus").Text.StartsWith("Failed") &&
                !window.CanFinish && window.AuthenticatedIdentity is null && window.DialogResult != true,
                "Setup did not retain a visible verified identity while separately reporting failed mail access.");
            TestAssert.True(Find<Expander>(window, "TechnicalDetails").Visibility == Visibility.Visible,
                "A mail access failure had no technical details.");
            setup.FailMail = false;
            window.RetryMailAsync().GetAwaiter().GetResult();
            TestAssert.True(setup.SignIns == 1 && !window.CanFinish,
                "Mail retry triggered a second login or bypassed required GitHub access.");
            window.CheckCopilotAsync().GetAwaiter().GetResult();
            TestAssert.True(window.CanFinish && !window.VoiceModelsReady &&
                window.AuthenticatedAccount is null && window.DialogResult != true,
                "Voice blocked completion, or setup committed before explicit Finish.");
            TestAssert.True(Find<TextBlock>(window, "MonitoringSummary").Text.Contains("Inbox only + Chats only"),
                "Setup did not present exact first-run monitoring defaults.");
            window.ChangeAccount();
            TestAssert.True(!window.CanFinish && Find<StackPanel>(window, "AccountEditor").Visibility == Visibility.Visible &&
                !email.IsReadOnly && email.IsEnabled, "Change account did not restore editable sign-in.");
            readiness.Version = "test-workiq-2";
            window.CheckLocalAsync().GetAwaiter().GetResult();
            TestAssert.True(Find<CheckBox>(window, "EulaCheck").IsChecked != true &&
                !Find<Button>(window, "SignInButton").IsEnabled,
                "WorkIQ version changes reused old terms acceptance.");
        }
        finally { window.Close(); }
    }

    private static void TestReconnectAndCancellation()
    {
        var setup = new FakeWorkIq { FailMail = true };
        var window = new WorkIqSignInWindow("signin@tenant.example", setup, new FakeReadiness(),
            previewMode: true, expectedMailbox: "mailbox@example.com",
            expectedPrincipalId: "verified-principal", requireMailAccess: false);
        try
        {
            TestAssert.True(Find<TextBox>(window, "AccountText").Text == "signin@tenant.example",
                "A stale environment preference replaced the verified account.");
            window.CheckLocalAsync().GetAwaiter().GetResult();
            Find<CheckBox>(window, "EulaCheck").IsChecked = true;
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(window.CanFinish && setup.MailChecks == 0 &&
                Find<TextBlock>(window, "MailAccessStatus").Text.StartsWith("Optional / skipped"),
                "Outlook Off reauthentication was blocked by irrelevant mail access.");
            window.ChangeAccount();
            setup.PrincipalId = "replacement-principal";
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(window.CanFinish && setup.MailChecks == 0 &&
                Find<TextBlock>(window, "MonitoringSummary").Text.Contains("existing monitoring modes are retained") &&
                Find<TextBlock>(window, "MonitoringSummary").Text.Contains("selections are cleared") &&
                !Find<TextBlock>(window, "MonitoringSummary").Text.Contains("Inbox only + Chats only"),
                "Account switching changed existing modes or required mail access while Outlook remained Off.");
            window.ChangeAccount();
            setup.OnSignIn = ct =>
            {
                TestAssert.True(!Find<TextBox>(window, "AccountText").IsEnabled &&
                    !Find<Button>(window, "ChangeAccountButton").IsEnabled &&
                    !Find<Button>(window, "FinishButton").IsEnabled,
                    "Setup actions remained enabled during authentication.");
                Find<Button>(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ct.ThrowIfCancellationRequested();
            };
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(window.AuthenticationAttempted && window.AuthenticatedIdentity is null &&
                !window.CanFinish && Find<TextBox>(window, "AccountText").Text == "signin@tenant.example" &&
                Find<TextBlock>(window, "OperationStatus").Text.Contains("cancelled"),
                "Cancelled authentication committed an account or failed to restore editing.");
        }
        finally { window.Close(); }
    }

    private static void TestExplicitFinish()
    {
        foreach (var finish in new[] { false, true })
        {
            var window = new WorkIqSignInWindow(null, new FakeWorkIq(),
                new FakeReadiness(), previewMode: true);
            window.CheckLocalAsync().GetAwaiter().GetResult();
            window.CheckCopilotAsync().GetAwaiter().GetResult();
            Find<TextBox>(window, "AccountText").Text = "signin@tenant.example";
            Find<CheckBox>(window, "EulaCheck").IsChecked = true;
            window.ConnectMicrosoftAsync().GetAwaiter().GetResult();
            TestAssert.True(window.CanFinish && window.AuthenticatedIdentity is null,
                "Verification completed the dialog without the final user action.");
            window.Dispatcher.BeginInvoke(new Action(() =>
                Find<Button>(window, finish ? "FinishButton" : "CancelButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            var completed = window.ShowDialog();
            TestAssert.True(finish
                    ? completed == true && window.AuthenticatedIdentity?.PrincipalId == "verified-principal"
                    : completed == false && window.AuthenticatedIdentity is null &&
                        window.AuthenticatedAccount is null && window.AcceptedEulaVersion is null,
                "Final Finish or Cancel did not preserve the setup commit boundary.");
        }
    }

    private static void TestProgressFeedback()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            TestLocalProgress();
            TestGitHubProgress();
            TestMicrosoftProgress();
            TestInstallProgress();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void TestLocalProgress()
    {
        var pending = new TaskCompletionSource<SetupLocalReadiness>();
        IProgress<SetupLocalCheckUpdate>? progress = null;
        var readiness = new FakeReadiness
        {
            LocalOperation = (_, updates) => { progress = updates; return pending.Task; }
        };
        var window = new WorkIqSignInWindow(null, new FakeWorkIq(), readiness, previewMode: true);
        try
        {
            var operation = window.CheckLocalAsync();
            foreach (var name in new[] { "NodeIndicator", "NpmIndicator", "PowerShellIndicator",
                "CopilotCliIndicator", "InstallationIndicator" })
                AssertState(window, name, SetupStepState.Running);
            progress!.Report(new(SetupLocalCheck.Node, readiness.ReadyLocal().Node));
            progress.Report(new(SetupLocalCheck.Npm, new(SetupCheckState.Failed, "Test npm failure")));
            AssertState(window, "NodeIndicator", SetupStepState.Succeeded);
            AssertState(window, "NpmIndicator", SetupStepState.Failed);
            AssertState(window, "PowerShellIndicator", SetupStepState.Running);
            Task.Run(() => progress.Report(new(SetupLocalCheck.PowerShell, readiness.ReadyLocal().PowerShell)))
                .GetAwaiter().GetResult();
            window.CancelActiveOperation();
            SetupStepIndicatorTests.DrainDispatcher();
            AssertState(window, "PowerShellIndicator", SetupStepState.Idle);
            AssertState(window, "NodeIndicator", SetupStepState.Succeeded);
            pending.SetResult(readiness.ReadyLocal());
            SetupStepIndicatorTests.Complete(operation);
            TestAssert.True(!window.CanFinish && !Find<Button>(window, "SignInButton").IsEnabled,
                "Late local-check completion enabled sign-in after cancellation.");
            readiness.LocalOperation = (_, _) => Task.FromException<SetupLocalReadiness>(new IOException("Test failure"));
            SetupStepIndicatorTests.Complete(window.CheckLocalAsync());
            AssertState(window, "InstallationIndicator", SetupStepState.Failed);
            TestAssert.True(Find<Button>(window, "RetryLocalButton").IsEnabled, "Failed local checks cannot be retried.");
            readiness.LocalOperation = null;
            SetupStepIndicatorTests.Complete(window.CheckLocalAsync());
            AssertState(window, "InstallationIndicator", SetupStepState.Succeeded);
        }
        finally { window.Close(); }
    }

    private static void TestGitHubProgress()
    {
        foreach (var url in new[] { "http://github.com/login/device", "https://github.com.evil.example/login/device",
            "https://github.com/login/device?code=ABCD-1234", "https://github.com/login/device#code",
            "https://user@github.com/login/device", "https://github.com:444/login/device", "file:///C:/test" })
            TestAssert.True(!WorkIqSignInWindow.IsAllowedGitHubVerificationUri(new Uri(url)),
                $"An unsafe GitHub verification URL was accepted: {url}");
        TestAssert.True(WorkIqSignInWindow.IsAllowedGitHubVerificationUri(new Uri("https://github.com/login/device")),
            "The exact GitHub device verification URL was rejected.");
        foreach (var ending in new[] { "success", "failure", "cancel", "close", "no-challenge" })
        {
            var signIn = new TaskCompletionSource();
            var catalog = new TaskCompletionSource<SetupCheck>();
            IProgress<CopilotDeviceCodeChallenge>? progress = null;
            var readiness = new FakeReadiness
            {
                SignInOperation = (_, updates) => { progress = updates; return signIn.Task; },
                AccessOperation = _ => catalog.Task
            };
            var window = new WorkIqSignInWindow(null, new FakeWorkIq(), readiness, previewMode: true);
            try
            {
                var operation = window.CheckCopilotAsync(signIn: true);
                AssertState(window, "CopilotAccessIndicator", SetupStepState.Running);
                TestAssert.True(Find<Border>(window, "GitHubDeviceCodeCard").Visibility == Visibility.Collapsed,
                    "A device code was fabricated before a challenge arrived.");
                if (ending != "no-challenge")
                {
                    progress!.Report(new("ABCD-1234", new Uri("https://github.com/login/device")));
                    AssertState(window, "CopilotAccessIndicator", SetupStepState.WaitingForUser);
                    var code = Find<TextBox>(window, "GitHubDeviceCodeText");
                    TestAssert.True(code.IsReadOnly && !code.IsUndoEnabled && code.Text == "ABCD-1234",
                        "GitHub device code was not safely selectable.");
                    var copied = "existing clipboard";
                    Uri? opened = null;
                    window.CopyGitHubCode(value => copied = value);
                    window.OpenGitHub(uri => opened = uri);
                    TestAssert.True(copied == "ABCD-1234" && opened?.AbsoluteUri == "https://github.com/login/device",
                        "Explicit Copy code or Open GitHub did not use the validated challenge.");
                }
                if (ending == "cancel") window.CancelActiveOperation();
                if (ending == "close") window.Close();
                if (ending is "cancel" or "close")
                {
                    AssertDeviceCodeCleared(window);
                    progress!.Report(new("EFGH-5678", new Uri("https://github.com/login/device")));
                    AssertDeviceCodeCleared(window);
                }
                if (ending == "failure")
                    signIn.SetException(new InvalidOperationException("Test login failure ABCD-1234 token-secret"));
                else signIn.SetResult();
                SetupStepIndicatorTests.DrainDispatcher();
                if (ending is "success" or "no-challenge")
                {
                    TestAssert.True(readiness.AccessChecks == 1, "Successful CLI login skipped model access verification.");
                    AssertDeviceCodeCleared(window);
                    AssertState(window, "CopilotAccessIndicator", SetupStepState.Running);
                    if (ending == "no-challenge")
                        TestAssert.True(Find<TextBlock>(window, "OperationStatus").Text.Contains("Verifying existing"),
                            "A no-challenge login claimed a new interactive sign-in.");
                    progress!.Report(new("EFGH-5678", new Uri("https://github.com/login/device")));
                    AssertDeviceCodeCleared(window);
                    catalog.SetResult(new(SetupCheckState.Ready, "Catalog ready"));
                }
                SetupStepIndicatorTests.Complete(operation);
                AssertDeviceCodeCleared(window);
                var overwritten = false;
                window.CopyGitHubCode(_ => overwritten = true);
                window.OpenGitHub(_ => overwritten = true);
                TestAssert.True(!overwritten, "An expired challenge could alter the clipboard or open a browser.");
                TestAssert.True(!Find<TextBox>(window, "DetailsText").Text.Contains("ABCD-1234") &&
                    !Find<TextBlock>(window, "OperationStatus").Text.Contains("token-secret"),
                    "Device-code authentication secrets escaped into diagnostics.");
                if (ending == "failure")
                {
                    AssertState(window, "CopilotAccessIndicator", SetupStepState.Failed);
                    TestAssert.True(Find<TextBlock>(window, "CopilotAccessStatus").Text.Contains("update GitHub Copilot CLI"),
                        "Unsupported device login had no update guidance.");
                }
                if (ending is "cancel" or "close")
                    TestAssert.True(readiness.AccessChecks == 0, "Cancelled GitHub login triggered access verification.");
            }
            finally { window.Close(); }
        }
    }

    private static void TestMicrosoftProgress()
    {
        var login = new TaskCompletionSource<WorkIqAccountIdentity>();
        var mail = new TaskCompletionSource();
        IProgress<WorkIqSignInStage>? stages = null;
        var setup = new FakeWorkIq
        {
            SignInOperation = (_, _, progress) => { stages = progress; return login.Task; },
            MailOperation = _ => mail.Task
        };
        var window = new WorkIqSignInWindow(null, setup, new FakeReadiness(), previewMode: true);
        try
        {
            PrepareMicrosoft(window);
            SetupStepIndicatorTests.Complete(window.CheckCopilotAsync());
            var connection = window.ConnectMicrosoftAsync();
            stages!.Report(WorkIqSignInStage.SigningIn);
            AssertState(window, "MicrosoftSignInIndicator", SetupStepState.WaitingForUser);
            TestAssert.True(Find<TextBlock>(window, "MicrosoftSignInStatus").Text.Contains("if prompted"),
                "Setup claimed to detect a Microsoft browser prompt.");
            stages.Report(WorkIqSignInStage.Consenting);
            AssertState(window, "MicrosoftSignInIndicator", SetupStepState.Succeeded);
            AssertState(window, "MicrosoftConsentIndicator", SetupStepState.WaitingForUser);
            stages.Report(WorkIqSignInStage.VerifyingAccount);
            AssertState(window, "MicrosoftConsentIndicator", SetupStepState.Succeeded);
            AssertState(window, "AccountVerifiedIndicator", SetupStepState.Running);
            login.SetResult(setup.VerifyAccountAsync("signin@tenant.example").GetAwaiter().GetResult());
            SetupStepIndicatorTests.DrainDispatcher();
            AssertState(window, "AccountVerifiedIndicator", SetupStepState.Succeeded);
            AssertState(window, "MailAccessIndicator", SetupStepState.Running);
            TestAssert.True(Find<Border>(window, "ConnectedCard").Visibility == Visibility.Visible &&
                !window.CanFinish && window.AuthenticatedIdentity is null, "Mail probing bypassed the Finish boundary.");
            stages.Report(WorkIqSignInStage.SigningIn);
            AssertState(window, "MicrosoftSignInIndicator", SetupStepState.Succeeded);
            mail.SetException(new WorkIqSetupException("Test denied Inbox permission"));
            SetupStepIndicatorTests.Complete(connection);
            AssertState(window, "MailAccessIndicator", SetupStepState.Failed);
            setup.MailOperation = _ => Task.CompletedTask;
            SetupStepIndicatorTests.Complete(window.RetryMailAsync());
            TestAssert.True(setup.SignIns == 1 && window.CanFinish, "Mailbox Retry required another sign-in.");
            window.ChangeAccount();
            login = new TaskCompletionSource<WorkIqAccountIdentity>();
            connection = window.ConnectMicrosoftAsync();
            stages!.Report(WorkIqSignInStage.SigningIn);
            window.CancelActiveOperation();
            Task.Run(() => stages.Report(WorkIqSignInStage.VerifyingAccount)).GetAwaiter().GetResult();
            login.SetResult(setup.VerifyAccountAsync("signin@tenant.example").GetAwaiter().GetResult());
            SetupStepIndicatorTests.Complete(connection);
            TestAssert.True(!window.CanFinish && window.AuthenticatedIdentity is null &&
                Find<Border>(window, "ConnectedCard").Visibility == Visibility.Collapsed,
                "Cancelled sign-in or late stage progress revived a candidate account.");
            login = new TaskCompletionSource<WorkIqAccountIdentity>();
            connection = window.ConnectMicrosoftAsync();
            login.SetResult(new("other@example.com", "other@example.com", "other@example.com")
            {
                PrincipalId = "different-principal", EulaAcceptedVersion = "test-workiq-1"
            });
            SetupStepIndicatorTests.Complete(connection);
            AssertState(window, "AccountVerifiedIndicator", SetupStepState.Failed);
            TestAssert.True(!window.CanFinish && setup.MailChecks == 2 &&
                Find<Border>(window, "ConnectedCard").Visibility == Visibility.Collapsed,
                "Email mismatch reached mailbox probing or became a connected account.");
        }
        finally { window.Close(); }
    }

    private static void TestInstallProgress()
    {
        var install = new TaskCompletionSource();
        var voice = new TaskCompletionSource();
        var voiceReady = false;
        var setup = new FakeWorkIq { InstallOperation = _ => install.Task };
        var readiness = new FakeReadiness();
        readiness.LocalOperation = (_, _) => Task.FromResult(readiness.ReadyLocal() with
        {
            WorkIq = new(SetupCheckState.ActionRequired, "Install WorkIQ")
        });
        var window = new WorkIqSignInWindow(null, setup, readiness, previewMode: true,
            installVoice: (_, _, _) => voice.Task, voiceInstalled: _ => voiceReady);
        try
        {
            SetupStepIndicatorTests.Complete(window.CheckLocalAsync());
            var operation = window.InstallWorkIqAsync();
            AssertState(window, "InstallationIndicator", SetupStepState.Running);
            install.SetException(new WorkIqSetupException("Test installation failed"));
            SetupStepIndicatorTests.Complete(operation);
            AssertState(window, "InstallationIndicator", SetupStepState.Failed);
            TestAssert.True(Find<Button>(window, "InstallButton").IsEnabled, "Failed WorkIQ installation cannot be retried.");
            install = new TaskCompletionSource();
            operation = window.InstallWorkIqAsync();
            readiness.LocalOperation = null;
            install.SetResult();
            SetupStepIndicatorTests.Complete(operation);
            AssertState(window, "InstallationIndicator", SetupStepState.Succeeded);
            PrepareMicrosoft(window);
            SetupStepIndicatorTests.Complete(window.CheckCopilotAsync());
            SetupStepIndicatorTests.Complete(window.ConnectMicrosoftAsync());
            operation = window.InstallVoiceModelsAsync();
            AssertState(window, "VoiceModelsIndicator", SetupStepState.Running);
            voice.SetException(new IOException("Test voice download failed"));
            SetupStepIndicatorTests.Complete(operation);
            AssertState(window, "VoiceModelsIndicator", SetupStepState.Failed);
            TestAssert.True(window.CanFinish, "An optional voice failure blocked Finish.");
            voice = new TaskCompletionSource();
            operation = window.InstallVoiceModelsAsync();
            window.CancelActiveOperation();
            AssertState(window, "VoiceModelsIndicator", SetupStepState.Idle);
            voice.SetResult();
            SetupStepIndicatorTests.Complete(operation);
            TestAssert.True(window.CanFinish, "Cancelling optional voice blocked Finish.");
            voice = new TaskCompletionSource();
            operation = window.InstallVoiceModelsAsync();
            voiceReady = true;
            voice.SetResult();
            SetupStepIndicatorTests.Complete(operation);
            AssertState(window, "VoiceModelsIndicator", SetupStepState.Succeeded);
        }
        finally { window.Close(); }
    }

    private static void PrepareMicrosoft(WorkIqSignInWindow window)
    {
        SetupStepIndicatorTests.Complete(window.CheckLocalAsync());
        Find<TextBox>(window, "AccountText").Text = "signin@tenant.example";
        Find<CheckBox>(window, "EulaCheck").IsChecked = true;
    }

    private static void AssertState(Window window, string name, SetupStepState expected)
    {
        var indicator = Find<SetupStepIndicator>(window, name);
        TestAssert.True(indicator.State == expected, $"{name}: expected {expected}, got {indicator.State}.");
        if (expected != SetupStepState.Running)
            TestAssert.True(!indicator.IsAnimating, $"{name} retained its animation after {expected}.");
    }

    private static void AssertDeviceCodeCleared(Window window)
        => TestAssert.True(Find<TextBox>(window, "GitHubDeviceCodeText").Text.Length == 0 &&
            Find<Border>(window, "GitHubDeviceCodeCard").Visibility == Visibility.Collapsed &&
            !Find<Button>(window, "CopyGitHubCodeButton").IsEnabled &&
            !Find<Button>(window, "OpenGitHubButton").IsEnabled, "An inactive GitHub challenge was not cleared.");

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement
        => (T)(root.FindName(name) ?? throw new InvalidOperationException($"Missing setup control: {name}"));

    private sealed class FakeReadiness : ISetupReadinessService
    {
        public int AccessChecks { get; private set; }
        public string Version { get; set; } = "test-workiq-1";
        public Func<CancellationToken, IProgress<SetupLocalCheckUpdate>?, Task<SetupLocalReadiness>>? LocalOperation { get; set; }
        public Func<CancellationToken, IProgress<CopilotDeviceCodeChallenge>?, Task>? SignInOperation { get; set; }
        public Func<CancellationToken, Task<SetupCheck>>? AccessOperation { get; set; }
        public SetupLocalReadiness ReadyLocal()
        {
            var ready = new SetupCheck(SetupCheckState.Ready, "Available", Version: "1.0.81");
            return new SetupLocalReadiness(ready, ready, ready, ready, ready with { Version = Version });
        }
        public Task<SetupLocalReadiness> CheckLocalAsync(CancellationToken ct, IProgress<SetupLocalCheckUpdate>? progress = null)
        {
            if (LocalOperation is not null) return LocalOperation(ct, progress);
            return Task.FromResult(ReadyLocal());
        }
        public Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct)
        {
            AccessChecks++;
            if (AccessOperation is not null) return AccessOperation(ct);
            return Task.FromResult(new SetupCheck(SetupCheckState.Ready, "Catalog available"));
        }
        public Task SignInCopilotAsync(CancellationToken ct, IProgress<CopilotDeviceCodeChallenge>? progress = null)
            => SignInOperation?.Invoke(ct, progress) ?? Task.CompletedTask;
    }

    private sealed class FakeWorkIq : IWorkIqSetupService
    {
        public int SignIns { get; private set; }
        public int Installs { get; private set; }
        public int MailChecks { get; private set; }
        public bool FailMail { get; set; }
        public string? AcceptedVersion { get; private set; }
        public string PrincipalId { get; set; } = "verified-principal";
        public Exception? VersionFailure { get; set; }
        public Func<CancellationToken, Task<string>>? VersionOperation { get; set; }
        public Action<CancellationToken>? OnSignIn { get; set; }
        public Func<string, CancellationToken, IProgress<WorkIqSignInStage>?, Task<WorkIqAccountIdentity>>? SignInOperation { get; set; }
        public Func<CancellationToken, Task>? MailOperation { get; set; }
        public Func<CancellationToken, Task>? InstallOperation { get; set; }
        public bool TryGetExecutablePath(out string path) { path = "fake-workiq.exe"; return true; }
        public Task<string> GetVersionAsync(CancellationToken ct = default)
            => VersionOperation?.Invoke(ct) ??
                (VersionFailure is null ? Task.FromResult("test-workiq-1") : Task.FromException<string>(VersionFailure));
        public Task InstallAsync(CancellationToken ct = default)
        {
            Installs++;
            return InstallOperation?.Invoke(ct) ?? Task.CompletedTask;
        }
        public Task<WorkIqAccountIdentity> VerifyAccountAsync(string account, CancellationToken ct = default)
            => Task.FromResult(new WorkIqAccountIdentity(account, "mailbox@example.com", "signin@tenant.example")
            {
                PrincipalId = PrincipalId,
                DisplayName = "Verified Person",
                EulaAcceptedVersion = "test-workiq-1"
            });
        public Task<WorkIqAccountIdentity> SignInAsync(
            string account, string acceptedEulaVersion, CancellationToken ct = default,
            IProgress<WorkIqSignInStage>? progress = null)
        {
            SignIns++;
            AcceptedVersion = acceptedEulaVersion;
            if (SignInOperation is not null) return SignInOperation(account, ct, progress);
            OnSignIn?.Invoke(ct);
            return VerifyAccountAsync(account, ct);
        }
        public Task ProbeInboxAccessAsync(WorkIqAccountIdentity identity, CancellationToken ct = default)
        {
            MailChecks++;
            if (MailOperation is not null) return MailOperation(ct);
            return FailMail ? Task.FromException(new WorkIqSetupException("Test mailbox unavailable"))
                : Task.CompletedTask;
        }
    }
}
