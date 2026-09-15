using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Tray;

namespace Peggy.Core.Tests;

internal static class DesktopSmokeTests
{
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunOnStaThread();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunOnStaThread()
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(
                Dispatcher.CurrentDispatcher));
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        try
        {
            TestAlertCenter();
            TestAlertContextWindow();
            TestConversationWindow();
            TestImmediateListeningWithoutGreeting();
            TestDeferredConversationApproval();
            TestWorkIqSignInWindow();
            TestSettingsWindow();
            TestSettingsPreserveUnavailableWatchlist();
            TestMascotAndBubble();
            TestLocalSpeech();
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void TestAlertCenter()
    {
        var now = DateTimeOffset.UtcNow;
        var alert = new AlertRecord
        {
            ThreadId = "thread",
            Source = MessageSource.Teams,
            Tier = AlertTier.Attention,
            Summary = "Customer response needs review",
            Reason = "The response has a near deadline.",
            FirstSeenUtc = now,
            LastActivityUtc = now,
            UpdatedUtc = now
        };
        var window = new AlertCenterWindow();
        TestAssert.True(
            window.Width == 620 &&
            window.Height == 780 &&
            !Find<Button>(window, "DismissAllButton").IsEnabled,
            "The polished Alert Center size or empty action state regressed.");
        window.UpdateAlerts([alert], maskContent: false);
        TestAssert.Equal(
            "1 active, 0 awareness",
            Find<TextBlock>(window, "StatusText").Text,
            "Alert Center status counts are incorrect.");
        TestAssert.True(
            Find<Button>(window, "DismissAllButton").IsEnabled,
            "Dismiss all was not enabled for an active alert.");
        TestAssert.True(
            Descendants<TextBlock>(window).Any(text =>
                text.Text == alert.Summary),
            "Alert Center omitted an unmasked alert summary.");
        TestAssert.True(
            Descendants<TextBlock>(window).Any(text =>
                text.Text == "Why Peggy flagged this") &&
            !Descendants<Button>(window).Any(button =>
                button.Content?.ToString()?.StartsWith(
                    "Find in ",
                    StringComparison.Ordinal) == true),
            "Alert Center did not explain or identify the alert source.");
        alert.SourceReference =
            "https://teams.microsoft.com/l/message/thread/linked";
        window.UpdateAlerts([alert], maskContent: false);
        TestAssert.True(
            FindButton(window, "Open in Teams").Visibility ==
                Visibility.Visible,
            "Alert Center did not expose a recovered Teams link.");

        window.UpdateAlerts([alert], maskContent: true);
        TestAssert.True(
            !Descendants<TextBlock>(window).Any(text =>
                text.Text == alert.Summary) &&
            Descendants<TextBlock>(window).Any(text =>
                text.Text.Contains(
                    "details are hidden",
                    StringComparison.Ordinal)),
            "Alert Center privacy masking failed.");

        window.SetMonitoringInterrupted(
            true,
            3,
            now.AddMinutes(-5));
        TestAssert.Equal(
            Visibility.Visible,
            Find<Border>(window, "HealthBanner").Visibility,
            "Monitoring interruption health UI did not appear.");
        TestAssert.Equal(
            "Monitoring interrupted",
            Find<TextBlock>(window, "HealthTitle").Text,
            "A real monitoring failure was not labeled as interrupted.");
        window.SetMonitoringInterrupted(false, 0, now);
        TestAssert.Equal(
            Visibility.Collapsed,
            Find<Border>(window, "HealthBanner").Visibility,
            "Monitoring health UI did not clear.");
        window.SetMonitoringInterrupted(
            false,
            1,
            null,
            "Run 'workiq accept-eula'.");
        TestAssert.True(
            Find<Border>(window, "HealthBanner").Visibility ==
                Visibility.Visible &&
            Find<TextBlock>(window, "HealthTitle").Text ==
                "Monitoring notice" &&
            Find<TextBlock>(window, "HealthDetail").Text.Contains(
                "accept-eula",
                StringComparison.Ordinal),
            "A nonfatal WorkIQ notice was mislabeled or not surfaced.");
        window.SetMonitoringInterrupted(false, 0, now);

        window.ShowCriticalBanner(alert, maskContent: false);
        TestAssert.Equal(
            alert.Summary,
            Find<TextBlock>(window, "CriticalText").Text,
            "Critical alert banner omitted its summary.");
        window.UpdateAlerts([], maskContent: false);
        TestAssert.True(
            Find<Border>(window, "CriticalBanner").Visibility ==
                Visibility.Collapsed &&
            Find<TextBlock>(window, "StatusText").Text ==
                "No active alerts" &&
            Find<TextBlock>(window, "ListCaptionText").Text ==
                "Peggy is still watching" &&
            Descendants<TextBlock>(window).Any(text =>
                text.Text == "You're all caught up") &&
            !Find<Button>(window, "DismissAllButton").IsEnabled,
            "An empty Alert Center retained stale critical or action state.");
        AssertDecorativeTextIsRaw(window, "Alert Center");
        window.ShowCriticalBanner(alert, maskContent: false);
        window.ClearCriticalBanner();
        TestAssert.Equal(
            Visibility.Collapsed,
            Find<Border>(window, "CriticalBanner").Visibility,
            "Critical alert banner did not clear.");
        window.UpdateAlerts([alert], maskContent: true);

        string? resolved = null;
        string? dismissed = null;
        var dismissedAll = false;
        string? snoozed = null;
        string? chatted = null;
        string? contextRequested = null;
        var feedback = new List<FeedbackKind>();
        AlertTier? correctedTier = null;
        var retried = false;
        var revealRequested = false;
        string? switchedCritical = null;
        window.ResolveRequested += id => resolved = id;
        window.DismissRequested += id => dismissed = id;
        window.DismissAllRequested += () => dismissedAll = true;
        window.SnoozeRequested += id => snoozed = id;
        window.ChatRequested += id => chatted = id;
        window.ContextRequested += id => contextRequested = id;
        window.FeedbackRequested += (_, kind) => feedback.Add(kind);
        window.TierChanged += (_, tier) => correctedTier = tier;
        window.RetryRequested += () => retried = true;
        window.RevealRequested += () => revealRequested = true;
        window.CriticalSwitchRequested += id => switchedCritical = id;
        FindButton(window, "Resolve").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Dismiss").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        Find<Button>(window, "DismissAllButton").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Snooze 1 hour").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Talk to Peggy").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Open in Teams").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Reveal details").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Less like this").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Always alert").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        Descendants<ComboBox>(window).Single().SelectedItem =
            AlertTier.Critical;
        FindButton(window, "Retry now").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        window.ShowCriticalBanner(alert, maskContent: false);
        FindButton(window, "Switch to alert").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        TestAssert.Equal(
            alert.Id,
            resolved,
            "Alert Center resolve wiring failed.");
        TestAssert.Equal(
            alert.Id,
            chatted,
            "Alert Center conversation wiring failed.");
        TestAssert.True(
            dismissed == alert.Id &&
            dismissedAll &&
            snoozed == alert.Id &&
            feedback.SequenceEqual(
                [
                    FeedbackKind.LessLikeThis,
                    FeedbackKind.AlwaysAlert
                ]) &&
            correctedTier == AlertTier.Critical &&
            retried &&
            revealRequested &&
            switchedCritical == alert.Id &&
            contextRequested == alert.Id,
            "One or more Alert Center actions were not wired.");
        window.ShowNearTaskbar();
        var workArea = SystemParameters.WorkArea;
        TestAssert.True(
            window.Left >= workArea.Left + 8 &&
            window.Top >= workArea.Top + 8 &&
            window.Width <= Math.Max(360, workArea.Width - 16) &&
            window.Height <= Math.Max(320, workArea.Height - 16),
            "The responsive Alert Center exceeded the available work area.");
        window.Hide();
    }

    private static void TestAlertContextWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var alert = new AlertRecord
        {
            ThreadId = "thread",
            Source = MessageSource.Email,
            Tier = AlertTier.Attention,
            Summary = "Customer response needs review",
            Reason = "The response has a near deadline.",
            FirstSeenUtc = now.AddMinutes(-3),
            LastActivityUtc = now,
            UpdatedUtc = now
        };
        var older = new Message(
            "older",
            MessageSource.Email,
            "thread",
            "First sender",
            "Customer response",
            "First message in the thread.",
            now.AddMinutes(-2),
            true);
        var newer = new Message(
            "newer",
            MessageSource.Email,
            "thread",
            "Second sender",
            "Customer response",
            "Most recent message in the thread.",
            now,
            false)
        {
            WebUrl =
                "https://outlook.office.com/mail/deeplink/read/newer"
        };
        var thread = new MessageThread(
            "thread",
            MessageSource.Email,
            "Customer response",
            [newer, older]);
        var window = new AlertContextWindow(
            alert,
            thread,
            canOpenOriginal: true);
        AssertDecorativeTextIsRaw(window, "alert context");

        TestAssert.Equal(
            alert.Summary,
            Find<TextBlock>(window, "SummaryText").Text,
            "Alert context omitted the saved summary.");
        TestAssert.Equal(
            "Why Peggy alerted you",
            Find<TextBlock>(window, "ReasonHeading").Text,
            "Alert context did not use the selected assistant name.");
        var texts = Descendants<TextBlock>(window)
            .Select(text => text.Text)
            .ToList();
        TestAssert.True(
            texts.IndexOf(older.Preview) >= 0 &&
            texts.IndexOf(newer.Preview) > texts.IndexOf(older.Preview),
            "Alert context did not render messages chronologically.");
        var openRequested = false;
        window.OpenOriginalRequested += () => openRequested = true;
        FindButton(window, "Open in Outlook").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        TestAssert.True(
            openRequested &&
            Find<Button>(window, "OpenOriginalButton").Visibility ==
                Visibility.Visible,
            "Alert context did not expose its original-item action.");
        var sourceLinkType = typeof(AlertManager).Assembly.GetType(
            "Peggy.Tray.AlertSourceLink")
            ?? throw new InvalidOperationException(
                "Alert source link policy was not found.");
        var tryCreate = sourceLinkType.GetMethod(
            "TryCreate",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Alert source link validation was not found.");
        var scopeOutlookMailbox = sourceLinkType.GetMethod(
            "ScopeOutlookMailbox",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Outlook mailbox link scoping was not found.");
        var trustedArguments = new object?[]
        {
            "https://teams.microsoft.com/l/message/thread/message",
            null
        };
        var untrustedArguments = new object?[]
        {
            "https://example.com/not-an-alert-source",
            null
        };
        var genericChatArguments = new object?[]
        {
            "https://teams.microsoft.com/l/chat/old-chat/conversations",
            null
        };
        var malformedOutlookArguments = new object?[]
        {
            "https://https://outlook.office.com/owa/person%40example.com/" +
            "?ItemID=AAMkRegional%3D",
            null
        };
        var nestedOutlookItemArguments = new object?[]
        {
            "https://outlook.office.com/owa/?ItemID=" +
            "https%3A%2F%2Foutlook.office.com%2Fowa%2F",
            null
        };
        var doubleEncodedNestedOutlookItemArguments = new object?[]
        {
            "https://outlook.office.com/owa/?ItemID=" +
            "https%253A%252F%252Fevil.example%252Fx",
            null
        };
        var regionalOutlookArguments = new object?[]
        {
            "https://emea.mail.microsoft.com/owa/?ItemID=" +
            "AAMkRegional%3D&exvsurl=1&viewmodel=ReadMessageItem",
            null
        };
        TestAssert.True(
            (bool)(tryCreate.Invoke(null, trustedArguments) ?? false) &&
            !(bool)(tryCreate.Invoke(null, untrustedArguments) ?? true) &&
            !(bool)(tryCreate.Invoke(null, genericChatArguments) ?? true) &&
            !(bool)(tryCreate.Invoke(null, malformedOutlookArguments) ?? true) &&
            !(bool)(tryCreate.Invoke(null, nestedOutlookItemArguments) ?? true) &&
            !(bool)(tryCreate.Invoke(
                null,
                doubleEncodedNestedOutlookItemArguments) ?? true) &&
            (bool)(tryCreate.Invoke(null, regionalOutlookArguments) ?? false) &&
            regionalOutlookArguments[1] is Uri regionalOutlookUri &&
            regionalOutlookUri.Host == "outlook.office.com" &&
            regionalOutlookUri.Query.Contains(
                "ItemID=AAMkRegional%3D",
                StringComparison.Ordinal),
            "Alert context accepted a non-Microsoft source link.");
        var scopedOutlookUri = (Uri?)scopeOutlookMailbox.Invoke(
            null,
            [
                regionalOutlookArguments[1],
                "person@example.com"
            ]);
        var unchangedTeamsUri = (Uri?)scopeOutlookMailbox.Invoke(
            null,
            [
                trustedArguments[1],
                "person@example.com"
            ]);
        TestAssert.True(
            scopedOutlookUri is not null &&
            scopedOutlookUri.AbsolutePath ==
                "/mail/person%40example.com/deeplink/read/AAMkRegional%3D" &&
            scopedOutlookUri.Query.Length == 0 &&
            !scopedOutlookUri.AbsoluteUri.Contains(
                "https://https://",
                StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(unchangedTeamsUri, trustedArguments[1]),
            "Outlook links were not rebuilt as one canonical message path or " +
            "a Teams link was modified.");
        var encodedOutlookUri = (Uri?)scopeOutlookMailbox.Invoke(
            null,
            [
                new Uri(
                    "https://outlook.office.com/owa/?ItemID=" +
                    "AAMkValue%2Bwith%2Fcharacters%3D"),
                "person@example.com"
            ]);
        TestAssert.True(
            encodedOutlookUri?.AbsolutePath.EndsWith(
                "/AAMkValue%2Bwith%2Fcharacters%3D",
                StringComparison.Ordinal) == true &&
            !encodedOutlookUri.AbsoluteUri.Contains(
                "%252",
                StringComparison.OrdinalIgnoreCase),
            "Outlook item identity was double encoded.");

        var savedOnly = new AlertContextWindow(
            alert,
            thread: null,
            canOpenOriginal: false);
        TestAssert.True(
            Find<TextBlock>(savedOnly, "ContextStatus").Visibility ==
                Visibility.Visible &&
            Find<Button>(
                savedOnly,
                "OpenOriginalButton").Visibility == Visibility.Collapsed,
            "Saved-only alert context did not explain the missing thread.");
        var shouldMask = false;
        var privacyWindow = new AlertContextWindow(
            alert,
            thread,
            canOpenOriginal: false,
            () => shouldMask);
        privacyWindow.Show();
        shouldMask = true;
        WaitWithDispatcher(Task.Delay(TimeSpan.FromMilliseconds(1200)));
        TestAssert.True(
            !privacyWindow.IsVisible,
            "An open alert context stayed visible after privacy masking began.");
        window.Close();
        savedOnly.Close();
    }

    private static void TestConversationWindow()
    {
        var window = new ConversationWindow();
        AssertDecorativeTextIsRaw(window, "conversation");
        TestAssert.True(
            AutomationProperties.GetName(
                Find<TextBox>(window, "TranscriptReviewText")) ==
                "Transcript review" &&
            AutomationProperties.GetName(
                Find<TextBox>(window, "TypedConfirmation")) ==
                "Typed destructive action confirmation",
            "Conversation inputs do not have accessible names.");
        window.BeginConversation();
        TestAssert.Equal(
            "Ready",
            Find<TextBlock>(window, "StatusText").Text,
            "Conversation window did not enter its ready state.");
        window.AppendUser("Hello");
        window.AppendAssistant("Hi");
        TestAssert.Equal(
            2,
            Find<StackPanel>(window, "TranscriptPanel").Children.Count,
            "Conversation transcript did not append both speakers.");
        window.SetListening();
        TestAssert.Equal(
            "Listening locally...",
            Find<TextBlock>(window, "StatusText").Text,
            "Conversation listening state failed.");
        window.ShowPartialTranscript("live partial words");
        TestAssert.True(
            Find<TextBox>(
                window,
                "TranscriptReviewText").Text ==
                "live partial words" &&
            Find<TextBox>(
                window,
                "TranscriptReviewText").IsReadOnly &&
            Find<Button>(
                window,
                "UseTranscriptButton").Visibility ==
                Visibility.Collapsed,
            "Live partial transcription was not shown locally.");
        window.SetThinking();
        TestAssert.Equal(
            "Thinking...",
            Find<TextBlock>(window, "StatusText").Text,
            "Conversation thinking state failed.");
        window.SetSpeaking();
        TestAssert.Equal(
            "Peggy is speaking",
            Find<TextBlock>(window, "StatusText").Text,
            "Conversation speaking state failed.");
        window.SetReady();

        var automaticTask = window.ReviewTranscriptAsync(
            "automatic words",
            autoSubmit: true,
            ct: CancellationToken.None);
        WaitWithDispatcher(
            Task.WhenAny(
                automaticTask,
                Task.Delay(TimeSpan.FromSeconds(3))));
        TestAssert.True(
            automaticTask.IsCompletedSuccessfully &&
            automaticTask.Result.Text == "automatic words",
            "Ordinary speech did not auto-send after one second.");

        var editedTask = window.ReviewTranscriptAsync(
            "uncorrected words",
            autoSubmit: true,
            ct: CancellationToken.None);
        TestAssert.Equal(
            Visibility.Visible,
            Find<Border>(window, "TranscriptReviewCard").Visibility,
            "Recognized speech did not show an editable local preview.");
        Find<TextBox>(window, "TranscriptReviewText").Text =
            "corrected words";
        WaitWithDispatcher(Task.Delay(TimeSpan.FromMilliseconds(1200)));
        TestAssert.True(
            !editedTask.IsCompleted &&
            Find<TextBlock>(
                window,
                "TranscriptReviewCountdown").Text.Contains(
                    "paused",
                    StringComparison.OrdinalIgnoreCase),
            "Editing the transcript did not pause auto-send.");
        FindButton(window, "Send now").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        WaitWithDispatcher(editedTask);
        TestAssert.True(
            editedTask.Result is
            {
                Text: "corrected words",
                RetryRequested: false
            } &&
            Find<Border>(
                window,
                "TranscriptReviewCard").Visibility == Visibility.Collapsed,
            "Edited speech was not accepted from the transcript preview.");

        var retryTask = window.ReviewTranscriptAsync(
            "try this again",
            autoSubmit: true,
            ct: CancellationToken.None);
        FindButton(window, "Cancel auto-send").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        WaitWithDispatcher(Task.Delay(TimeSpan.FromMilliseconds(1200)));
        TestAssert.True(
            !retryTask.IsCompleted,
            "Cancelling auto-send submitted the transcript.");
        FindButton(window, "Try again").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        WaitWithDispatcher(retryTask);
        TestAssert.True(
            retryTask.Result.RetryRequested,
            "The transcript preview could not request another local recording.");

        var approvalReview = window.ReviewTranscriptAsync(
            "yes",
            autoSubmit: false,
            ct: CancellationToken.None);
        WaitWithDispatcher(Task.Delay(TimeSpan.FromMilliseconds(1200)));
        TestAssert.True(
            !approvalReview.IsCompleted &&
            Find<Button>(
                window,
                "CancelTranscriptButton").Visibility ==
                    Visibility.Collapsed,
            "An action-approval transcript auto-sent without review.");
        FindButton(window, "Use transcript").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        WaitWithDispatcher(approvalReview);
        window.SetReady();

        var action = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendEmail,
                Subject = "Status",
                Body = "Exact body",
                Recipients = ["person@example.com"]
            });
        window.ShowAction(action);
        TestAssert.Equal(
            Visibility.Visible,
            Find<Border>(window, "ActionCard").Visibility,
            "Conversation action preview did not appear.");
        TestAssert.Equal(
            action.ExactDescription,
            Find<TextBlock>(window, "ActionDescription").Text,
            "Conversation action preview changed the exact content.");
        var longAction = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendEmail,
                Subject = "Long preview",
                Body = new string('x', 10_000),
                Recipients = ["person@example.com"]
            });
        window.ShowAction(longAction);
        TestAssert.True(
            Descendants<ScrollViewer>(
                    Find<Border>(window, "ActionCard"))
                .Any(scroll => scroll.MaxHeight == 220) &&
            FindButton(window, "Approve").IsVisible,
            "A long action preview hid its approval controls.");
        window.ShowAction(action);

        var destructive = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteMessage,
                TargetId = "message"
            });
        window.ShowAction(destructive);
        window.RequireTypedConfirmation(destructive);
        TestAssert.Contains(
            destructive.TypedConfirmationPhrase,
            Find<TextBlock>(window, "TypedInstruction").Text,
            "Destructive confirmation omitted the exact phrase.");
        window.HideAction();
        TestAssert.Equal(
            Visibility.Collapsed,
            Find<Border>(window, "ActionCard").Visibility,
            "Conversation action preview did not clear.");

        var alert = new AlertRecord
        {
            Tier = AlertTier.Critical,
            Summary = "Critical private detail"
        };
        window.ShowCritical(alert, maskContent: true);
        TestAssert.True(
            Find<TextBlock>(window, "CriticalText").Text != alert.Summary,
            "Conversation critical banner ignored privacy masking.");
        window.EndConversation();
        TestAssert.Equal(
            Visibility.Collapsed,
            Find<Border>(window, "CriticalBanner").Visibility,
            "Ending a conversation left a stale critical banner.");
        window.ShowCritical(alert, maskContent: false);
        window.BeginConversation();
        TestAssert.Equal(
            Visibility.Collapsed,
            Find<Border>(window, "CriticalBanner").Visibility,
            "Starting a conversation retained a prior critical banner.");
        window.ShowCritical(alert, maskContent: true);

        var microphoneRequested = false;
        var ended = false;
        var approved = false;
        var cancelled = false;
        string? typedConfirmation = null;
        string? switchedCritical = null;
        window.MicrophoneRequested += () => microphoneRequested = true;
        window.ConversationClosed += () => ended = true;
        window.ApproveRequested += () => approved = true;
        window.CancelActionRequested += () => cancelled = true;
        window.TypedConfirmationSubmitted += value =>
            typedConfirmation = value;
        window.CriticalSwitchRequested += id =>
            switchedCritical = id;
        window.ShowAction(action);
        FindButton(window, "Approve").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Cancel").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        window.ShowAction(destructive);
        window.RequireTypedConfirmation(destructive);
        Find<TextBox>(window, "TypedConfirmation").Text =
            destructive.TypedConfirmationPhrase;
        FindButton(window, "Confirm destructive action").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Switch").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "Listen").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        FindButton(window, "End conversation").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        TestAssert.True(
            microphoneRequested &&
            ended &&
            approved &&
            cancelled &&
            typedConfirmation == destructive.TypedConfirmationPhrase &&
            switchedCritical == alert.Id,
            "Conversation controls were not wired.");
    }

    private static void TestImmediateListeningWithoutGreeting()
    {
        var window = new ConversationWindow();
        var mascot = new MascotWindow();
        var state = new PeggyStateManager(
            new EncryptedStateStore(
                Path.Combine(
                    Path.GetTempPath(),
                    $"peggy-listening-{Guid.NewGuid():N}.dat")));
        var speech = new SilentSpeechProvider();
        var controller = new ConversationController(
            window,
            mascot,
            state,
            new AlertManager(state),
            () => throw new InvalidOperationException(
                "Copilot should not run before speech is captured."),
            () => new MockMessageProvider(),
            _ => Array.Empty<Message>(),
            () => false,
            CancellationToken.None,
            () => speech,
            () => mascot.SetState(PeggyState.Sleeping));

        WaitWithDispatcher(controller.StartAsync());
        WaitWithDispatcher(Task.Delay(100));
        TestAssert.True(
            speech.Spoken.Count == 0 &&
            Find<StackPanel>(window, "TranscriptPanel").Children.Count == 0 &&
            mascot.CurrentState == PeggyState.Sleeping,
            "Starting a general conversation still played a delayed greeting.");

        controller.EndConversation();
        WaitWithDispatcher(controller.DisposeAsync().AsTask());
        window.Close();
        mascot.Close();
    }

    private static void TestDeferredConversationApproval()
    {
        var window = new ConversationWindow();
        var mascot = new MascotWindow();
        var state = new PeggyStateManager(
            new EncryptedStateStore(
                Path.Combine(
                    Path.GetTempPath(),
                    $"peggy-controller-{Guid.NewGuid():N}.dat")));
        var controller = new ConversationController(
            window,
            mascot,
            state,
            new AlertManager(state),
            () => throw new InvalidOperationException(
                "Copilot should not run in the controller state test."),
            () => new MockMessageProvider(),
            _ => Array.Empty<Message>(),
            () => false,
            CancellationToken.None);
        var conversationCts = new CancellationTokenSource();
        SetPrivateField(
            controller,
            "_conversationCts",
            conversationCts);
        using var interruptedTurn = new CancellationTokenSource();
        SetPrivateField(
            controller,
            "_activeOperationCts",
            interruptedTurn);
        SetPrivateField(controller, "_turnInProgress", true);
        InvokeInstancePrivate(
            controller,
            "OnMicrophoneRequested");
        TestAssert.True(
            interruptedTurn.IsCancellationRequested &&
            GetPrivateField<bool>(
                controller,
                "_listenAfterInterrupt"),
            "Barge-in did not cancel the current turn and request listening.");
        SetPrivateField(controller, "_activeOperationCts", null);
        SetPrivateField(controller, "_listenAfterInterrupt", false);
        SetPrivateField(controller, "_userInterruptedTurn", false);
        SetPrivateField(controller, "_turnInProgress", false);
        SetPrivateField(
            controller,
            "_pendingAction",
            WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendEmail,
                    Subject = "Deferred",
                    Body = "Approve while listening",
                    Recipients = ["person@example.com"]
                }));
        TestAssert.True(
            InvokeInstancePrivate<bool>(
                controller,
                "ShouldAutoSubmitTranscript"),
            "An ordinary transcript did not allow auto-send.");
        SetPrivateField(controller, "_voiceApprovalEligible", true);
        TestAssert.True(
            !InvokeInstancePrivate<bool>(
                controller,
                "ShouldAutoSubmitTranscript"),
            "An action-approval transcript was allowed to auto-send.");
        SetPrivateField(controller, "_voiceApprovalEligible", false);
        SetPrivateField(controller, "_turnInProgress", true);
        InvokeInstancePrivate(
            controller,
            "OnClickApprove");
        TestAssert.Equal(
            ApprovalMethod.Click,
            GetPrivateField<ApprovalMethod?>(
                controller,
                "_deferredApproval"),
            "Click approval was ignored during the listening turn.");
        var approvedActionId = GetPrivateField<WorkActionRequest>(
            controller,
            "_pendingAction").Id;
        TestAssert.Equal(
            approvedActionId,
            GetPrivateField<string>(
                controller,
                "_deferredActionId"),
            "Deferred click approval was not bound to its action.");
        SetPrivateField(
            controller,
            "_pendingAction",
            WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.CreateTask,
                    Subject = "Replacement action"
                }));
        TestAssert.Equal(
            null,
            InvokeInstancePrivate<ApprovalMethod?>(
                controller,
                "TakeDeferredApproval"),
            "Deferred approval executed a replacement action.");

        var destructive = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.DeleteMessage,
                TargetId = "message"
            });
        SetPrivateField(
            controller,
            "_pendingAction",
            destructive);
        SetPrivateField(
            controller,
            "_deferredApproval",
            null);
        SetPrivateField(
            controller,
            "_deferredActionId",
            null);
        InvokeInstancePrivate(
            controller,
            "OnTypedConfirmation",
            "WRONG PHRASE");
        TestAssert.Equal(
            null,
            GetPrivateField<ApprovalMethod?>(
                controller,
                "_deferredApproval"),
            "An incorrect typed phrase deferred destructive approval.");
        InvokeInstancePrivate(
            controller,
            "OnTypedConfirmation",
            destructive.TypedConfirmationPhrase);
        TestAssert.Equal(
            ApprovalMethod.TypedConfirmation,
            GetPrivateField<ApprovalMethod?>(
                controller,
                "_deferredApproval"),
            "Typed approval was ignored while the prior turn was ending.");
        TestAssert.Equal(
            destructive.Id,
            GetPrivateField<string>(
                controller,
                "_deferredActionId"),
            "Deferred typed approval was not bound to its action.");

        using var switchCancellation = new CancellationTokenSource();
        var staleAction = WorkActionPolicy.NormalizeAndValidate(
            new WorkActionRequest
            {
                Kind = WorkActionKind.SendEmail,
                Subject = "Stale action",
                Body = "Must not survive alert switching",
                Recipients = ["person@example.com"]
            });
        var staleTurn = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    switchCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            SetPrivateField(controller, "_pendingAction", staleAction);
            SetPrivateField(controller, "_voiceApprovalEligible", true);
            SetPrivateField(
                controller,
                "_deferredApproval",
                ApprovalMethod.Voice);
            SetPrivateField(
                controller,
                "_deferredActionId",
                staleAction.Id);
        });
        SetPrivateField(controller, "_activeOperationCts", switchCancellation);
        SetPrivateField(controller, "_activeTurn", staleTurn);
        SetPrivateField(controller, "_turnInProgress", true);
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            controller,
            "SwitchToAlertAsync",
            "missing-alert"));
        TestAssert.True(
            switchCancellation.IsCancellationRequested &&
            GetPrivateField<WorkActionRequest?>(
                controller,
                "_pendingAction") is null &&
            !GetPrivateField<bool>(
                controller,
                "_voiceApprovalEligible") &&
            GetPrivateField<ApprovalMethod?>(
                controller,
                "_deferredApproval") is null &&
            GetPrivateField<string?>(
                controller,
                "_deferredActionId") is null,
            "Switching alerts retained an action from the cancelled turn.");

        SetPrivateField(controller, "_turnInProgress", false);
        SetPrivateField(controller, "_activeOperationCts", null);
        SetPrivateField(
            controller,
            "_pendingAction",
            WorkActionPolicy.NormalizeAndValidate(
                new WorkActionRequest
                {
                    Kind = WorkActionKind.SendEmail,
                    Subject = "Speech failure audit",
                    Body = "Verified before speech",
                    Recipients = ["person@example.com"]
                }));
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            controller,
            "ExecutePendingActionAsync",
            ApprovalMethod.Click,
            conversationCts.Token));
        TestAssert.True(
            state.State.AuditLog.Count == 1 &&
            state.State.AuditLog[0].Verified,
            "A post-action speech failure corrupted the verified audit.");

        SetPrivateField(
            controller,
            "_activeTurn",
            Task.FromException(
                new InvalidOperationException("speech fault")));
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            controller,
            "WaitForActiveTurnAsync"));

        controller.EndConversation();
        WaitWithDispatcher(controller.DisposeAsync().AsTask());
        conversationCts.Dispose();
        window.Close();
        mascot.Close();
    }

    private static void TestSettingsWindow()
    {
        var initial = new PeggySettings
        {
            CopilotModel = "gpt-5.4",
            StartAtSignIn = true,
            MonitoringScheduleEnabled = true,
            MonitoringWakeMinute = 7 * 60,
            MonitoringSleepMinute = 19 * 60,
            AdditionalOutlookFolders = ["Inbox/Unavailable"],
            TeamsScope = TeamsMonitoringScope.SelectedTeamsAndChannels,
            TeamsChannelWatchlist =
            [
                new TeamsChannelWatch
                {
                    TeamId = "unavailable-team",
                    ChannelId = "unavailable-channel",
                    DisplayName = "Unavailable / Preserved"
                }
            ]
        };
        var provider = new MockMessageProvider();
        var modelLoadCount = 0;
        Task<IReadOnlyList<CopilotModelOption>> LoadModelsAsync(
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            modelLoadCount++;
            IReadOnlyList<CopilotModelOption> models =
                modelLoadCount == 1
                    ?
                    [
                        new(
                            "claude-haiku-4.5",
                            "Claude Haiku 4.5",
                            MaxContextWindowTokens: 200_000),
                        new(
                            "gpt-5.4",
                            "GPT-5.4",
                            SupportedReasoningEfforts:
                                ["none", "low", "medium", "high", "xhigh"],
                            SupportsLongContext: true,
                            MaxContextWindowTokens: 1_050_000),
                        new(
                            "gpt-5.6-sol",
                            "GPT-5.6 Sol",
                            SupportedReasoningEfforts:
                                [
                                    "none",
                                    "low",
                                    "medium",
                                    "high",
                                    "xhigh",
                                    "max"
                                ],
                            SupportsLongContext: true,
                            MaxContextWindowTokens: 1_050_000)
                    ]
                    :
                    [
                        new(
                            "claude-haiku-4.5",
                            "Claude Haiku 4.5",
                            MaxContextWindowTokens: 200_000),
                        new(
                            "gpt-5.4",
                            "GPT-5.4",
                            SupportedReasoningEfforts:
                                ["none", "low", "medium", "high", "xhigh"],
                            SupportsLongContext: true,
                            MaxContextWindowTokens: 1_050_000),
                        new(
                            "gpt-5.6-sol",
                            "GPT-5.6 Sol",
                            SupportedReasoningEfforts:
                                [
                                    "none",
                                    "low",
                                    "medium",
                                    "high",
                                    "xhigh",
                                    "max"
                                ],
                            SupportsLongContext: true,
                            MaxContextWindowTokens: 1_050_000),
                        new(
                            "gpt-6-astra",
                            "GPT-6 Astra",
                            SupportedReasoningEfforts:
                                ["low", "medium", "high", "xhigh", "max"],
                            SupportsLongContext: true,
                            MaxContextWindowTokens: 1_178_000)
                    ];
            return Task.FromResult(models);
        }
        var window = new SettingsWindow(
            initial,
            provider,
            "person@example.com",
            LoadModelsAsync);
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            window,
            "LoadOutlookFoldersAsync"));
        var outlookPanel = Find<StackPanel>(
            window,
            "OutlookFoldersPanel");
        TestAssert.True(
            provider.ListOutlookRootFolderCallCount == 1 &&
            provider.ListOutlookChildFolderCallCount == 0 &&
            Descendants<Expander>(outlookPanel).Count() == 2 &&
            InvokeInstancePrivate<bool>(
                window,
                "IsOutlookFolderIncluded",
                PeggyDefaults.ActiveCasesFolder),
            "Settings did not lazily load Outlook folders or preserve the " +
            "default Active selection.");
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            window,
            "LoadCopilotModelsAsync"));
        var modelCombo = Find<ComboBox>(window, "ModelCombo");
        TestAssert.True(
            modelLoadCount == 1 &&
            modelCombo.Items
                .Cast<CopilotModelOption>()
                .Any(model => model.Id == "gpt-5.6-sol") &&
            modelCombo.Items
                .Cast<CopilotModelOption>()
                .All(model => model.Id != "gpt-6-astra"),
            "Settings did not load the current GitHub Copilot model catalog.");
        WaitWithDispatcher(InvokeInstancePrivate<Task>(
            window,
            "LoadCopilotModelsAsync"));
        TestAssert.True(
            modelLoadCount == 2 &&
            modelCombo.Items
                .Cast<CopilotModelOption>()
                .Any(model => model.Id == "gpt-6-astra"),
            "Refreshing Settings did not surface a newly available model.");
        modelCombo.SelectedValue = "claude-haiku-4.5";
        TestAssert.True(
            !Find<ComboBox>(
                window,
                "ReasoningEffortCombo").IsEnabled &&
            !Find<ComboBox>(
                window,
                "ContextTierCombo").IsEnabled,
            "Unsupported model options were left enabled.");
        modelCombo.SelectedValue = "gpt-5.4";
        AssertDecorativeTextIsRaw(window, "Settings");
        var modelBorderColor = ((SolidColorBrush)Find<ComboBox>(
            window,
            "ModelCombo").BorderBrush).Color;
        var comboBorderColor = ((SolidColorBrush)Find<ComboBox>(
            window,
            "TeamsScopeCombo").BorderBrush).Color;
        var modelBorderContrast = GetContrastRatio(
            modelBorderColor,
            Colors.White);
        var comboBorderContrast = GetContrastRatio(
            comboBorderColor,
            Colors.White);
        TestAssert.True(
            modelBorderContrast >= 3 &&
            comboBorderContrast >= 3,
            "Settings form boundaries do not meet 3:1 contrast. " +
            $"Model={modelBorderColor} ({modelBorderContrast:F2}), " +
            $"ComboBox={comboBorderColor} ({comboBorderContrast:F2}).");
        TestAssert.True(
            new[]
            {
                "SpeechModelsText",
                "WakeTimeText",
                "SleepTimeText"
            }.All(name => !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(
                    Find<TextBox>(window, name)))) &&
            new[]
            {
                "SpeechVoice",
                "SpeechThreads",
                "SpeechInputGain",
                "TeamsScopeCombo",
                "ModelCombo",
                "ReasoningEffortCombo",
                "ContextTierCombo"
            }.All(name => !string.IsNullOrWhiteSpace(
                AutomationProperties.GetName(
                    Find<ComboBox>(window, name)))),
            "One or more Settings inputs do not have accessible names.");
        TestAssert.True(
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(
                Find<CheckBox>(window, "AllInboxFoldersCheck"))),
            "The Outlook all-folders control has no accessible name.");
        var reauthenticated = new WorkIqAccountIdentity(
            "person@example.com",
            "mailbox@example.com",
            "person@example.com")
        {
            EulaAcceptedVersion = "1.0.0-test"
        };
        InvokeInstancePrivate(
            window,
            "ApplyReauthenticatedIdentity",
            reauthenticated);
        TestAssert.True(
            ReferenceEquals(
                window.ReauthenticatedIdentity,
                reauthenticated) &&
            Find<TextBlock>(window, "AccountStatus").Text ==
                "Connected as person@example.com",
            "Settings discarded a successful WorkIQ reauthentication.");
        window.Loaded += (_, _) =>
            window.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    Find<ComboBox>(
                        window,
                        "ModelCombo").SelectedValue = "gpt-5.6-sol";
                    Find<ComboBox>(
                        window,
                        "ReasoningEffortCombo").SelectedValue = "high";
                    Find<ComboBox>(
                        window,
                        "ContextTierCombo").SelectedValue = "long_context";
                    Find<CheckBox>(
                        window,
                        "ScheduleEnabledCheck").IsChecked = true;
                    Find<TextBox>(
                        window,
                        "WakeTimeText").Text = "06:30";
                    Find<TextBox>(
                        window,
                        "SleepTimeText").Text = "20:45";
                    Find<CheckBox>(window, "StartupCheck").IsChecked = false;
                    Find<CheckBox>(window, "PausedCheck").IsChecked = true;
                    Find<CheckBox>(
                        window,
                        "MonitorTeamsChatsCheck").IsChecked = true;
                    Find<CheckBox>(
                        window,
                        "SpeechEnabledCheck").IsChecked = false;
                    Find<TextBox>(
                        window,
                        "SpeechModelsText").Text = @"C:\Peggy\Models";
                    Find<ComboBox>(
                        window,
                        "SpeechThreads").SelectedIndex = 2;
                    Find<ComboBox>(
                        window,
                        "SpeechVoice").SelectedIndex = 3;
                    Find<ComboBox>(
                        window,
                        "SpeechInputGain").SelectedIndex = 3;
                    WaitWithDispatcher(InvokeInstancePrivate<Task>(
                        window,
                        "LoadTeamsAsync"));
                    var teamsPanel = Find<StackPanel>(
                        window,
                        "TeamsPanel");
                    TestAssert.True(
                        provider.ListJoinedTeamsCallCount > 0 &&
                        provider.ListTeamChannelsCallCount == 0 &&
                        Descendants<Expander>(teamsPanel).Count() == 2,
                        "Settings eagerly loaded every team's channels.");

                    var cases = Descendants<Expander>(outlookPanel)
                        .Single(expander =>
                            expander.Tag is OutlookFolderInfo
                            {
                                FolderId: "mock-cases"
                            });
                    WaitWithDispatcher(InvokeInstancePrivate<Task>(
                        window,
                        "LoadOutlookChildFoldersAsync",
                        cases.Tag!));
                    var active = Descendants<CheckBox>(cases)
                        .Single(check =>
                            check.Tag is OutlookFolderInfo
                            {
                                FolderPath: "Inbox/Cases/Active"
                            });
                    TestAssert.True(
                        active.IsChecked == true,
                        "Inbox/Cases/Active was not selected by default.");
                    active.IsChecked = false;

                    var projects = Descendants<Expander>(outlookPanel)
                        .Single(expander =>
                            expander.Tag is OutlookFolderInfo
                            {
                                FolderId: "mock-projects"
                            });
                    WaitWithDispatcher(InvokeInstancePrivate<Task>(
                        window,
                        "LoadOutlookChildFoldersAsync",
                        projects.Tag!));
                    Descendants<CheckBox>(projects)
                        .Single(check =>
                            check.Tag is OutlookFolderInfo
                            {
                                FolderPath:
                                    "Inbox/Projects/Project Alpha"
                            })
                        .IsChecked = true;
                    Descendants<CheckBox>(outlookPanel)
                        .Single(check =>
                            check.Tag is OutlookFolderInfo
                            {
                                FolderPath: "Inbox/Archive"
                            })
                        .IsChecked = true;
                    TestAssert.True(
                        provider.ListOutlookRootFolderCallCount == 1 &&
                        provider.ListOutlookChildFolderCallCount == 2 &&
                        Descendants<TextBlock>(
                                Find<StackPanel>(
                                    window,
                                    "SelectedOutlookFoldersPanel"))
                            .Any(text =>
                                text.Text == "Inbox/Unavailable"),
                        "Outlook folder selection was not lazy or erased an " +
                        "unavailable saved folder.");

                    var engineering = Descendants<Expander>(teamsPanel)
                        .First(expander =>
                            expander.Tag is TeamsTeamInfo
                            {
                                TeamId: "mock-team"
                            });
                    WaitWithDispatcher(InvokeInstancePrivate<Task>(
                        window,
                        "LoadTeamChannelsAsync",
                        engineering.Tag!));
                    TestAssert.Equal(
                        1,
                        provider.ListTeamChannelsCallCount,
                        "Expanding one team did not load exactly that team.");
                    var general = Descendants<CheckBox>(engineering)
                        .First(check => string.Equals(
                            check.Content?.ToString(),
                            "General",
                            StringComparison.Ordinal));
                    var incidents = Descendants<CheckBox>(engineering)
                        .First(check => string.Equals(
                            check.Content?.ToString(),
                            "Incidents",
                            StringComparison.Ordinal));
                    FindButton(
                            engineering,
                            "Select entire team")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestAssert.True(
                        general.IsChecked == true &&
                        incidents.IsChecked == true,
                        "Team-level selection did not select every channel.");
                    FindButton(
                            engineering,
                            "Deselect team")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestAssert.True(
                        general.IsChecked == false &&
                        incidents.IsChecked == false,
                        "Team-level deselection did not clear every channel.");
                    FindButton(
                            engineering,
                            "Select entire team")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    incidents.IsChecked = false;
                    var teamCheck = Descendants<CheckBox>(engineering)
                        .Single(check => check.Tag is TeamsTeamInfo);
                    TestAssert.True(
                        general.IsChecked == true &&
                        incidents.IsChecked == false &&
                        teamCheck.IsChecked is null,
                        "A channel exclusion did not produce partial team " +
                        "selection.");
                    var selectedPanel = Find<StackPanel>(
                        window,
                        "SelectedChannelsPanel");
                    Descendants<Button>(selectedPanel)
                        .First(button =>
                            string.Equals(
                                button.Content?.ToString(),
                                "Deselect team",
                                StringComparison.Ordinal) &&
                            string.Equals(
                            button.Tag?.ToString(),
                            "unavailable-team",
                            StringComparison.Ordinal))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    InvokeInstancePrivate(
                        window,
                        "Save_Click",
                        window,
                        new RoutedEventArgs());
                }),
                DispatcherPriority.Background);

        TestAssert.True(
            window.ShowDialog() == true &&
            window.SavedSettings is not null,
            "Settings could not be saved.");
        TestAssert.Equal(
            "Connected as person@example.com",
            Find<TextBlock>(window, "AccountStatus").Text,
            "Settings did not show the pinned Microsoft 365 account.");
        TestAssert.Equal(
            3,
            window.SavedSettings!.AdditionalOutlookFolders.Count,
            "Settings did not persist selected Outlook folder roots.");
        TestAssert.True(
            window.SavedSettings.CopilotModel == "gpt-5.6-sol" &&
            window.SavedSettings.CopilotReasoningEffort == "high" &&
            window.SavedSettings.CopilotContextTier == "long_context" &&
            !window.SavedSettings.IncludeActiveCasesFolder &&
            window.SavedSettings.AdditionalOutlookFolders.SequenceEqual(
                [
                    "Inbox/Archive",
                    "Inbox/Projects/Project Alpha",
                    "Inbox/Unavailable"
                ],
                StringComparer.OrdinalIgnoreCase) &&
            !window.SavedSettings.StartAtSignIn &&
            window.SavedSettings.MonitoringPaused &&
            window.SavedSettings.TeamsScope ==
                TeamsMonitoringScope.SelectedTeamsAndChannels &&
            window.SavedSettings.MonitorTeamsChatsInSelectedScope &&
            window.SavedSettings.MonitoringScheduleEnabled &&
            window.SavedSettings.MonitoringWakeMinute == 390 &&
            window.SavedSettings.MonitoringSleepMinute == 1245 &&
            !window.SavedSettings.SpeechEnabled &&
            window.SavedSettings.SpeechThreads == 4 &&
            window.SavedSettings.SpeechVoiceId == 6 &&
            window.SavedSettings.SpeechInputGain == 16 &&
            window.SavedSettings.SpeechModelDirectory ==
                @"C:\Peggy\Models",
            "Settings toggles were not persisted.");
        TestAssert.Equal(
            0,
            window.SavedSettings.TeamsChannelWatchlist.Count,
            "Settings retained a redundant explicit channel selection.");
        TestAssert.True(
            window.SavedSettings.TeamsTeamWatchlist is
            [
                {
                    TeamId: "mock-team",
                    ExcludedChannelIds: ["mock-incidents"]
                }
            ],
            "Settings did not persist whole-team monitoring with a channel " +
            "exclusion.");
    }

    private static void TestWorkIqSignInWindow()
    {
        var pinned = new WorkIqSignInWindow("person@example.com");
        AssertDecorativeTextIsRaw(pinned, "Microsoft 365 connection");
        TestAssert.True(
            Find<TextBox>(pinned, "AccountText").Text ==
                "person@example.com" &&
            AutomationProperties.GetName(
                Find<TextBox>(pinned, "AccountText")) ==
                "Microsoft 365 account" &&
            Find<TextBox>(pinned, "AccountText").IsReadOnly &&
            Find<Button>(pinned, "SignInButton").Content?.ToString() ==
                "Sign in again" &&
            Find<CheckBox>(pinned, "EulaCheck").IsChecked != true,
            "The reauthentication window did not preserve its account and " +
            "EULA boundary.");
        pinned.Close();

        var firstRun = new WorkIqSignInWindow();
        TestAssert.True(
            !Find<TextBox>(firstRun, "AccountText").IsReadOnly &&
            Find<Button>(firstRun, "InstallButton").Content?.ToString() ==
                "Install WorkIQ with npm",
            "The first-run window did not expose standalone WorkIQ setup.");
        firstRun.Close();

        var constrained = new Window
        {
            Width = 760,
            Height = 820,
            MinWidth = 620,
            MinHeight = 620
        };
        PeggyWindowLayout.ConstrainToWorkArea(
            constrained,
            new Rect(0, 0, 240, 200),
            margin: 16);
        TestAssert.True(
            constrained.Width == 224 &&
            constrained.Height == 184 &&
            constrained.MinWidth == 224 &&
            constrained.MinHeight == 184 &&
            constrained.MaxWidth == 224 &&
            constrained.MaxHeight == 184,
            "Window constraints exceeded an extreme-DPI work area.");
    }

    private static void TestSettingsPreserveUnavailableWatchlist()
    {
        var original = new TeamsChannelWatch
        {
            TeamId = "original-team",
            ChannelId = "original-channel",
            DisplayName = "Original / Channel"
        };
        var originalTeam = new TeamsTeamWatch
        {
            TeamId = "entire-team",
            DisplayName = "Entire team",
            ExcludedChannelIds = ["excluded-channel"]
        };
        var window = new SettingsWindow(
            new PeggySettings
            {
                TeamsTeamWatchlist = [originalTeam],
                TeamsChannelWatchlist = [original]
            },
            new MockMessageProvider());
        var saved = InvokeInstancePrivate<List<TeamsChannelWatch>>(
            window,
            "GetSavedWatchlist");
        TestAssert.True(
            saved.Count == 1 &&
            saved[0].TeamId == original.TeamId &&
            saved[0].ChannelId == original.ChannelId &&
            saved[0].DisplayName == original.DisplayName,
            "Settings erased the watchlist before channel loading completed.");
        var savedTeams = InvokeInstancePrivate<List<TeamsTeamWatch>>(
            window,
            "GetSavedTeamWatchlist");
        TestAssert.True(
            savedTeams is
            [
                {
                    TeamId: "entire-team",
                    ExcludedChannelIds: ["excluded-channel"]
                }
            ],
            "Settings erased a whole-team selection before channel loading " +
            "completed.");
        window.Close();
    }

    private static void TestMascotAndBubble()
    {
        var mascot = new MascotWindow();
        mascot.ShowAnimated();
        mascot.UpdateLayout();
        TestAssert.True(
            mascot.IsVisible,
            "Mascot did not become visible.");
        var contextMenuRequested = false;
        mascot.ContextMenuRequested += () => contextMenuRequested = true;
        mascot.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent
        });
        TestAssert.True(
            contextMenuRequested,
            "Right-clicking the mascot did not request the tray menu.");
        TestAssert.True(
            Find<System.Windows.Shapes.Path>(
                mascot,
                "PeggyFeet") is not null,
            "Peggy's original bird artwork is incomplete.");
        mascot.SetState(PeggyState.Listening);
        TestAssert.Equal(
            PeggyState.Listening,
            mascot.CurrentState,
            "Mascot did not retain its listening state.");
        TestAssert.Equal(
            Visibility.Visible,
            Find<Canvas>(mascot, "Ripples").Visibility,
            "Mascot listening animation did not enable ripples.");
        mascot.SetState(PeggyState.Thinking);
        TestAssert.Equal(
            PeggyState.Thinking,
            mascot.CurrentState,
            "Mascot did not retain its thinking state.");
        TestAssert.Equal(
            Visibility.Visible,
            Find<Canvas>(mascot, "Thinking").Visibility,
            "Mascot thinking animation did not appear.");
        mascot.SetState(PeggyState.NewDigest);
        TestAssert.True(
            FindObject<RotateTransform>(
                mascot,
                "WingRotate").HasAnimatedProperties &&
            FindObject<TranslateTransform>(
                mascot,
                "BodyTranslate").HasAnimatedProperties,
            "Peggy's original new-digest motion did not run.");
        mascot.SetState(PeggyState.Sleeping);
        TestAssert.Equal(
            Visibility.Visible,
            Find<Canvas>(mascot, "SleepOverlay").Visibility,
            "Mascot sleeping animation did not display its sleep overlay.");
        var rootScale = FindObject<ScaleTransform>(
            mascot,
            "RootScale");
        TestAssert.True(
            rootScale.ScaleX == rootScale.ScaleY,
            "Peggy's sleep state changed her body proportions.");
        var woke = false;
        mascot.WakingCompleted += () => woke = true;
        mascot.SetState(PeggyState.Waking);
        WaitWithDispatcher(Task.Delay(TimeSpan.FromSeconds(1.9)));
        TestAssert.True(
            woke,
            "Mascot waking animation did not complete.");
        var anchor = mascot.HeadAnchor();
        TestAssert.True(
            double.IsFinite(anchor.X) && double.IsFinite(anchor.Y),
            "Mascot speech anchor is invalid.");

        var bubble = new SpeechBubbleWindow();
        bubble.Say("Peggy smoke test", anchor, seconds: 1);
        TestAssert.Equal(
            "Peggy smoke test",
            Find<TextBlock>(bubble, "Text").Text,
            "Speech bubble omitted its text.");
        TestAssert.True(
            bubble.IsVisible,
            "Speech bubble did not become visible.");
        bubble.HideNow();
        TestAssert.True(
            !bubble.IsVisible,
            "Speech bubble did not hide.");
        mascot.Hide();
    }

    private static void TestLocalSpeech()
    {
        var previousTestMode = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_TEST_MODE");
        var previousTranscript = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_TEST_TRANSCRIPT");
        var previousPartial = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_TEST_PARTIAL");
        var previousDelay = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_TEST_DELAY_MS");
        try
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_MODE",
                "1");
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_TRANSCRIPT",
                "local protocol transcript");
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_PARTIAL",
                "local partial transcript");
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_DELAY_MS",
                null);

            using var speech = new LocalSpeechService();
            speech.SpeakAsync(string.Empty, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            speech.WarmUpAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            string? partial = null;
            var transcript = speech.RecognizeOnceAsync(
                    TimeSpan.FromSeconds(2),
                    value => partial = value,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            TestAssert.True(
                transcript == "local protocol transcript" &&
                partial == "local partial transcript" &&
                speech.IsLoaded,
                "The isolated local speech protocol did not return a transcript.");

            speech.Unload();
            TestAssert.True(
                !speech.IsLoaded,
                "Unloading speech left the model host running.");
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_DELAY_MS",
                "3000");
            var speaking = speech.SpeakAsync(
                "interrupt this test",
                CancellationToken.None);
            TestAssert.True(
                SpinWait.SpinUntil(
                    () => speech.IsSpeaking && speech.IsLoaded,
                    TimeSpan.FromSeconds(5)),
                "The isolated speech host did not enter speaking state.");
            speech.CancelSpeaking();
            speaking.GetAwaiter().GetResult();
            TestAssert.True(
                !speech.IsLoaded &&
                !speech.IsSpeaking,
                "Speech barge-in did not unload the isolated host.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            TestAssert.Throws<OperationCanceledException>(
                () => speech.RecognizeOnceAsync(
                        TimeSpan.FromSeconds(2),
                        onPartial: null,
                        ct: cancelled.Token)
                    .GetAwaiter()
                    .GetResult(),
                "Local speech recognition ignored cancellation.");
        }

        finally
        {
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_MODE",
                previousTestMode);
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_TRANSCRIPT",
                previousTranscript);
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_PARTIAL",
                previousPartial);
            Environment.SetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_DELAY_MS",
                previousDelay);
        }
    }

    private sealed class SilentSpeechProvider : ISpeechProvider
    {
        public List<string> Spoken { get; } = [];

        public bool IsSpeaking => false;

        public bool IsListening => false;

        public bool IsLoaded { get; private set; }

        public Task WarmUpAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IsLoaded = true;
            return Task.CompletedTask;
        }

        public Task<string?> RecognizeOnceAsync(
            TimeSpan silenceTimeout,
            Action<string>? onPartial,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task SpeakAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public void CancelSpeaking()
        {
        }

        public void CancelListening()
        {
        }

        public void Unload()
            => IsLoaded = false;

        public void Dispose()
            => Unload();
    }

    private static void AssertDecorativeTextIsRaw(
        DependencyObject root,
        string surface)
    {
        var decorative = Descendants<TextBlock>(root)
            .Where(text =>
                text.Text is "P" or "!" or "i" ||
                string.Equals(
                    text.FontFamily.Source,
                    "Segoe MDL2 Assets",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        TestAssert.True(
            decorative.Count > 0 &&
            decorative.All(text =>
                text is DecorativeTextBlock &&
                UIElementAutomationPeer.CreatePeerForElement(text) is null),
            $"A decorative glyph in {surface} is exposed as screen-reader " +
            "content.");
    }

    private static double GetContrastRatio(Color first, Color second)
    {
        var firstLuminance = GetRelativeLuminance(first);
        var secondLuminance = GetRelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double GetRelativeLuminance(Color color)
        => (0.2126 * Linearize(color.R)) +
           (0.7152 * Linearize(color.G)) +
           (0.0722 * Linearize(color.B));

    private static double Linearize(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static T Find<T>(FrameworkElement root, string name)
        where T : FrameworkElement
        => root.FindName(name) as T
           ?? throw new InvalidOperationException(
               $"Desktop element '{name}' was not found.");

    private static T FindObject<T>(FrameworkElement root, string name)
        where T : class
        => root.FindName(name) as T
           ?? throw new InvalidOperationException(
               $"Desktop object '{name}' was not found.");

    private static Button FindButton(DependencyObject root, string content)
        => Descendants<Button>(root).FirstOrDefault(button =>
            string.Equals(
                button.Content?.ToString(),
                content,
                StringComparison.Ordinal))
           ?? throw new InvalidOperationException(
               $"Desktop button '{content}' was not found.");

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root)
                     .OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void InvokeInstancePrivate(
        object instance,
        string methodName,
        params object?[] arguments)
        => InvokeInstancePrivate<object?>(instance, methodName, arguments);

    private static T InvokeInstancePrivate<T>(
        object instance,
        string methodName,
        params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Desktop method {instance.GetType().Name}.{methodName} was not found.");
        try
        {
            return (T)method.Invoke(instance, arguments)!;
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void SetPrivateField(
        object instance,
        string fieldName,
        object? value)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Desktop field {instance.GetType().Name}.{fieldName} was not found.");
        field.SetValue(instance, value);
    }

    private static T GetPrivateField<T>(
        object instance,
        string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Desktop field {instance.GetType().Name}.{fieldName} was not found.");
        return (T)field.GetValue(instance)!;
    }

    private static void WaitWithDispatcher(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
}
