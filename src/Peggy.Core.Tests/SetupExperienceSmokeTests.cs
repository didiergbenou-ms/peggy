using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class SetupExperienceSmokeTests
{
    public static async Task RunAsync()
    {
        TestEmailValidation();
        await TestReadinessAsync();
        await TestPinnedInboxProbeAsync();
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

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement
        => (T)(root.FindName(name) ?? throw new InvalidOperationException($"Missing setup control: {name}"));

    private sealed class FakeReadiness : ISetupReadinessService
    {
        public int AccessChecks { get; private set; }
        public string Version { get; set; } = "test-workiq-1";
        public Task<SetupLocalReadiness> CheckLocalAsync(CancellationToken ct)
        {
            var ready = new SetupCheck(SetupCheckState.Ready, "Available", Version: "1.0.81");
            return Task.FromResult(new SetupLocalReadiness(ready, ready, ready, ready,
                ready with { Version = Version }));
        }
        public Task<SetupCheck> CheckCopilotAccessAsync(CancellationToken ct)
        {
            AccessChecks++;
            return Task.FromResult(new SetupCheck(SetupCheckState.Ready, "Catalog available"));
        }
        public Task SignInCopilotAsync(CancellationToken ct) => Task.CompletedTask;
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
        public Action<CancellationToken>? OnSignIn { get; set; }
        public bool TryGetExecutablePath(out string path) { path = "fake-workiq.exe"; return true; }
        public Task<string> GetVersionAsync(CancellationToken ct = default)
            => VersionFailure is null ? Task.FromResult("test-workiq-1") : Task.FromException<string>(VersionFailure);
        public Task InstallAsync(CancellationToken ct = default) { Installs++; return Task.CompletedTask; }
        public Task<WorkIqAccountIdentity> VerifyAccountAsync(string account, CancellationToken ct = default)
            => Task.FromResult(new WorkIqAccountIdentity(account, "mailbox@example.com", "signin@tenant.example")
            {
                PrincipalId = PrincipalId,
                DisplayName = "Verified Person",
                EulaAcceptedVersion = "test-workiq-1"
            });
        public Task<WorkIqAccountIdentity> SignInAsync(
            string account, string acceptedEulaVersion, CancellationToken ct = default)
        {
            SignIns++;
            AcceptedVersion = acceptedEulaVersion;
            OnSignIn?.Invoke(ct);
            return VerifyAccountAsync(account, ct);
        }
        public Task ProbeInboxAccessAsync(WorkIqAccountIdentity identity, CancellationToken ct = default)
        {
            MailChecks++;
            return FailMail ? Task.FromException(new WorkIqSetupException("Test mailbox unavailable"))
                : Task.CompletedTask;
        }
    }
}
