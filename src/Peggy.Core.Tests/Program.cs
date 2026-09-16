using System.Text;
using System.Text.Json;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Peggy.Core;
using Peggy.Core.Tests;
using Peggy.Tray;

var liveRun = args.Any(argument =>
    argument.StartsWith("--live", StringComparison.OrdinalIgnoreCase));
if (!liveRun &&
    string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(
            PeggyDefaults.WorkIqAccountEnvironmentVariable)))
{
    Environment.SetEnvironmentVariable(
        PeggyDefaults.WorkIqAccountEnvironmentVariable,
        "test-account@example.invalid");
}

if (TryRenderMascots(args) ||
    TryRenderAlertCenter(args) ||
    TryRenderUiSuite(args) ||
    await TryCreateScheduleSmokeStateAsync(args) ||
    TryRunFakeWorkIq(args))
{
    return;
}

var testDirectory = Path.Combine(
    Path.GetTempPath(),
    "peggy-core-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    await CoreBehaviorSmokeTests.RunAsync(testDirectory);
    await AssistantBehaviorSmokeTests.RunAsync(testDirectory);
    await DesktopSmokeTests.RunAsync();
    await TestEncryptedStateAsync(testDirectory);
    await TestStartupMonitoringWindowAsync(testDirectory);
    await TestMockMonitoringAndActionsAsync();
    TestActionPolicy();
    if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
    {
        await TestLiveReadSurfacesAsync(channelsOnly: false);
    }
    else if (args.Contains("--live-channels", StringComparer.OrdinalIgnoreCase))
    {
        await TestLiveReadSurfacesAsync(channelsOnly: true);
    }
    else if (args.Contains("--live-copilot", StringComparer.OrdinalIgnoreCase))
    {
        await AssistantBehaviorSmokeTests.RunLiveCopilotAsync();
    }
    else if (args.Contains("--live-unread", StringComparer.OrdinalIgnoreCase))
    {
        await TestLiveUnreadAsync();
    }
    else if (args.Contains(
                 "--live-monitoring",
                 StringComparer.OrdinalIgnoreCase))
    {
        await TestLiveMonitoringAsync();
    }
    else if (args.Contains(
                 "--live-outlook-link",
                 StringComparer.OrdinalIgnoreCase))
    {
        await TestLiveOutlookLinkAsync();
    }
    Console.WriteLine("All Peggy Core smoke tests passed.");
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}

static bool TryRenderMascots(string[] arguments)
{
    const string prefix = "--render-mascots=";
    var configured = arguments.FirstOrDefault(argument =>
        argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (configured is null)
    {
        return false;
    }

    var directory = Path.GetFullPath(
        configured[prefix.Length..].Trim().Trim('"'));
    Directory.CreateDirectory(directory);
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("en-US");
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var mascot = new MascotWindow();
            mascot.Show();
            var root = mascot.FindName("RootCanvas") as FrameworkElement
                ?? throw new InvalidOperationException(
                    "Mascot render root was not found.");
            foreach (var state in Enum.GetValues<PeggyState>())
            {
                mascot.SetState(state);
                PumpDispatcher(TimeSpan.FromMilliseconds(180));
                mascot.UpdateLayout();
                root.Measure(new Size(250, 268));
                root.Arrange(new Rect(0, 0, 250, 268));
                root.UpdateLayout();

                var fileName =
                    $"peggy-{state.ToString().ToLowerInvariant()}.png";
                SaveMascotPreview(
                    root,
                    Path.Combine(directory, fileName));
                if (state == PeggyState.Idle)
                {
                    SaveMascotPreview(
                        root,
                        Path.Combine(directory, "peggy.png"));
                }
            }
            mascot.Close();
            application.Shutdown();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException(
            "Mascot previews could not be rendered.",
            failure);
    }

    Console.WriteLine($"Rendered mascot previews to {directory}.");
    return true;
}

static bool TryRenderAlertCenter(string[] arguments)
{
    const string prefix = "--render-alert-center=";
    var configured = arguments.FirstOrDefault(argument =>
        argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (configured is null)
    {
        return false;
    }

    var directory = Path.GetFullPath(
        configured[prefix.Length..].Trim().Trim('"'));
    Directory.CreateDirectory(directory);
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("en-US");
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var previewLocal = new DateTime(
                2024,
                4,
                8,
                15,
                0,
                0,
                DateTimeKind.Unspecified);
            var now = new DateTimeOffset(
                previewLocal,
                TimeZoneInfo.Local.GetUtcOffset(previewLocal));
            var alerts = new[]
            {
                new AlertRecord
                {
                    ThreadId = "preview-critical",
                    Source = MessageSource.Email,
                    Tier = AlertTier.Critical,
                    Summary =
                        "Production monitoring is delayed in a primary region",
                    Reason =
                        "A live service incident can delay telemetry and " +
                        "affect active customer investigations.",
                    SourceReference =
                        "https://outlook.office.com/mail/deeplink/read/AAMkPreview",
                    FirstSeenUtc = now.AddMinutes(-18),
                    LastActivityUtc = now.AddMinutes(-4),
                    UpdatedUtc = now.AddMinutes(-4)
                },
                new AlertRecord
                {
                    ThreadId = "preview-attention",
                    Source = MessageSource.Teams,
                    Tier = AlertTier.Attention,
                    Summary =
                        "Customer escalation needs a response before 4:00 PM",
                    Reason =
                        "The latest reply asks for an owner and a recovery " +
                        "timeline before today's status call.",
                    SourceReference =
                        "https://teams.microsoft.com/l/message/thread/message",
                    FirstSeenUtc = now.AddMinutes(-42),
                    LastActivityUtc = now.AddMinutes(-12),
                    UpdatedUtc = now.AddMinutes(-12)
                }
            };

            var window = new AlertCenterWindow();
            window.Show();
            window.UpdateAlerts(alerts, maskContent: false);
            PumpDispatcher(TimeSpan.FromMilliseconds(160));
            SaveWindowContent(
                window,
                Path.Combine(directory, "alert-center.png"));

            window.SetMonitoringInterrupted(
                true,
                3,
                now.AddMinutes(-11),
                "WorkIQ connection needs attention.");
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            SaveWindowContent(
                window,
                Path.Combine(directory, "alert-center-health.png"));

            window.SetMonitoringInterrupted(false, 0, now);
            window.UpdateAlerts([], maskContent: false);
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            SaveWindowContent(
                window,
                Path.Combine(directory, "alert-center-empty.png"));
            window.Close();
            application.Shutdown();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException(
            "Alert Center previews could not be rendered.",
            failure);
    }

    Console.WriteLine($"Rendered Alert Center previews to {directory}.");
    return true;
}

static bool TryRenderUiSuite(string[] arguments)
{
    const string prefix = "--render-ui-suite=";
    var configured = arguments.FirstOrDefault(argument =>
        argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (configured is null)
    {
        return false;
    }

    var directory = Path.GetFullPath(
        configured[prefix.Length..].Trim().Trim('"'));
    Directory.CreateDirectory(directory);
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            CultureInfo.CurrentCulture =
                CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("en-US");
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var previewLocal = new DateTime(
                2024,
                4,
                8,
                15,
                0,
                0,
                DateTimeKind.Unspecified);
            var now = new DateTimeOffset(
                previewLocal,
                TimeZoneInfo.Local.GetUtcOffset(previewLocal));

            var settings = new SettingsWindow(
                new PeggySettings
                {
                    CopilotModel = "gpt-5.4",
                    MonitoringScheduleEnabled = true,
                    MonitoringWakeMinute = 7 * 60,
                    MonitoringSleepMinute = 19 * 60,
                    SpeechEnabled = true,
                    TeamsScope =
                        TeamsMonitoringScope.SelectedTeamsAndChannels,
                    TeamsTeamWatchlist =
                    [
                        new TeamsTeamWatch
                        {
                            TeamId = "mock-team",
                            DisplayName = "Engineering"
                        }
                    ]
                },
                new MockMessageProvider(),
                "person@example.com");
            settings.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(260));
            if (int.TryParse(
                    Environment.GetEnvironmentVariable(
                        "PEGGY_UI_PREVIEW_HOLD_SECONDS"),
                    out var previewHoldSeconds) &&
                previewHoldSeconds > 0)
            {
                PumpDispatcher(TimeSpan.FromSeconds(
                    Math.Min(previewHoldSeconds, 120)));
            }
            SaveWindowContent(
                settings,
                Path.Combine(directory, "settings.png"));
            settings.Close();

            var conversation = new ConversationWindow();
            conversation.BeginConversation();
            conversation.AppendAssistant(
                "Good morning. I found two items that may need your attention.");
            conversation.AppendUser(
                "Summarize the customer escalation and suggest a reply.");
            conversation.SetListening();
            conversation.ShowPartialTranscript(
                "Draft a concise response with the current recovery timeline.");
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            SaveWindowContent(
                conversation,
                Path.Combine(directory, "conversation.png"));
            conversation.ShowAction(new WorkActionRequest
            {
                Kind = WorkActionKind.CreateReplyDraft,
                ExactDescription =
                    "Create a reply draft for the customer escalation. " +
                    "The draft will not be sent."
            });
            PumpDispatcher(TimeSpan.FromMilliseconds(80));
            SaveWindowContent(
                conversation,
                Path.Combine(directory, "conversation-action.png"));
            conversation.EndConversation();
            conversation.Close();

            var alert = new AlertRecord
            {
                ThreadId = "context-preview",
                Source = MessageSource.Email,
                Tier = AlertTier.Attention,
                Summary =
                    "Customer escalation needs a response before 4:00 PM",
                Reason =
                    "The latest reply asks for an owner and a recovery " +
                    "timeline before today's status call.",
                FirstSeenUtc = now.AddMinutes(-40),
                LastActivityUtc = now.AddMinutes(-12),
                UpdatedUtc = now.AddMinutes(-12)
            };
            var context = new AlertContextWindow(
                alert,
                new MessageThread(
                    "context-preview",
                    MessageSource.Email,
                    "Customer escalation",
                    [
                        new Message(
                            "context-message-1",
                            MessageSource.Email,
                            "context-preview",
                            "Customer contact",
                            "Customer escalation",
                            "Can you confirm an owner and the recovery " +
                            "timeline before our status call?",
                            now.AddMinutes(-20),
                            IsRead: false),
                        new Message(
                            "context-message-2",
                            MessageSource.Email,
                            "context-preview",
                            "You",
                            "Customer escalation",
                            "We are validating the latest telemetry and will " +
                            "share an update shortly.",
                            now.AddMinutes(-12),
                            IsRead: true)
                    ]),
                canOpenOriginal: true);
            context.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            SaveWindowContent(
                context,
                Path.Combine(directory, "alert-context.png"));
            context.Close();

            var signIn = new WorkIqSignInWindow(
                "person@example.com",
                setup: null,
                previewMode: true);
            signIn.SetPreviewInstallationState(
                "WorkIQ 1.0.0 is ready.",
                "Ready to connect securely.");
            signIn.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            SaveWindowContent(
                signIn,
                Path.Combine(directory, "microsoft-365-connection.png"));
            signIn.Close();

            var bubble = new SpeechBubbleWindow();
            bubble.Say(
                "I found two items that may need your attention.",
                new Point(420, 420),
                seconds: 60);
            PumpDispatcher(TimeSpan.FromMilliseconds(100));
            SaveWindowContent(
                bubble,
                Path.Combine(directory, "speech-bubble.png"));
            bubble.HideNow();
            bubble.Close();
            application.Shutdown();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException(
            "Peggy UI previews could not be rendered.",
            failure);
    }

    Console.WriteLine($"Rendered Peggy UI previews to {directory}.");
    return true;
}

static void SaveWindowContent(Window window, string path)
{
    var content = window.Content as FrameworkElement
        ?? throw new InvalidOperationException(
            "The window did not expose renderable content.");
    if (!window.IsVisible ||
        content.ActualWidth <= 0 ||
        content.ActualHeight <= 0)
    {
        var constrainedWidth =
            double.IsNaN(window.Width) || window.Width <= 0
                ? 760
                : window.Width;
        var constrainedHeight =
            double.IsNaN(window.Height) || window.Height <= 0
                ? double.PositiveInfinity
                : window.Height;
        content.Measure(new Size(
            constrainedWidth,
            constrainedHeight));
        var arrangedWidth =
            double.IsNaN(window.Width) || window.Width <= 0
                ? Math.Max(1, content.DesiredSize.Width)
                : window.Width;
        var arrangedHeight =
            double.IsNaN(window.Height) || window.Height <= 0
                ? Math.Max(1, content.DesiredSize.Height)
                : window.Height;
        content.Arrange(new Rect(
            0,
            0,
            arrangedWidth,
            arrangedHeight));
        content.UpdateLayout();
    }
    var width = Math.Max(
        1,
        (int)Math.Ceiling(content.ActualWidth));
    var height = Math.Max(
        1,
        (int)Math.Ceiling(content.ActualHeight));
    var bitmap = new RenderTargetBitmap(
        width,
        height,
        96,
        96,
        PixelFormats.Pbgra32);
    bitmap.Render(content);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static void PumpDispatcher(TimeSpan duration)
{
    var frame = new DispatcherFrame();
    var timer = new DispatcherTimer
    {
        Interval = duration
    };
    timer.Tick += (_, _) =>
    {
        timer.Stop();
        frame.Continue = false;
    };
    timer.Start();
    Dispatcher.PushFrame(frame);
}

static void SaveMascotPreview(
    FrameworkElement root,
    string path)
{
    var preview = new DrawingVisual();
    using (var drawing = preview.RenderOpen())
    {
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(
                Color.FromRgb(244, 241, 236)),
            null,
            new Rect(0, 0, 500, 536),
            24,
            24);
        drawing.DrawRectangle(
            new VisualBrush(root)
            {
                Stretch = Stretch.Uniform
            },
            null,
            new Rect(0, 0, 500, 536));
    }

    var bitmap = new RenderTargetBitmap(
        500,
        536,
        96,
        96,
        PixelFormats.Pbgra32);
    bitmap.Render(preview);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static async Task TestStartupMonitoringWindowAsync(string directory)
{
    var path = Path.Combine(directory, "startup-state.dat");
    var store = new EncryptedStateStore(path);
    await store.SaveAsync(new PeggyStateDocument
    {
        MonitoringCursor = new MonitoringCursorState
        {
            Initialized = true,
            InitialOutlookScanComplete = true,
            InitialScanStartedUtc = DateTimeOffset.UtcNow.AddHours(-24),
            TeamsWatermarkUtc = DateTimeOffset.UtcNow.AddHours(-12),
            TeamsBoundaryMessageIds = ["teams-boundary"],
            PromptWatermarkUtc = DateTimeOffset.UtcNow.AddHours(-12),
            OutlookWatermarkUtc = DateTimeOffset.UtcNow.AddHours(-12),
            OutlookBoundaryMessageIds = ["outlook-boundary"],
            ChannelWatermarksUtc =
                new Dictionary<string, DateTimeOffset>
                {
                    ["channel"] = DateTimeOffset.UtcNow.AddHours(-12)
                },
            ChannelBoundaryMessageIds =
                new Dictionary<string, List<string>>
                {
                    ["channel"] = ["channel-boundary"]
                },
            ChannelDeltaLinks =
                new Dictionary<string, string>
                {
                    ["channel"] = "/opaque-next-link"
                },
            ChannelPageSizes =
                new Dictionary<string, int>
                {
                    ["channel"] = 50
                },
            ChannelScanResumeKey = "channel",
            ChannelContinuationPass = true,
            ParticipatedChannelThreads =
            [
                new ParticipatedChannelThread
                {
                    TeamId = "team",
                    ChannelId = "channel",
                    RootMessageId = "root",
                    LastActivityUtc = DateTimeOffset.UtcNow
                }
            ]
        }
    });

    var earliestStart = DateTimeOffset.UtcNow.AddMinutes(
        -PeggyDefaults.StartupLookbackMinutes);
    var manager = new PeggyStateManager(store);
    await manager.InitializeAsync();
    var latestStart = DateTimeOffset.UtcNow.AddMinutes(
        -PeggyDefaults.StartupLookbackMinutes);
    var cursor = manager.State.MonitoringCursor;

    Assert(
        cursor.InitialScanStartedUtc >= earliestStart &&
        cursor.InitialScanStartedUtc <= latestStart,
        "Startup monitoring was not rebased to the five-minute window.");
    Assert(
        !cursor.Initialized &&
        !cursor.InitialOutlookScanComplete &&
        cursor.TeamsWatermarkUtc is null &&
        cursor.PromptWatermarkUtc is null &&
        cursor.OutlookWatermarkUtc is null,
        "Startup monitoring retained stale global cursors.");
    Assert(
        cursor.TeamsBoundaryMessageIds.Count == 0 &&
        cursor.OutlookBoundaryMessageIds.Count == 0 &&
        cursor.ChannelWatermarksUtc.Count == 0 &&
        cursor.ChannelBoundaryMessageIds.Count == 0 &&
        cursor.ChannelDeltaLinks.Count == 0 &&
        cursor.ChannelPageSizes.Count == 0 &&
        cursor.ChannelScanResumeKey is null &&
        !cursor.ChannelContinuationPass,
        "Startup monitoring retained stale paging cursors.");
    Assert(
        cursor.ParticipatedChannelThreads.Count == 1,
        "Startup monitoring discarded participated channel threads.");
}

static async Task<bool> TryCreateScheduleSmokeStateAsync(string[] arguments)
{
    const string prefix = "--create-schedule-smoke-state=";
    var configured = arguments.FirstOrDefault(argument =>
        argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (configured is null)
    {
        return false;
    }

    var path = configured[prefix.Length..].Trim().Trim('"');
    if (path.Length == 0)
    {
        throw new ArgumentException(
            "The schedule smoke-state path is required.");
    }

    var now = DateTime.Now;
    var sleepMinute = (now.Hour * 60) + now.Minute;
    var wakeMinute =
        (sleepMinute + 1) % MonitoringSchedule.MinutesPerDay;
    await new EncryptedStateStore(path).SaveAsync(
        new PeggyStateDocument
        {
            Settings = new PeggySettings
            {
                StartAtSignIn = true,
                MonitoringScheduleEnabled = true,
                MonitoringWakeMinute = wakeMinute,
                MonitoringSleepMinute = sleepMinute,
                SpeechEnabled = false
            }
        });
    Console.WriteLine(
        $"Created isolated schedule state: sleep=" +
        $"{MonitoringSchedule.FormatMinute(sleepMinute)}, wake=" +
        $"{MonitoringSchedule.FormatMinute(wakeMinute)}.");
    return true;
}

static async Task TestEncryptedStateAsync(string directory)
{
    var path = Path.Combine(directory, "state.dat");
    var store = new EncryptedStateStore(path);
    const string marker = "private-summary-marker";
    var state = new PeggyStateDocument
    {
        MonitoringCursor = new MonitoringCursorState
        {
            Initialized = true,
            TeamsWatermarkUtc = DateTimeOffset.UtcNow,
            TeamsBoundaryMessageIds = ["boundary-message"],
            TeamsSearchWindowEndUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            TeamsSearchOffset = 25,
            TeamsSearchProcessedHitIds = ["processed-search-hit"],
            PromptWatermarkUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            OutlookWatermarkUtc = DateTimeOffset.UtcNow,
            OutlookBoundaryMessageIds = ["outlook-boundary-message"],
            ChannelScanResumeKey = "channel-resume-key",
            ChannelDeltaLinks =
                new Dictionary<string, string>
                {
                    ["channel"] = "/opaque-delta-link"
                },
            ChannelPageSizes =
                new Dictionary<string, int>
                {
                    ["channel"] = 25
                },
            ChannelContinuationPass = true
        },
        Alerts =
        [
            new AlertRecord
            {
                ThreadId = "thread",
                Summary = marker,
                Reason = "reason",
                Tier = AlertTier.Attention,
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastActivityUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            }
        ],
        AuditLog =
        [
            new AuditLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow.AddDays(-31),
                Action = WorkActionKind.MarkMessageRead,
                ApprovalMethod = ApprovalMethod.Click,
                Result = "old",
                Verified = true
            },
            new AuditLogEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Action = WorkActionKind.MarkMessageRead,
                ApprovalMethod = ApprovalMethod.Click,
                Result = "current",
                Verified = true
            }
        ]
    };

    await store.SaveAsync(state);
    var ciphertext = await File.ReadAllBytesAsync(path);
    Assert(
        !Encoding.UTF8.GetString(ciphertext).Contains(
            marker,
            StringComparison.Ordinal),
        "Encrypted state exposed plaintext.");

    var loaded = await store.LoadAsync();
    Assert(loaded.Alerts.Single().Summary == marker, "State did not round-trip.");
    Assert(
        loaded.MonitoringCursor.TeamsBoundaryMessageIds
            .SequenceEqual(["boundary-message"]),
        "The Teams boundary cursor did not round-trip.");
    Assert(
        loaded.MonitoringCursor.TeamsSearchWindowEndUtc is not null &&
        loaded.MonitoringCursor.TeamsSearchOffset == 25 &&
        loaded.MonitoringCursor.TeamsSearchProcessedHitIds
            .SequenceEqual(["processed-search-hit"]),
        "The structured Teams search continuation did not round-trip.");
    Assert(
        loaded.MonitoringCursor.PromptWatermarkUtc is not null,
        "The legacy Teams monitoring cursor did not round-trip.");
    Assert(
        loaded.MonitoringCursor.OutlookBoundaryMessageIds
            .SequenceEqual(["outlook-boundary-message"]),
        "The Outlook boundary cursor did not round-trip.");
    Assert(
        loaded.MonitoringCursor.ChannelScanResumeKey == "channel-resume-key",
        "The Teams channel resume cursor did not round-trip.");
    Assert(
        loaded.MonitoringCursor.ChannelDeltaLinks["channel"] ==
        "/opaque-delta-link",
        "The Teams channel delta link did not round-trip.");
    Assert(
        loaded.MonitoringCursor.ChannelPageSizes["channel"] == 25,
        "The Teams channel page metadata did not round-trip.");
    Assert(
        loaded.MonitoringCursor.ChannelContinuationPass,
        "The Teams channel continuation phase did not round-trip.");
    Assert(
        loaded.MonitoringCursor.InitialOutlookScanComplete,
        "Existing initialized state did not migrate its Outlook cursor.");
    Assert(loaded.AuditLog.Count == 1, "Audit retention was not pruned.");
}

static async Task TestMockMonitoringAndActionsAsync()
{
    var provider = new MockMessageProvider();
    var batch = await provider.GetChangesAsync(
        new MonitoringCursorState(),
        100,
        DateTimeOffset.Now);
    Assert(batch.Cursor.Initialized, "Mock cursor was not initialized.");
    Assert(batch.Messages.Count > 0, "Initial monitoring returned no messages.");
    Assert(
        batch.Messages.All(message =>
            message.ReceivedAt > DateTimeOffset.Now.AddMinutes(
                -PeggyDefaults.StartupLookbackMinutes - 1)),
        "Initial monitoring returned data outside the startup window.");

    var message = batch.Messages.First(item => item.Source == MessageSource.Email);
    var read = await provider.ExecuteActionAsync(new WorkActionRequest
    {
        Kind = WorkActionKind.MarkMessageRead,
        TargetId = message.Id
    });
    Assert(read.Success && read.Verified, "Mock mark-read was not verified.");

    var draft = await provider.ExecuteActionAsync(new WorkActionRequest
    {
        Kind = WorkActionKind.CreateReplyDraft,
        RelatedMessageId = message.Id,
        Body = "Thanks, I will review this."
    });
    Assert(draft.Success && draft.Verified, "Mock draft was not verified.");
}

static void TestActionPolicy()
{
    var email = WorkActionPolicy.NormalizeAndValidate(new WorkActionRequest
    {
        Kind = WorkActionKind.SendEmail,
        Subject = "Status",
        Body = "The review is complete.",
        Recipients = ["person@example.com"]
    });
    Assert(
        email.ExactDescription.Contains(
            "person@example.com",
            StringComparison.Ordinal),
        "Email preview omitted its recipient.");

    var destructive = WorkActionPolicy.NormalizeAndValidate(
        new WorkActionRequest
        {
            Kind = WorkActionKind.DeleteMessage,
            TargetId = "email-message:YWJjMTIz"
        });
    Assert(
        destructive.IsDestructive &&
        destructive.TypedConfirmationPhrase.StartsWith(
            "DELETE MESSAGE",
            StringComparison.Ordinal),
        "Destructive confirmation policy was not applied.");
}

static async Task TestLiveReadSurfacesAsync(bool channelsOnly)
{
    var provider = new WorkIqMessageProvider();
    var nowUtc = DateTimeOffset.UtcNow;
    var statePath = Path.Combine(
        Path.GetTempPath(),
        channelsOnly
            ? "peggy-live-channels-5m-state.dat"
            : "peggy-live-read-5m-state.dat");
    var stateStore = new EncryptedStateStore(statePath);
    var state = await stateStore.LoadAsync();
    if (state.MonitoringCursor.InitialScanStartedUtc is null)
    {
        state.MonitoringCursor = channelsOnly
            ? new MonitoringCursorState
            {
                InitialScanStartedUtc =
                    nowUtc.AddMinutes(-PeggyDefaults.StartupLookbackMinutes),
                InitialOutlookScanComplete = true,
                OutlookWatermarkUtc = nowUtc,
                TeamsWatermarkUtc = nowUtc
            }
            : new MonitoringCursorState();
        await stateStore.SaveAsync(state);
    }
    var cursor = state.MonitoringCursor;
    var total = 0;
    var batches = 0;
    bool hasMore;
    do
    {
        var cursorBefore = JsonSerializer.Serialize(cursor);
        var batch = await provider.GetChangesAsync(
            cursor,
            100,
            DateTimeOffset.UtcNow);
        cursor = batch.Cursor;
        state.MonitoringCursor = cursor;
        await stateStore.SaveAsync(state);
        total += batch.Messages.Count;
        batches++;
        hasMore = batch.HasMore;
        Assert(
            batch.Messages.Count <= 100,
            "Live catch-up exceeded its 100-message batch limit.");
        Console.WriteLine(
            $"Live catch-up batch {batches}: {batch.Messages.Count} changes, " +
            $"hasMore={hasMore}.");
        Assert(
            !hasMore ||
            batch.Messages.Count > 0 ||
            !string.Equals(
                cursorBefore,
                JsonSerializer.Serialize(cursor),
                StringComparison.Ordinal),
            "Live catch-up reported more work without advancing its cursor.");
        Assert(batches <= 500, "Live catch-up exceeded 500 batches.");
    }
    while (hasMore);

    Console.WriteLine("Reading unread Outlook email...");
    var unreadEmail = await provider.ListUnreadAsync(
        MessageSource.Email,
        10);
    Console.WriteLine("Reading unread Teams chats...");
    var unreadTeams = await provider.ListUnreadAsync(
        MessageSource.Teams,
        10);
    Console.WriteLine("Reading joined Teams...");
    var teams = await provider.ListJoinedTeamsAsync();
    Console.WriteLine("Reading one team's channels...");
    var channels = teams.Count == 0
        ? Array.Empty<TeamsChannelInfo>()
        : await provider.ListTeamChannelsAsync(
            teams[0].TeamId,
            teams[0].TeamName);
    Console.WriteLine("Reading upcoming calendar events...");
    var events = await provider.ListUpcomingEventsAsync(
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7),
        20);
    Console.WriteLine("Reading incomplete Outlook tasks...");
    var tasks = await provider.ListTasksAsync(
        includeCompleted: false,
        limit: 20);
    Console.WriteLine(
        $"Live read surfaces passed: {total} changes in {batches} batches, " +
        $"{unreadEmail.Count} unread emails, {unreadTeams.Count} unread Teams messages, " +
        $"{teams.Count} teams, {channels.Count} sampled team channels, " +
        $"{events.Count} events, {tasks.Count} tasks.");
    File.Delete(statePath);
}

static async Task TestLiveUnreadAsync()
{
    var provider = new WorkIqMessageProvider();
    var email = await provider.ListUnreadAsync(
        MessageSource.Email,
        10);
    var teams = await provider.ListUnreadAsync(
        MessageSource.Teams,
        10);
    if (email.FirstOrDefault() is { } emailMessage)
    {
        var thread = await provider.GetThreadAsync(
            emailMessage.ThreadId);
        Assert(
            thread is { Messages.Count: > 0 },
            "Live Outlook thread retrieval returned no context.");
    }
    if (teams.FirstOrDefault() is { } teamsMessage)
    {
        var thread = await provider.GetThreadAsync(
            teamsMessage.ThreadId);
        Assert(
            thread is { Messages.Count: > 0 },
            "Live Teams thread retrieval returned no context.");
    }

    Console.WriteLine(
        $"Live unread surfaces passed: {email.Count} email, " +
        $"{teams.Count} Teams, available threads resolved.");
}

static async Task TestLiveMonitoringAsync()
{
    var provider = new WorkIqMessageProvider();
    var cursor = new MonitoringCursorState
    {
        InitialScanStartedUtc = DateTimeOffset.UtcNow.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes)
    };
    var messages = new List<Message>();
    string? warning = null;
    var hasMore = true;
    var batches = 0;
    while (hasMore && batches < 20)
    {
        batches++;
        var batch = await provider.GetChangesAsync(
            cursor,
            100,
            DateTimeOffset.UtcNow);
        cursor = batch.Cursor;
        hasMore = batch.HasMore;
        warning = batch.Warning ?? warning;
        messages.AddRange(batch.Messages);
    }

    Assert(
        !hasMore,
        "Live monitoring did not complete its bounded continuation.");
    Assert(
        messages
            .Where(message => message.Source == MessageSource.Teams)
            .All(message =>
                Uri.TryCreate(
                    message.WebUrl,
                    UriKind.Absolute,
                    out var uri) &&
                uri.AbsolutePath.StartsWith(
                    "/l/message/",
                    StringComparison.OrdinalIgnoreCase)),
        "Live monitoring returned a Teams item without an exact source link.");
    Console.WriteLine(
        $"Live monitoring passed: {messages.Count} exact-source item(s), " +
        $"{batches} batch(es), warning={warning ?? "none"}.");
}

static async Task TestLiveOutlookLinkAsync()
{
    var messageId = Environment.GetEnvironmentVariable(
        "PEGGY_LIVE_OUTLOOK_MESSAGE_ID")?.Trim();
    var account = Environment.GetEnvironmentVariable(
        PeggyDefaults.WorkIqAccountEnvironmentVariable)?.Trim();
    if (string.IsNullOrWhiteSpace(messageId) ||
        string.IsNullOrWhiteSpace(account))
    {
        throw new InvalidOperationException(
            "Set PEGGY_LIVE_OUTLOOK_MESSAGE_ID and PEGGY_WORKIQ_ACCOUNT " +
            "before running the live Outlook-link test.");
    }

    var encodedId = Uri.EscapeDataString(messageId);
    var client = new WorkIqClient(account: account);
    var entity = await client.FetchAsync<JsonElement>(
        $"/me/messages/{encodedId}" +
        "?$select=id,webLink,subject,receivedDateTime,conversationId",
        CancellationToken.None);
    var actualId = entity.GetProperty("id").GetString();
    var webLink = entity.GetProperty("webLink").GetString();
    var subject = entity.GetProperty("subject").GetString();
    var conversationId = entity.GetProperty("conversationId").GetString();
    var receivedAt = entity.GetProperty("receivedDateTime")
        .GetDateTimeOffset();
    var profile = await client.FetchAsync<JsonElement>(
        "/me?$select=mail,userPrincipalName",
        CancellationToken.None);
    var mailbox = profile.TryGetProperty("mail", out var mailProperty)
        ? mailProperty.GetString()
        : null;
    mailbox = string.IsNullOrWhiteSpace(mailbox)
        ? account
        : mailbox;
    Assert(
        string.Equals(actualId, messageId, StringComparison.Ordinal),
        "WorkIQ returned a different Outlook message.");
    Assert(
        !string.IsNullOrWhiteSpace(webLink) &&
        !string.IsNullOrWhiteSpace(conversationId),
        "WorkIQ returned the Outlook message without its source metadata.");

    var statePath = Path.Combine(
        Path.GetTempPath(),
        $"peggy-live-outlook-link-{Guid.NewGuid():N}.dat");
    try
    {
        var state = new PeggyStateManager(new EncryptedStateStore(statePath));
        await state.InitializeAsync();
        await state.PinWorkIqIdentityAsync(account, mailbox);
        var reloadedState = new PeggyStateManager(
            new EncryptedStateStore(statePath));
        await reloadedState.InitializeAsync();
        Assert(
            string.Equals(
                reloadedState.State.PinnedMailbox,
                mailbox,
                StringComparison.OrdinalIgnoreCase),
            "Peggy did not persist WorkIQ's verified mailbox address.");
        var message = new Message(
            actualId!,
            MessageSource.Email,
            conversationId!,
            "Live Outlook source",
            subject ?? "Outlook message",
            "Live source-link test",
            receivedAt,
            IsRead: false)
        {
            WebUrl = webLink
        };
        var decision = new AlertDecision(
            conversationId!,
            AlertTier.Attention,
            "Live Outlook source-link test",
            "Verifies Peggy's production alert-to-browser path.",
            "opaque-test-reference",
            [actualId!]);
        var alert = new AlertManager(reloadedState)
            .Apply([decision], [message], DateTimeOffset.UtcNow)
            .MaterialUpdates
            .Single();
        Assert(
            AlertSourceLink.TryCreate(
                alert.SourceReference,
                out var sourceUri),
            "Peggy rejected WorkIQ's live Outlook source link.");
        var openUri = AlertSourceLauncher.CreateOpenUri(
            sourceUri,
            reloadedState.State.PinnedMailbox);
        Assert(
            openUri.Scheme == Uri.UriSchemeHttps &&
            openUri.Host == "outlook.office.com" &&
            openUri.AbsolutePath.StartsWith(
                $"/mail/{Uri.EscapeDataString(mailbox)}/deeplink/read/",
                StringComparison.Ordinal) &&
            !openUri.AbsoluteUri.Contains(
                "https://https://",
                StringComparison.OrdinalIgnoreCase),
            "Peggy did not produce one canonical account-scoped Outlook link.");
        Assert(
            string.Equals(
                Uri.UnescapeDataString(openUri.Segments[^1]),
                actualId,
                StringComparison.Ordinal),
            "Peggy's browser link did not preserve the exact WorkIQ message ID.");

        var outputPath = Environment.GetEnvironmentVariable(
            "PEGGY_LIVE_OUTLOOK_LINK_OUTPUT");
        var browserResultPath = Environment.GetEnvironmentVariable(
            "PEGGY_LIVE_OUTLOOK_BROWSER_RESULT");
        if (string.IsNullOrWhiteSpace(outputPath) ||
            string.IsNullOrWhiteSpace(browserResultPath))
        {
            throw new InvalidOperationException(
                "Set PEGGY_LIVE_OUTLOOK_LINK_OUTPUT and " +
                "PEGGY_LIVE_OUTLOOK_BROWSER_RESULT for the live browser test.");
        }
        outputPath = Path.GetFullPath(outputPath);
        browserResultPath = Path.GetFullPath(browserResultPath);
        File.Delete(browserResultPath);
        var runId = Guid.NewGuid().ToString("N");
        var expectedSubjectHash = ComputeSha256(subject ?? string.Empty);
        var expectedUriHash = ComputeSha256(openUri.AbsoluteUri);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(new
            {
                runId,
                uri = openUri.AbsoluteUri,
                expectedSubjectHash
            }));
        AlertSourceLauncher.Open(
            sourceUri,
            reloadedState.State.PinnedMailbox);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!File.Exists(browserResultPath) &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500);
        }
        Assert(
            File.Exists(browserResultPath),
            "Browser verification did not finish within three minutes.");
        using var browserResult = JsonDocument.Parse(
            await File.ReadAllTextAsync(browserResultPath));
        var root = browserResult.RootElement;
        var actualRunId = root.GetProperty("runId").GetString();
        var navigatedUriHash =
            root.GetProperty("navigatedUriHash").GetString();
        var renderedSubjectHash =
            root.GetProperty("renderedSubjectHash").GetString();
        var unauthorizedFound =
            root.GetProperty("unauthorizedFound").GetBoolean();
        var getItemStatuses = root.GetProperty("getItemStatuses")
            .EnumerateArray()
            .Select(status => status.GetInt32())
            .ToList();
        Assert(
            string.Equals(actualRunId, runId, StringComparison.Ordinal) &&
            string.Equals(
                navigatedUriHash,
                expectedUriHash,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                renderedSubjectHash,
                expectedSubjectHash,
                StringComparison.OrdinalIgnoreCase) &&
            !unauthorizedFound &&
            getItemStatuses.Count > 0 &&
            getItemStatuses.All(status => status == 200),
            "The authenticated Outlook browser did not render the expected " +
            "message with HTTP 200 GetItem responses.");
        Console.WriteLine(
            "Live Outlook link end-to-end test passed: correct message, " +
            "GetItem HTTP 200, no 401.");
    }
    finally
    {
        File.Delete(statePath);
    }
}

static string ComputeSha256(string value)
    => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool TryRunFakeWorkIq(string[] arguments)
{
    var configuredScenario = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_SCENARIO");
    if (string.Equals(
            configuredScenario,
            "setup",
            StringComparison.Ordinal) &&
        arguments.Length > 0 &&
        (arguments[0] == "accept-eula" ||
         arguments[0] == "version" ||
         arguments[0] == "auth"))
    {
        WriteFakeWorkIqLog(string.Join(" ", arguments));
        if (arguments[0] == "version")
        {
            Console.WriteLine("1.0.0-test");
        }
        return true;
    }

    if (arguments.Length > 0 &&
        string.Equals(
            arguments[0],
            "mcp",
            StringComparison.Ordinal))
    {
        RunFakeWorkIqMcp();
        return true;
    }

    if (arguments.Length > 0 &&
        string.Equals(
            arguments[0],
            "ask",
            StringComparison.Ordinal))
    {
        var questionIndex = Array.IndexOf(arguments, "--question");
        var question = questionIndex >= 0 &&
                       questionIndex + 1 < arguments.Length
            ? arguments[questionIndex + 1]
            : string.Empty;
        var isDiscovery = question.StartsWith(
            "Search Microsoft Teams chats and channel messages",
            StringComparison.Ordinal);
        WriteFakeWorkIqLog(
            $"{(isDiscovery ? "ask" : "ask-context")} {question}");
        var answer = Environment.GetEnvironmentVariable(
            isDiscovery
                ? "PEGGY_FAKE_WORKIQ_ANSWER"
                : "PEGGY_FAKE_WORKIQ_CONTEXT_ANSWER")
            ?? (isDiscovery
                ? """{"items":[]}"""
                : "Found exact source context.");
        var targetLink = Environment.GetEnvironmentVariable(
            isDiscovery
                ? "PEGGY_FAKE_WORKIQ_DISCOVERY_REFERENCE"
                : "PEGGY_FAKE_WORKIQ_REFERENCE");
        var references = string.IsNullOrWhiteSpace(targetLink)
            ? Array.Empty<object>()
            :
            [
                new
                {
                    targetLink,
                    isCitedInResponse =
                        !isDiscovery
                }
            ];
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isError = false,
            response = answer,
            conversationId = "fake-context-conversation",
            references
        }));
        return true;
    }

    if (arguments.Length == 0 ||
        arguments[0] is not (
            "fetch" or
            "call-function" or
            "create" or
            "update" or
            "do-action" or
            "delete"))
    {
        return false;
    }

    var urlOption = arguments[0] == "fetch" ? "--urls" : "--url";
    var urlIndex = Array.IndexOf(arguments, urlOption);
    if (urlIndex < 0 ||
        urlIndex + 1 >= arguments.Length)
    {
        return false;
    }

    var url = arguments[urlIndex + 1];
    var bodyIndex = Array.IndexOf(arguments, "--body");
    var body = bodyIndex >= 0 && bodyIndex + 1 < arguments.Length
        ? arguments[bodyIndex + 1]
        : null;
    var scenario = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_SCENARIO");
    if (string.Equals(
            scenario,
            "setup",
            StringComparison.Ordinal))
    {
        WriteFakeWorkIqLog($"{arguments[0]} {url}");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id = "user-me",
            mail = Environment.GetEnvironmentVariable(
                       "PEGGY_FAKE_WORKIQ_MAIL") ??
                   PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        }));
        return true;
    }

    if (string.Equals(
            scenario,
            "actions",
            StringComparison.Ordinal))
    {
        HandleFakeActionRequest(arguments[0], url, body);
        return true;
    }

    if (string.Equals(
            scenario,
            "outlook-self-filter",
            StringComparison.Ordinal))
    {
        HandleFakeOutlookSelfFilterRequest(arguments[0], url);
        return true;
    }

    if (string.Equals(
            scenario,
            "outlook-folder-pagination",
            StringComparison.Ordinal))
    {
        HandleFakeOutlookFolderPaginationRequest(arguments[0], url);
        return true;
    }

    if (scenario is "semantic" or "structured-search")
    {
        HandleFakeSemanticReadRequest(arguments[0], url);
        return true;
    }

    if (scenario is "chat-metadata" or "chat-edit" or
        "selected-chat-scope")
    {
        HandleFakeChatActivityRequest(
            arguments[0],
            url,
            isContentEdit: scenario != "chat-metadata",
            multipleMessages: scenario == "selected-chat-scope");
        return true;
    }

    if (string.Equals(
            scenario,
            "channel-delta",
            StringComparison.Ordinal))
    {
        HandleFakeChannelDeltaRequest(arguments[0], url);
        return true;
    }

    if (!url.StartsWith("peggy-test|", StringComparison.Ordinal))
    {
        return false;
    }

    var parts = url.Split('|', 3);
    var mode = parts[1];
    var counterPath = parts[2];
    var attempts = File.Exists(counterPath)
        ? int.Parse(File.ReadAllText(counterPath))
        : 0;
    attempts++;
    File.WriteAllText(counterPath, attempts.ToString());
    if (mode == "eula")
    {
        Console.WriteLine(
            "In order to use this tool you must accept the End User License " +
            "Agreement. Run workiq accept-eula.");
        return true;
    }
    if (attempts == 1)
    {
        var statusCode = mode == "read500" ? 500 : 504;
        Console.WriteLine(
            "{\"results\":[{\"statusCode\":" + statusCode +
            ",\"error\":{\"message\":\"temporary service failure\"}}]}");
        return true;
    }

    Console.WriteLine("""{"value":"ok"}""");
    return true;
}

static void HandleFakeOutlookSelfFilterRequest(
    string command,
    string url)
{
    WriteFakeWorkIqLog($"{command} {url}");
    var activityUtc = DateTimeOffset.TryParse(
        Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_OUTLOOK_ACTIVITY_UTC"),
        out var configuredActivity)
        ? configuredActivity
        : DateTimeOffset.UtcNow;
    var currentUserAddress =
        Environment.GetEnvironmentVariable("PEGGY_FAKE_WORKIQ_MAIL") ??
        PeggyDefaults.ConfiguredWorkIqAccount;
    const string currentUserLegacyAddress =
        "/O=EXCHANGELABS/OU=EXCHANGE ADMINISTRATIVE GROUP/" +
        "CN=RECIPIENTS/CN=CURRENT-USER";
    var external = FakeMonitoredOutlookMessage(
        "external-message",
        "External sender",
        "external@example.com",
        "external@example.com",
        activityUtc.AddMinutes(-3),
        isRead: false);
    var directSelf = FakeMonitoredOutlookMessage(
        "direct-self-message",
        "Current user",
        currentUserLegacyAddress,
        currentUserLegacyAddress,
        activityUtc.AddMinutes(-2),
        isRead: false);
    var delegatedSelf = FakeMonitoredOutlookMessage(
        "delegated-self-message",
        "Support mailbox",
        "support@example.com",
        currentUserLegacyAddress,
        activityUtc.AddMinutes(-1.5),
        isRead: true);

    object response;
    if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = currentUserAddress,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount,
            proxyAddresses = new[]
            {
                $"SMTP:{currentUserAddress}",
                $"X500:{currentUserLegacyAddress}"
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "inbox",
            displayName = "Inbox",
            childFolderCount = 1
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "cases",
                    displayName = "Cases",
                    parentFolderId = "inbox",
                    childFolderCount = 1
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/cases/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "active",
                    displayName = "Active",
                    parentFolderId = "cases",
                    childFolderCount = 0
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox/messages?",
                 StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/active/messages?",
                 StringComparison.Ordinal))
    {
        response = new Dictionary<string, object?>
        {
            ["value"] = new[] { directSelf, delegatedSelf },
            ["@odata.nextLink"] =
                "https://graph.microsoft.com/v1.0/" +
                "peggy-test-outlook-self-next"
        };
    }
    else if (url.StartsWith(
                 "/peggy-test-outlook-self-next",
                 StringComparison.Ordinal))
    {
        response = new { value = new[] { external } };
    }
    else if (url.StartsWith(
                 "/me/messages?$filter=",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[] { external, directSelf, delegatedSelf }
        };
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected Outlook self-filter fake WorkIQ URL: {url}");
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
}

static object FakeMonitoredOutlookMessage(
    string id,
    string fromName,
    string fromAddress,
    string senderAddress,
    DateTimeOffset receivedDateTime,
    bool isRead)
    => new
    {
        id,
        conversationId = "case-thread",
        from = new
        {
            emailAddress = new
            {
                name = fromName,
                address = fromAddress
            }
        },
        sender = new
        {
            emailAddress = new
            {
                name = fromName,
                address = senderAddress
            }
        },
        subject = "Support escalation",
        bodyPreview = "A monitored support message.",
        webLink =
            $"https://outlook.office.com/mail/deeplink/read/{id}",
        receivedDateTime,
        isRead
    };

static void HandleFakeChannelDeltaRequest(
    string command,
    string url)
{
    WriteFakeWorkIqLog($"{command} {url}");
    if ((url.Contains(
             "/messages/delta()",
             StringComparison.Ordinal) ||
         url.Contains(
             "peggy-test-channel-",
             StringComparison.Ordinal)) &&
        !string.Equals(
            command,
            "call-function",
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Channel delta requests must use WorkIQ call-function.");
    }

    object response;
    if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = Environment.GetEnvironmentVariable(
                       "PEGGY_FAKE_WORKIQ_MAIL") ??
                   PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "inbox",
            displayName = "Inbox",
            childFolderCount = 1
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "cases",
                    displayName = "Cases",
                    parentFolderId = "inbox",
                    childFolderCount = 1
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/cases/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "active",
                    displayName = "Active",
                    parentFolderId = "cases",
                    childFolderCount = 0
                }
            }
        };
    }
    else if (url.Contains(
                 "/messages?",
                 StringComparison.Ordinal) &&
             url.StartsWith(
                 "/me/mailFolders/inbox/",
                 StringComparison.Ordinal) &&
             url.Contains(
                 "isRead%20eq%20false",
                 StringComparison.OrdinalIgnoreCase))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "unread-with-link",
                    conversationId = "conversation-with-link",
                    from = new
                    {
                        emailAddress = new
                        {
                            name = "Outlook sender",
                            address = "sender@example.com"
                        }
                    },
                    subject = "Outlook source link",
                    bodyPreview = "Open the original message.",
                    webLink =
                        "https://outlook.office.com/mail/deeplink/read/unread-with-link",
                    receivedDateTime = DateTimeOffset.UtcNow.AddMinutes(-1),
                    isRead = false
                }
            }
        };
    }
    else if (url.Contains(
                 "/messages?",
                 StringComparison.Ordinal) &&
             url.StartsWith(
                 "/me/mailFolders/",
                 StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith("/me/chats?", StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith(
                 "/me/joinedTeams?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "team-1",
                    displayName = "Test team"
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/teams/team-1/channels?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = Enumerable.Range(1, 3)
                .Select(index => new
                {
                    id = $"channel-{index}",
                    displayName = $"Test channel {index}"
                })
                .ToArray()
        };
    }
    else if (url.StartsWith(
                 "/teams/team-1/channels/channel-1?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "channel-1",
            displayName = "Test channel 1"
        };
    }
    else if (url.Contains(
                 "/messages/root-edit-channel-1/replies?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                FakeChannelThreadMessage(
                    "reply-context",
                    "root-edit-channel-1",
                    "Reply context")
            }
        };
    }
    else if (url.Contains(
                 "/messages/root-edit-channel-1",
                 StringComparison.Ordinal) &&
             !url.Contains(
                 "/replies",
                 StringComparison.Ordinal))
    {
        response = FakeChannelThreadMessage(
            "root-edit-channel-1",
            replyToId: null,
            body: "Root context");
    }
    else if (url.Contains(
                 "peggy-test-expired-token",
                 StringComparison.Ordinal))
    {
        response = new
        {
            results = new[]
            {
                new
                {
                    statusCode = 410,
                    error = new
                    {
                        message = "The delta token has expired."
                    }
                }
            }
        };
    }
    else if (url.Contains(
                 "peggy-test-channel-next",
                 StringComparison.Ordinal))
    {
        var channelId = GetFakeChannelId(url);
        response = FakeChannelPage(
            $"root-edit-{channelId}",
            $"Second opaque page {channelId}",
            nextLink: null,
            deltaLink:
                $"https://graph.microsoft.com/v1.0/" +
                $"peggy-test-channel-delta-{channelId}" +
                "?$deltatoken=opaque-delta");
    }
    else if (url.Contains(
                 "peggy-test-channel-delta",
                 StringComparison.Ordinal))
    {
        response = new Dictionary<string, object?>
        {
            ["value"] = Array.Empty<object>(),
            ["@odata.deltaLink"] =
                "https://graph.microsoft.com/v1.0/" +
                $"peggy-test-channel-delta-{GetFakeChannelId(url)}-2" +
                "?$deltatoken=opaque-delta-2"
        };
    }
    else if (url.Contains(
                 "/messages/delta()",
                 StringComparison.Ordinal))
    {
        var channelId = GetFakeChannelId(url);
        response = FakeChannelPage(
            $"root-edit-{channelId}",
            $"First opaque page {channelId}",
            nextLink:
                $"https://graph.microsoft.com/v1.0/" +
                $"peggy-test-channel-next-{channelId}" +
                "?$skiptoken=opaque-token",
            deltaLink: null);
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected fake WorkIQ URL: {url}");
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
}

static void HandleFakeSemanticReadRequest(
    string command,
    string url)
{
    WriteFakeWorkIqLog($"{command} {url}");
    object response;
    if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = Environment.GetEnvironmentVariable(
                       "PEGGY_FAKE_WORKIQ_MAIL") ??
                   PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "inbox",
            displayName = "Inbox",
            childFolderCount = 1
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "cases",
                    displayName = "Cases",
                    parentFolderId = "inbox",
                    childFolderCount = 1
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/cases/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "active",
                    displayName = "Active",
                    parentFolderId = "cases",
                    childFolderCount = 0
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/",
                 StringComparison.Ordinal) &&
             url.Contains("/messages?", StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith("/me/chats?", StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.Contains(
                 "/messages/delta()",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "channel-root-1",
                    createdDateTime =
                        DateTimeOffset.UtcNow.AddMinutes(-2),
                    lastModifiedDateTime =
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                    messageType = "message",
                    from = new
                    {
                        user = new
                        {
                            id = "root-sender",
                            displayName = "Root sender"
                        }
                    },
                    body = new
                    {
                        contentType = "text",
                        content = "Channel root"
                    },
                    mentions = Array.Empty<object>(),
                    replies = new[]
                    {
                        FakeStructuredSearchMessage(
                            "channel-message-1",
                            "channel-root-1",
                            "A channel reply needs review.",
                            "https://teams.microsoft.com/l/message/" +
                            "19%3Achannel-1%40thread.tacv2/" +
                            "channel-message-1",
                            senderId: "sender-2",
                            senderName: "Channel sender")
                    }
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/chats/19%3Acontext-chat%40thread.v2/messages/" +
                 "context-message",
                 StringComparison.Ordinal))
    {
        response = FakeChatActivityMessage(
            "context-message",
            isContentEdit: false,
            createdRecently: true);
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected semantic fake WorkIQ URL: {url}");
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
}

static void HandleFakeOutlookFolderPaginationRequest(
    string command,
    string url)
{
    WriteFakeWorkIqLog($"{command} {url}");
    const string inboxId = "opaque-inbox/id==";
    const string encodedInboxId = "opaque-inbox%2Fid%3D%3D";
    var isSecondPage =
        url.Contains("%24skip=50", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("$skip=50", StringComparison.OrdinalIgnoreCase);
    object response;
    if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = inboxId,
            displayName = "Inbox",
            childFolderCount = 97
        };
    }
    else if (url.Contains(
                 "mailFolders('",
                 StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"WorkIQ rejected an unnormalized Outlook URL: {url}");
    }
    else if (url.StartsWith(
                 $"/me/mailFolders/{encodedInboxId}/childFolders?",
                 StringComparison.Ordinal) &&
             isSecondPage)
    {
        response = new
        {
            value = Enumerable.Range(51, 47)
                .Select(index => new
                {
                    id = index == 97 ? "cases" : $"folder-{index}",
                    displayName = index == 97 ? "Cases" : $"Folder {index}",
                    parentFolderId = inboxId,
                    childFolderCount = index == 97 ? 1 : 0
                })
                .ToArray()
        };
    }
    else if (url.StartsWith(
                 $"/me/mailFolders/{encodedInboxId}/childFolders?",
                 StringComparison.Ordinal))
    {
        response = new Dictionary<string, object?>
        {
            ["value"] = Enumerable.Range(1, 50)
                .Select(index => new
                {
                    id = $"folder-{index}",
                    displayName = $"Folder {index}",
                    parentFolderId = inboxId,
                    childFolderCount = 0
                })
                .ToArray(),
            ["@odata.nextLink"] =
                "https://graph.microsoft.com/v1.0/me/" +
                "mailFolders('opaque-inbox/id==')/childFolders" +
                "?%24select=id%2cdisplayName%2cparentFolderId%2c" +
                "childFolderCount&%24top=50&%24skip=50"
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/cases/childFolders?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "active",
                    displayName = "Active",
                    parentFolderId = "cases",
                    childFolderCount = 0
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/active/messages?",
                 StringComparison.Ordinal) &&
             isSecondPage)
    {
        response = new
        {
            value = new[]
            {
                FakeMonitoredOutlookMessage(
                    "paged-message-2",
                    "Second sender",
                    "second@example.com",
                    "second@example.com",
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    isRead: false)
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/active/messages?",
                 StringComparison.Ordinal))
    {
        response = new Dictionary<string, object?>
        {
            ["value"] = new[]
            {
                FakeMonitoredOutlookMessage(
                    "paged-message-1",
                    "First sender",
                    "first@example.com",
                    "first@example.com",
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    isRead: false)
            },
            ["@odata.nextLink"] =
                "https://graph.microsoft.com/v1.0/me/" +
                "mailFolders('active')/messages" +
                "?%24select=id%2cconversationId%2cfrom%2csender%2c" +
                "subject%2cbodyPreview%2creceivedDateTime%2cisRead%2c" +
                "webLink&%24top=50&%24skip=50"
        };
    }
    else if (url.StartsWith(
                 $"/me/mailFolders/{encodedInboxId}/messages?",
                 StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected Outlook pagination fake WorkIQ URL: {url}");
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
}

static void HandleFakeChatActivityRequest(
    string command,
    string url,
    bool isContentEdit,
    bool multipleMessages)
{
    WriteFakeWorkIqLog($"{command} {url}");
    object response;
    if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "inbox",
            displayName = "Inbox",
            childFolderCount = 1
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/inbox/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "cases",
                    displayName = "Cases",
                    parentFolderId = "inbox",
                    childFolderCount = 1
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/cases/childFolders",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = new[]
            {
                new
                {
                    id = "active",
                    displayName = "Active",
                    parentFolderId = "cases",
                    childFolderCount = 0
                }
            }
        };
    }
    else if (url.StartsWith(
                 "/me/mailFolders/",
                 StringComparison.Ordinal) &&
             url.Contains("/messages?", StringComparison.Ordinal))
    {
        response = new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith("/me/chats?", StringComparison.Ordinal))
    {
        response = isContentEdit
            ? new
            {
                value = new[]
                {
                    new
                    {
                        id = "old-chat",
                        topic = "Edited incident chat",
                        chatType = "oneOnOne",
                        viewpoint = new
                        {
                            lastMessageReadDateTime =
                                DateTimeOffset.UtcNow.AddYears(-1)
                        },
                        lastMessagePreview = FakeChatActivityMessage(
                            multipleMessages
                                ? "chat-message-2"
                                : "old-message",
                            isContentEdit: true,
                            createdRecently: true)
                    }
                }
            }
            : new { value = Array.Empty<object>() };
    }
    else if (url.StartsWith(
                 "/me/chats/old-chat/messages?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            value = multipleMessages
                ? new object[]
                {
                    FakeChatActivityMessage(
                        "chat-message-1",
                        isContentEdit,
                        createdRecently: isContentEdit),
                    FakeChatActivityMessage(
                        "chat-message-2",
                        isContentEdit,
                        createdRecently: isContentEdit)
                }
                : new object[]
                {
                    FakeChatActivityMessage(
                        "old-message",
                        isContentEdit,
                        createdRecently: isContentEdit)
                }
        };
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected chat activity fake WorkIQ URL: {url}");
    }

    Console.WriteLine(JsonSerializer.Serialize(response));
}

static object FakeChatActivityMessage(
    string id,
    bool isContentEdit,
    bool createdRecently)
{
    var configuredActivity = DateTimeOffset.TryParse(
        Environment.GetEnvironmentVariable("PEGGY_FAKE_CHAT_ACTIVITY_UTC"),
        out var activity)
        ? activity
        : DateTimeOffset.UtcNow.AddMinutes(-1);
    var created = createdRecently
        ? configuredActivity
        : configuredActivity.AddMonths(-11);
    var modified = configuredActivity;
    return new
    {
        id,
        createdDateTime = created,
        lastModifiedDateTime = modified,
        lastEditedDateTime = isContentEdit
            ? modified
            : (DateTimeOffset?)null,
        messageType = "message",
        from = new
        {
            user = new
            {
                id = "other-user",
                displayName = "Other user"
            }
        },
        body = new
        {
            contentType = "text",
            content = "Old message whose metadata changed today."
        },
        mentions = Array.Empty<object>()
    };
}

static string GetFakeChannelId(string url)
{
    var match = System.Text.RegularExpressions.Regex.Match(
        url,
        @"channel-\d+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success ? match.Value : "channel-1";
}

static Dictionary<string, object?> FakeChannelPage(
    string id,
    string body,
    string? nextLink,
    string? deltaLink)
{
    var activityUtc = DateTimeOffset.TryParse(
        Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_CHANNEL_ACTIVITY_UTC"),
        out var configuredActivity)
        ? configuredActivity
        : DateTimeOffset.UtcNow.AddMinutes(-1);
    var root = new Dictionary<string, object?>
    {
        ["id"] = id,
        ["createdDateTime"] = activityUtc,
        ["lastModifiedDateTime"] = activityUtc,
        ["messageType"] = "message",
        ["subject"] = "Opaque delta test",
        ["webUrl"] =
            $"https://teams.microsoft.com/l/message/channel/{id}",
        ["from"] = new
        {
            user = new
            {
                id = "other-user",
                displayName = "Other user"
            }
        },
        ["body"] = new
        {
            contentType = "text",
            content = body
        },
        ["mentions"] = Array.Empty<object>(),
        ["replies"] = Array.Empty<object>()
    };
    if (ParseFakeChannelEdit() is { } editedUtc)
    {
        root["lastEditedDateTime"] = editedUtc;
        root["lastModifiedDateTime"] = editedUtc;
    }
    if (id.Contains("channel-3", StringComparison.Ordinal))
    {
        root["replies@odata.nextLink"] =
            "https://graph.microsoft.com/v1.0/unavailable-replies";
    }

    var response = new Dictionary<string, object?>
    {
        ["value"] = new[] { root }
    };
    if (nextLink is not null)
    {
        response["@odata.nextLink"] = nextLink;
    }
    if (deltaLink is not null)
    {
        response["@odata.deltaLink"] = deltaLink;
    }

    return response;
}

static object FakeChannelThreadMessage(
    string id,
    string? replyToId,
    string body)
    => new
    {
        id,
        replyToId,
        createdDateTime = DateTimeOffset.UtcNow.AddMinutes(-1),
        lastModifiedDateTime = DateTimeOffset.UtcNow.AddMinutes(-1),
        messageType = "message",
        subject = "Thread context",
        webUrl = $"https://teams.microsoft.com/l/message/channel/{id}",
        from = new
        {
            user = new
            {
                id = "other-user",
                displayName = "Other user"
            }
        },
        body = new
        {
            contentType = "text",
            content = body
        },
        mentions = Array.Empty<object>(),
        replies = Array.Empty<object>()
    };

static void HandleFakeActionRequest(
    string command,
    string url,
    string? body)
    => Console.WriteLine(JsonSerializer.Serialize(
        CreateFakeActionResponse(command, url, body)));

static object CreateFakeActionResponse(
    string command,
    string url,
    string? body)
{
    WriteFakeWorkIqLog(
        command == "do-action" &&
        string.Equals(url, "/search/query", StringComparison.Ordinal)
            ? $"{command} {url} {body}"
            : $"{command} {url}");
    object response;
    if (command == "do-action" &&
        string.Equals(url, "/search/query", StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"entityTypes\":[\"chatMessage\"]",
            "\"queryString\":\"sent");
        response = GetFakeSearchResponse(body);
    }
    else if (url.StartsWith("/me?$select=", StringComparison.Ordinal))
    {
        response = new
        {
            id = "user-me",
            mail = Environment.GetEnvironmentVariable(
                       "PEGGY_FAKE_WORKIQ_MAIL") ??
                   PeggyDefaults.ConfiguredWorkIqAccount,
            userPrincipalName = PeggyDefaults.ConfiguredWorkIqAccount
        };
    }
    else if (command == "update" &&
             url.StartsWith(
                 "/me/messages/mark-id",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(body, "\"isRead\":true");
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages/mark-id?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "mark-id",
            isRead = true
        };
    }
    else if (command == "create" &&
             string.Equals(
                 url,
                 "/me/messages",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"subject\":\"Status\"",
            "\"content\":\"Exact email body\"",
            "\"address\":\"person@example.com\"");
        response = new { id = "email-draft" };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages/email-draft?",
                 StringComparison.Ordinal))
    {
        response = FakeEmail(
            "email-draft",
            "Status",
            "Exact email body",
            isDraft: true,
            "person@example.com");
    }
    else if (command == "do-action" &&
             string.Equals(
                 url,
                 "/me/sendMail",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"subject\":\"Status\"",
            "\"content\":\"Exact email body\"",
            "\"address\":\"person@example.com\"",
            "\"saveToSentItems\":true");
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages?$filter=",
                 StringComparison.Ordinal) &&
             url.Contains("subject%20eq", StringComparison.Ordinal))
    {
        response = GetFakeWorkIqRequestCount($"fetch {url}") == 1
            ? new { value = Array.Empty<object>() }
            : new
        {
            value = new[]
            {
                FakeEmail(
                    "sent-email",
                    "Status",
                    "Exact email body",
                    isDraft: false,
                    "person@example.com",
                    sent: true)
            }
        };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages/original?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "original",
            conversationId = "conversation-1"
        };
    }
    else if (command == "do-action" &&
             url.EndsWith(
                 "/createReply",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(body, "\"comment\":\"Exact reply body\"");
        response = new { id = "reply-draft" };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages/reply-draft?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            id = "reply-draft",
            conversationId = "conversation-1",
            isDraft = true,
            body = new
            {
                contentType = "text",
                content = "Exact reply body"
            }
        };
    }
    else if (command == "do-action" &&
             url.EndsWith("/reply", StringComparison.Ordinal))
    {
        RequireFakeBody(body, "\"comment\":\"Exact reply body\"");
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages?$filter=",
                 StringComparison.Ordinal) &&
             url.Contains(
                 "conversationId%20eq",
                 StringComparison.Ordinal))
    {
        response = GetFakeWorkIqRequestCount($"fetch {url}") == 1
            ? new { value = Array.Empty<object>() }
            : new
        {
            value = new[]
            {
                FakeEmail(
                    "sent-reply",
                    string.Empty,
                    "Exact reply body",
                    isDraft: false,
                    sent: true)
            }
        };
    }
    else if (command == "create" &&
             url.StartsWith(
                 "/me/chats/chat-1/messages",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(body, "\"content\":\"Exact Teams message\"");
        response = new { id = "chat-message-1" };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/chats/chat-1/messages/chat-message-1",
                 StringComparison.Ordinal))
    {
        response = FakeTeamsMessage(
            "chat-message-1",
            "Exact Teams message");
    }
    else if (command == "create" &&
             url.Contains(
                 "/teams/team-1/channels/channel-1/messages/root-1/replies",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(body, "\"content\":\"Exact channel reply\"");
        response = new { id = "channel-message-1" };
    }
    else if (command == "fetch" &&
             url.Contains(
                 "/teams/team-1/channels/channel-1/messages/root-1/" +
                 "replies/channel-message-1",
                 StringComparison.Ordinal))
    {
        response = FakeTeamsMessage(
            "channel-message-1",
            "Exact channel reply");
    }
    else if (command == "create" &&
             string.Equals(url, "/me/events", StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"subject\":\"Created event\"",
            "\"content\":\"Created agenda\"",
            "\"address\":\"person@example.com\"");
        response = new { id = "event-created" };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/events/event-created?",
                 StringComparison.Ordinal))
    {
        response = FakeCalendarEvent(
            "event-created",
            "Created event",
            "Created agenda",
            "2030-01-02T10:00:00",
            "2030-01-02T11:00:00",
            ["person@example.com"],
            isCancelled: false);
    }
    else if (command == "update" &&
             url.StartsWith(
                 "/me/events/event-updated",
                 StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"subject\":\"Updated event\"",
            "\"content\":\"Updated agenda\"",
            "\"address\":\"person@example.com\"");
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/events/event-updated?",
                 StringComparison.Ordinal))
    {
        response = FakeCalendarEvent(
            "event-updated",
            "Updated event",
            "Updated agenda",
            "2030-01-02T12:00:00",
            "2030-01-02T13:00:00",
            ["person@example.com"],
            isCancelled: false);
    }
    else if (command == "do-action" &&
             url.EndsWith("/cancel", StringComparison.Ordinal))
    {
        RequireFakeBody(
            body,
            "\"comment\":\"Cancelled by the organizer.\"");
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/events/event-cancelled?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            results = new[]
            {
                new
                {
                    statusCode = 404,
                    error = new
                    {
                        message = "ErrorItemNotFound"
                    }
                }
            }
        };
    }
    else if (command == "delete" &&
             url.StartsWith(
                 "/me/messages/delete-id",
                 StringComparison.Ordinal))
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                "Delete unexpectedly included a request body.");
        }
        response = new { };
    }
    else if (command == "fetch" &&
             url.StartsWith(
                 "/me/messages/delete-id?",
                 StringComparison.Ordinal))
    {
        response = new
        {
            results = new[]
            {
                new
                {
                    statusCode = 404,
                    error = new
                    {
                        message = "ErrorItemNotFound"
                    }
                }
            }
        };
    }
    else
    {
        throw new InvalidOperationException(
            $"Unexpected fake action request: {command} {url}");
    }

    return response;
}

static void RunFakeWorkIqMcp()
{
    var commandLine = Environment.CommandLine;
    if (new[]
        {
            "Exact email body",
            "Exact reply body",
            "Exact Teams message",
            "Created agenda",
            "Updated agenda",
            "person@example.com",
            "approved once"
        }.Any(value => commandLine.Contains(
            value,
            StringComparison.Ordinal)))
    {
        throw new InvalidOperationException(
            "Private mutation content leaked into the WorkIQ command line.");
    }

    while (Console.ReadLine() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("method", out var method))
        {
            continue;
        }
        var methodName = method.GetString();
        if (string.Equals(
                methodName,
                "notifications/initialized",
                StringComparison.Ordinal))
        {
            continue;
        }
        if (!root.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            continue;
        }
        if (string.Equals(
                methodName,
                "initialize",
                StringComparison.Ordinal))
        {
            WriteFakeMcpResponse(
                id,
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new
                    {
                        tools = new { }
                    },
                    serverInfo = new
                    {
                        name = "fake-workiq",
                        version = "1.0"
                    }
                });
            continue;
        }
        if (!string.Equals(
                methodName,
                "tools/call",
                StringComparison.Ordinal))
        {
            WriteFakeMcpError(
                id,
                -32601,
                $"Unsupported fake MCP method '{methodName}'.");
            continue;
        }

        var call = root.GetProperty("params");
        var tool = call.GetProperty("name").GetString()
            ?? string.Empty;
        var toolArguments = call.GetProperty("arguments");
        if (string.Equals(tool, "ask", StringComparison.Ordinal))
        {
            var question = toolArguments.GetProperty(
                "question").GetString() ?? string.Empty;
            WriteFakeWorkIqLog($"ask {question}");
            var answer = Environment.GetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_ANSWER")
                ?? """{"items":[]}""";
            WriteFakeMcpAskSuccess(
                id,
                answer);
            continue;
        }
        if (string.Equals(tool, "fetch", StringComparison.Ordinal))
        {
            var urls = toolArguments
                .GetProperty("entityUrls")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
            var responses = urls
                .Select(url =>
                {
                    WriteFakeWorkIqLog($"fetch {url}");
                    return CreateFakeStructuredSearchFetchResponse(url);
                })
                .ToList();
            WriteFakeMcpBatchSuccess(id, responses);
            continue;
        }
        var (command, urlParameter) = tool switch
        {
            "create_entity" => ("create", "parentUrl"),
            "update_entity" => ("update", "entityUrl"),
            "do_action" => ("do-action", "actionUrl"),
            "delete_entity" => ("delete", "entityUrl"),
            _ => (string.Empty, string.Empty)
        };
        if (command.Length == 0)
        {
            WriteFakeMcpError(
                id,
                -32602,
                $"Unsupported fake WorkIQ tool '{tool}'.");
            continue;
        }

        var url = toolArguments.GetProperty(urlParameter).GetString()
            ?? string.Empty;
        string? body = null;
        if (toolArguments.TryGetProperty(
                "jsonBody",
                out var jsonBody) &&
            jsonBody.ValueKind is not (
                JsonValueKind.Null or
                JsonValueKind.Undefined))
        {
            body = jsonBody.GetRawText();
        }
        if (url.StartsWith(
                "peggy-test|",
                StringComparison.Ordinal))
        {
            var parts = url.Split('|', 3);
            var counterPath = parts[2];
            var attempts = File.Exists(counterPath)
                ? int.Parse(File.ReadAllText(counterPath))
                : 0;
            attempts++;
            File.WriteAllText(
                counterPath,
                attempts.ToString());
            if (attempts == 1)
            {
                WriteFakeMcpToolError(
                    id,
                    504,
                    "temporary gateway timeout");
            }
            else
            {
                WriteFakeMcpToolSuccess(
                    id,
                    new Dictionary<string, string>
                    {
                        ["value"] = "ok"
                    });
            }
            continue;
        }

        if (string.Equals(tool, "do_action", StringComparison.Ordinal) &&
            string.Equals(url, "/search/query", StringComparison.Ordinal) &&
            ShouldCrashFakeSearch())
        {
            WriteFakeWorkIqLog($"{command} {url} {body}");
            Environment.Exit(70);
        }

        if (string.Equals(tool, "do_action", StringComparison.Ordinal) &&
            string.Equals(url, "/search/query", StringComparison.Ordinal) &&
            ShouldFailFakeSearch())
        {
            WriteFakeWorkIqLog($"{command} {url} {body}");
            WriteFakeMcpToolError(
                id,
                503,
                "temporary structured search failure");
            continue;
        }

        var response = CreateFakeActionResponse(
            command,
            url,
            body);
        if (string.Equals(tool, "do_action", StringComparison.Ordinal) &&
            string.Equals(url, "/search/query", StringComparison.Ordinal))
        {
            WriteFakeMcpDirectToolSuccess(id, response);
        }
        else
        {
            WriteFakeMcpToolSuccess(id, response);
        }
    }
}

static object GetFakeSearchResponse(string? body)
{
    var offset = 0;
    if (!string.IsNullOrWhiteSpace(body))
    {
        using var request = JsonDocument.Parse(body);
        var requests = request.RootElement.GetProperty("requests");
        if (requests.GetArrayLength() > 0 &&
            requests[0].TryGetProperty("from", out var from) &&
            from.TryGetInt32(out var parsedOffset))
        {
            offset = parsedOffset;
        }
    }

    var configured =
        Environment.GetEnvironmentVariable(
            $"PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE_{offset}") ??
        Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_WORKIQ_SEARCH_RESPONSE") ??
        """
        {"value":[{"searchTerms":[],"hitsContainers":[{"total":0,"moreResultsAvailable":false,"hits":[]}]}]}
        """;
    return JsonSerializer.Deserialize<JsonElement>(configured).Clone();
}

static bool ShouldFailFakeSearch()
{
    if (!int.TryParse(
            Environment.GetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_FAIL_COUNT"),
            out var failures) ||
        failures <= 0)
    {
        return false;
    }

    var counterPath = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_SEARCH_COUNTER");
    if (string.IsNullOrWhiteSpace(counterPath))
    {
        throw new InvalidOperationException(
            "A fake search retry counter path is required.");
    }

    var attempts = File.Exists(counterPath)
        ? int.Parse(File.ReadAllText(counterPath))
        : 0;
    attempts++;
    File.WriteAllText(counterPath, attempts.ToString());
    return attempts <= failures;
}

static bool ShouldCrashFakeSearch()
{
    if (!int.TryParse(
            Environment.GetEnvironmentVariable(
                "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNT"),
            out var failures) ||
        failures <= 0)
    {
        return false;
    }

    var counterPath = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_SEARCH_CRASH_COUNTER");
    if (string.IsNullOrWhiteSpace(counterPath))
    {
        throw new InvalidOperationException(
            "A fake search crash counter path is required.");
    }

    var attempts = File.Exists(counterPath)
        ? int.Parse(File.ReadAllText(counterPath))
        : 0;
    attempts++;
    File.WriteAllText(counterPath, attempts.ToString());
    return attempts <= failures;
}

static object? CreateFakeStructuredSearchFetchResponse(string url)
{
    if (url.Contains(
            "/me/chats/19%3Astale-chat%40thread.v2/messages/" +
            "stale-message",
            StringComparison.Ordinal))
    {
        return null;
    }
    if (url.Contains(
            "/me/chats/19%3Acontext-chat%40thread.v2/messages/" +
            "context-message",
            StringComparison.Ordinal))
    {
        return FakeStructuredSearchMessage(
            "context-message",
            replyToId: null,
            "A decision is requested.",
            "https://teams.microsoft.com/l/message/" +
            "19%3Acontext-chat%40thread.v2/context-message" +
            "?context=%7B%22contextType%22%3A%22chat%22%7D",
            senderId: "sender@example.com",
            senderName: "Structured sender",
            senderAsEmail: true);
    }
    if (url.Contains(
            "/me/chats/19%3Aself-chat%40thread.v2/messages/self-message",
            StringComparison.Ordinal))
    {
        var primaryMail = Environment.GetEnvironmentVariable(
                              "PEGGY_FAKE_WORKIQ_MAIL") ??
                          PeggyDefaults.ConfiguredWorkIqAccount;
        return FakeStructuredSearchMessage(
            "self-message",
            replyToId: null,
            "The current user's own message.",
            "https://teams.microsoft.com/l/message/" +
            "19%3Aself-chat%40thread.v2/self-message" +
            "?context=%7B%22contextType%22%3A%22chat%22%7D",
            senderId: primaryMail,
            senderName: "Current user",
            senderAsEmail: true);
    }
    if (url.Contains(
            "/messages/channel-root-1/replies/channel-message-1",
            StringComparison.Ordinal))
    {
        return FakeStructuredSearchMessage(
            "channel-message-1",
            "channel-root-1",
            "A channel reply needs review.",
            "https://teams.microsoft.com/l/message/" +
            "19%3Achannel-1%40thread.tacv2/channel-message-1" +
            "?parentMessageId=channel-root-1&groupId=team-1",
            senderId: "sender-2",
            senderName: "Channel sender");
    }
    if (url.EndsWith(
            "/messages/channel-message-1",
            StringComparison.Ordinal))
    {
        return FakeStructuredSearchMessage(
            "not-the-requested-message",
            replyToId: null,
            "Direct reply lookup did not resolve.",
            webUrl: string.Empty,
            senderId: "sender-2",
            senderName: "Channel sender");
    }
    if (url.Contains(
            "/messages/root-edit-channel-1",
            StringComparison.Ordinal))
    {
        return FakeStructuredSearchMessage(
            "root-edit-channel-1",
            replyToId: null,
            "First opaque page channel-1",
            "https://teams.microsoft.com/l/message/" +
            "channel/root-edit-channel-1",
            senderId: "other-user",
            senderName: "Other user",
            activityUtc: ParseFakeChannelActivity(),
            editedUtc: ParseFakeChannelEdit());
    }
    if (url.Contains(
            "/messages/unlinked-message",
            StringComparison.Ordinal))
    {
        return FakeStructuredSearchMessage(
            "unlinked-message",
            replyToId: null,
            "A source link must be recovered.",
            webUrl: string.Empty,
            senderId: "sender-3",
            senderName: "Structured sender");
    }

    throw new InvalidOperationException(
        $"Unexpected fake structured search fetch URL: {url}");
}

static object FakeStructuredSearchMessage(
    string id,
    string? replyToId,
    string body,
    string webUrl,
    string senderId,
    string senderName,
    DateTimeOffset? activityUtc = null,
    DateTimeOffset? editedUtc = null,
    bool senderAsEmail = false)
{
    var timestamp = activityUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1);
    var from = senderAsEmail
        ? (object)new
        {
            emailAddress = new
            {
                name = senderName,
                address = senderId
            }
        }
        : new
        {
            user = new
            {
                id = senderId,
                displayName = senderName
            }
        };
    return new
    {
        id,
        replyToId,
        createdDateTime = timestamp,
        lastModifiedDateTime = editedUtc ?? timestamp,
        lastEditedDateTime = editedUtc,
        messageType = "message",
        subject = string.Empty,
        webUrl,
        from,
        body = new
        {
            contentType = "text",
            content = body
        },
        mentions = Array.Empty<object>(),
        replies = Array.Empty<object>()
    };
}

static DateTimeOffset ParseFakeChannelActivity()
    => DateTimeOffset.TryParse(
        Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_CHANNEL_ACTIVITY_UTC"),
        out var configuredActivity)
        ? configuredActivity
        : DateTimeOffset.UtcNow.AddMinutes(-1);

static DateTimeOffset? ParseFakeChannelEdit()
    => DateTimeOffset.TryParse(
        Environment.GetEnvironmentVariable(
            "PEGGY_FAKE_CHANNEL_EDITED_UTC"),
        out var configuredActivity)
        ? configuredActivity
        : null;

static void WriteFakeMcpResponse(int id, object result)
    => Console.WriteLine(JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id,
        result
    }));

static void WriteFakeMcpError(
    int id,
    int code,
    string message)
    => Console.WriteLine(JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id,
        error = new
        {
            code,
            message
        }
    }));

static void WriteFakeMcpToolSuccess(
    int id,
    object data)
    => WriteFakeMcpResponse(
        id,
        new
        {
            content = Array.Empty<object>(),
            structuredContent = new
            {
                results = new[]
                {
                    new
                    {
                        data,
                        statusCode = 200
                    }
                }
            },
            isError = false
        });

static void WriteFakeMcpDirectToolSuccess(
    int id,
    object data)
    => WriteFakeMcpResponse(
        id,
        new
        {
            content = Array.Empty<object>(),
            structuredContent = new
            {
                data,
                statusCode = 200
            },
            isError = false
        });

static void WriteFakeMcpBatchSuccess(
    int id,
    IReadOnlyList<object?> data)
    => WriteFakeMcpResponse(
        id,
        new
        {
            content = Array.Empty<object>(),
            structuredContent = new
            {
                results = data.Select(item => new
                {
                    data = item,
                    statusCode = item is null ? 404 : 200,
                    error = item is null
                        ? new
                        {
                            message = "ErrorItemNotFound"
                        }
                        : null
                })
            },
            isError = false
        });

static void WriteFakeMcpToolError(
    int id,
    int statusCode,
    string message)
    => WriteFakeMcpResponse(
        id,
        new
        {
            content = Array.Empty<object>(),
            structuredContent = new
            {
                results = new[]
                {
                    new
                    {
                        data = (object?)null,
                        statusCode,
                        error = new
                        {
                            message
                        }
                    }
                }
            },
            isError = false
        });

static void WriteFakeMcpAskSuccess(
    int id,
    string answer)
    => WriteFakeMcpResponse(
        id,
        new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = answer
                }
            },
            structuredContent = new
            {
                answer,
                conversationId = "fake-conversation"
            },
            isError = false
        });

static void RequireFakeBody(
    string? body,
    params string[] expectedFragments)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        throw new InvalidOperationException(
            "The fake write request did not include a body.");
    }
    using var document = JsonDocument.Parse(body);
    var normalized = JsonSerializer.Serialize(document.RootElement);
    foreach (var fragment in expectedFragments)
    {
        if (!normalized.Contains(fragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The fake write request body omitted {fragment}.");
        }
    }
}

static void WriteFakeWorkIqLog(string value)
{
    var logPath = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_LOG");
    if (string.IsNullOrWhiteSpace(logPath))
    {
        return;
    }

    using var mutex = new Mutex(
        initiallyOwned: false,
        @"Local\Peggy.FakeWorkIq.Log");
    try
    {
        mutex.WaitOne();
        File.AppendAllLines(logPath, [value]);
    }
    catch (AbandonedMutexException)
    {
        File.AppendAllLines(logPath, [value]);
    }
    finally
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
    }

}

static int GetFakeWorkIqRequestCount(string value)
{
    var logPath = Environment.GetEnvironmentVariable(
        "PEGGY_FAKE_WORKIQ_LOG");
    return string.IsNullOrWhiteSpace(logPath) ||
           !File.Exists(logPath)
        ? 0
        : File.ReadAllLines(logPath).Count(line =>
            string.Equals(line, value, StringComparison.Ordinal));
}

static object FakeEmail(
    string id,
    string subject,
    string body,
    bool isDraft,
    string? recipient = null,
    bool sent = false)
    => new
    {
        id,
        subject,
        body = new
        {
            contentType = "text",
            content = body
        },
        toRecipients = recipient is null
            ? Array.Empty<object>()
            :
            [
                new
                {
                    emailAddress = new
                    {
                        address = recipient
                    }
                }
            ],
        from = new
        {
            emailAddress = new
            {
                address = Environment.GetEnvironmentVariable(
                              "PEGGY_FAKE_WORKIQ_MAIL") ??
                          PeggyDefaults.ConfiguredWorkIqAccount
            }
        },
        sentDateTime = sent
            ? DateTimeOffset.UtcNow
            : (DateTimeOffset?)null,
        isDraft
    };

static object FakeTeamsMessage(
    string id,
    string body)
    => new
    {
        id,
        from = new
        {
            user = new
            {
                id = "user-me",
                displayName = "Current user"
            }
        },
        body = new
        {
            contentType = "text",
            content = body
        }
    };

static object FakeCalendarEvent(
    string id,
    string subject,
    string body,
    string start,
    string end,
    string[] attendees,
    bool isCancelled)
    => new
    {
        id,
        subject,
        body = new
        {
            contentType = "text",
            content = body
        },
        start = new
        {
            dateTime = start,
            timeZone = "UTC"
        },
        end = new
        {
            dateTime = end,
            timeZone = "UTC"
        },
        attendees = attendees.Select(address => new
        {
            emailAddress = new
            {
                address
            }
        }),
        isCancelled
    };
