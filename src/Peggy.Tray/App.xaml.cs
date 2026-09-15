using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Peggy.Core;
using Forms = System.Windows.Forms;

namespace Peggy.Tray;

public partial class App : Application
{
    private const string AssistantName = "Peggy";
    private const string StartupGreeting =
        "Peggy's on the perch. I'll chirp if something needs you.";
    private const string IdleDescription =
        "Idle, calm perch and blink.";
    private const string CelebrationDescription =
        "New digest, hop and wing flap.";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SingleInstanceGuard _singleInstance = new();
    private readonly PeggyStateManager _state = new();
    private readonly PrivacyGuard _privacy = new();
    private readonly Dictionary<string, Message> _messageCache =
        new(StringComparer.Ordinal);
    private readonly HashSet<AlertContextWindow> _alertContextWindows = [];

    private Forms.NotifyIcon _tray = null!;
    private Forms.ToolStripMenuItem _alertsMenu = null!;
    private Forms.ToolStripMenuItem _creditsMenu = null!;
    private Forms.ToolStripMenuItem _pauseMenu = null!;
    private Forms.ToolStripMenuItem _talkMenu = null!;
    private Forms.ToolStripMenuItem _toggleMascotMenu = null!;
    private Forms.ToolStripMenuItem _quitMenu = null!;
    private MascotWindow _mascot = null!;
    private SpeechBubbleWindow _bubble = null!;
    private AlertCenterWindow _alertCenter = null!;
    private TroubleshootingWindow? _troubleshootingWindow;
    private ConversationWindow _conversationWindow = null!;
    private ConversationController _conversation = null!;
    private AlertManager _alertManager = null!;
    private IMessageProvider _provider = null!;
    private CopilotRunner _copilot = null!;
    private DispatcherTimer _poll = null!;
    private DispatcherTimer _scheduleTimer = null!;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _alertRevealTimer;
    private DispatcherTimer? _wakeCheckTimer;
    private Task _activeCheck = Task.CompletedTask;
    private Task _copilotWarmup = Task.CompletedTask;
    private bool _checking;
    private bool _restartCheckRequested;
    private bool _restartCheckForced;
    private bool _workIqEulaRepairRetryPending;
    private bool _quitting;
    private bool _scheduledSleeping;
    private int _monitoringGeneration;
    private int _monitoringCycleGeneration = -1;
    private string? _monitoringCycleWarning;
    private string? _monitoringRemediation;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (!_singleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        SessionEnding += (_, _) => _shutdown.Cancel();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        try
        {
            await _state.InitializeAsync(_shutdown.Token);
            if (!await EnsureWorkIqConnectionAsync())
            {
                Shutdown();
                return;
            }
            _scheduledSleeping = MonitoringSchedule.Evaluate(
                _state.State.Settings,
                DateTime.Now).IsSleeping;
            StartupManager.Apply(_state.State.Settings.StartAtSignIn);
            CreateRuntimeServices();
            CreateWindows();
            CreateTray();
            await _state.SaveAsync(_shutdown.Token);
            StartCopilotWarmup(_copilot);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Assistant could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        RestoreRestingState();
        _mascot.ShowAnimated();
        Log(
            $"START character={AssistantName} " +
            $"schedule={(_scheduledSleeping ? "sleeping" : "awake")}");
        RefreshUi(showPersistentCards: true);

        if (!_scheduledSleeping)
        {
            var hello = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.3)
            };
            hello.Tick += (_, _) =>
            {
                hello.Stop();
                Say(StartupGreeting);
            };
            hello.Start();
        }

        var first = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        first.Tick += (_, _) =>
        {
            first.Stop();
            StartCheck(force: false);
        };
        first.Start();

        _poll = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _poll.Tick += (_, _) => StartCheck(force: false);
        _poll.Start();
        StartScheduleTimer();
        ScheduleMockSmokeExit(e.Args);
    }

    private void CreateRuntimeServices()
    {
        _provider = MessageProviderFactory.Create(
            _state.State.Settings,
            _state.State.PinnedAccount);
        _copilot = new CopilotRunner(
            _state.State.Settings,
            _state.State.CopilotSessionId,
            _state.State.CopilotSessionCredits,
            account: _state.State.PinnedAccount);
        _alertManager = new AlertManager(_state);
        var dismissed = _alertManager.DismissAlertsWithoutVerifiedSources(
            DateTimeOffset.UtcNow);
        if (dismissed > 0)
        {
            Log(
                $"ALERT CLEANUP dismissedUnsupportedSources={dismissed}");
        }
    }

    private void CreateWindows()
    {
        _bubble = new SpeechBubbleWindow();
        _mascot = new MascotWindow();
        _alertCenter = new AlertCenterWindow();
        _conversationWindow = new ConversationWindow();
        _mascot.WakingCompleted += OnWakingCompleted;
        _conversation = new ConversationController(
            _conversationWindow,
            _mascot,
            _state,
            _alertManager,
            () => _copilot,
            () => _provider,
            GetFocusMessages,
            () => _privacy.ShouldMask,
            _shutdown.Token,
            restoreRestingState: RestoreRestingState);

        _alertCenter.ResolveRequested += alertId =>
            RunUiAction(
                () => UpdateAlertAsync(
                    alertId,
                    AlertStatus.Resolved),
                $"{AssistantName} alerts");
        _alertCenter.DismissRequested += alertId =>
            RunUiAction(
                () => UpdateAlertAsync(
                    alertId,
                    AlertStatus.Dismissed),
                $"{AssistantName} alerts");
        _alertCenter.DismissAllRequested += () =>
            RunUiAction(
                DismissAllAlertsAsync,
                $"{AssistantName} alerts");
        _alertCenter.SnoozeRequested += alertId =>
            RunUiAction(
                () => SnoozeAlertAsync(alertId),
                $"{AssistantName} alerts");
        _alertCenter.FeedbackRequested += (alertId, kind) =>
            RunUiAction(
                () => AddFeedbackAsync(alertId, kind),
                $"{AssistantName} alerts");
        _alertCenter.TierChanged += (alertId, tier) =>
            RunUiAction(
                () => CorrectTierAsync(alertId, tier),
                $"{AssistantName} alerts");
        _alertCenter.ChatRequested += alertId =>
            RunUiAction(
                () => StartConversationAsync(alertId),
                $"{AssistantName} voice");
        _alertCenter.ContextRequested += alertId =>
            RunUiAction(
                () => OpenAlertContextAsync(alertId),
                $"{AssistantName} alert context");
        _alertCenter.RevealRequested += () =>
            _ = ConfirmContextReveal();
        _alertCenter.RetryRequested += () => StartCheck(force: true);
        _alertCenter.TroubleshootRequested += () =>
            RunUiAction(
                ShowTroubleshootingAsync,
                $"{AssistantName} troubleshooting");
        _alertCenter.CriticalSwitchRequested += alertId =>
            RunUiAction(
                () => StartConversationAsync(alertId),
                $"{AssistantName} voice");
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = AssistantName
        };
        var menu = new Forms.ContextMenuStrip();
        _alertsMenu = new Forms.ToolStripMenuItem("Open alerts");
        _alertsMenu.Click += (_, _) => ShowAlertCenter();
        menu.Items.Add(_alertsMenu);
        _talkMenu = new Forms.ToolStripMenuItem(
            $"Talk to {AssistantName}");
        _talkMenu.Click += (_, _) => RunUiAction(
                () => StartConversationAsync(null),
                $"{AssistantName} voice");
        menu.Items.Add(_talkMenu);
        menu.Items.Add(
            "Unload voice models",
            null,
            (_, _) =>
            {
                _conversation.UnloadSpeech();
                Say("Voice models unloaded.");
            });
        menu.Items.Add(
            "Check now",
            null,
            (_, _) => StartCheck(force: true));
        _pauseMenu = new Forms.ToolStripMenuItem("Pause monitoring")
        {
            Checked = _state.State.Settings.MonitoringPaused,
            CheckOnClick = true
        };
        _pauseMenu.CheckedChanged += (_, _) =>
            RunUiAction(
                () => SetPausedAsync(_pauseMenu.Checked),
                $"{AssistantName} monitoring");
        menu.Items.Add(_pauseMenu);

        _creditsMenu = new Forms.ToolStripMenuItem("AI credits: 0");
        menu.Items.Add(_creditsMenu);
        menu.Items.Add(
            "Microsoft 365 sign-in",
            null,
            (_, _) => RunUiAction(
                ReauthenticateWorkIqAsync,
                "Microsoft 365 sign-in"));
        menu.Items.Add(
            "Settings",
            null,
            (_, _) => RunUiAction(
                ShowSettingsAsync,
                $"{AssistantName} settings"));
        _toggleMascotMenu = new Forms.ToolStripMenuItem(
            $"Show or hide {AssistantName}");
        _toggleMascotMenu.Click += (_, _) => ToggleMascot();
        menu.Items.Add(_toggleMascotMenu);

        var animations = new Forms.ToolStripMenuItem("Test animations");
        animations.DropDownItems.Add(
            "Play all",
            null,
            (_, _) => PlayAllAnimations());
        animations.DropDownItems.Add(
            "Idle",
            null,
            (_, _) => ShowState(
                PeggyState.Idle,
                IdleDescription));
        animations.DropDownItems.Add(
            "Listening",
            null,
            (_, _) => ShowState(PeggyState.Listening, "Listening locally..."));
        animations.DropDownItems.Add(
            "Thinking",
            null,
            (_, _) => ShowState(PeggyState.Thinking, null));
        animations.DropDownItems.Add(
            "New digest",
            null,
            (_, _) => ShowState(
                PeggyState.NewDigest,
                "Something needs your attention."));
        animations.DropDownItems.Add(
            "Sleeping",
            null,
            (_, _) => ShowState(
                PeggyState.Sleeping,
                $"Good night from {AssistantName}."));
        animations.DropDownItems.Add(
            "Waking",
            null,
            (_, _) => ShowState(
                PeggyState.Waking,
                $"Good morning from {AssistantName}."));
        animations.DropDownItems.Add(
            "Leave then return",
            null,
            (_, _) => EntranceDemo());
        menu.Items.Add(animations);

        menu.Items.Add(new Forms.ToolStripSeparator());
        _quitMenu = new Forms.ToolStripMenuItem(
            $"Quit {AssistantName}");
        _quitMenu.Click += (_, _) => RunUiAction(
                QuitAppAsync,
                AssistantName);
        menu.Items.Add(_quitMenu);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowAlertCenter();
        _mascot.ContextMenuRequested += ShowTrayContextMenu;
    }

    private void ShowTrayContextMenu()
    {
        if (_quitting ||
            _tray.ContextMenuStrip is not { IsDisposed: false } menu)
        {
            return;
        }

        menu.Show(Forms.Control.MousePosition);
    }

    private void StartCheck(bool force)
    {
        if (_quitting ||
            (!force &&
             !MonitoringSchedule.CanRunAutomatic(
                 _state.State.Settings,
                 DateTime.Now)))
        {
            return;
        }

        if (_checking)
        {
            if (force)
            {
                _restartCheckRequested = true;
                _restartCheckForced = true;
            }
            return;
        }

        _activeCheck = RunCheckAsync(_monitoringGeneration, force);
    }

    private async Task RunCheckAsync(int generation, bool force)
    {
        if (_checking)
        {
            return;
        }
        _checking = true;
        if (_monitoringCycleGeneration != generation)
        {
            _monitoringCycleGeneration = generation;
            _monitoringCycleWarning = null;
        }
        var keepThinkingForContinuation = false;
        var resultAnimationActive = false;
        var checkStage = "preparing";
        var batchLimit = PeggyDefaults.GetMonitoringBatchSize(
            _state.State.ConsecutiveMonitoringFailures);
        DateTimeOffset? retrievalReferenceUtc = null;
        MonitoringCursorState? retrievalCursorSnapshot = null;
        if (!_conversation.IsActive &&
            _mascot.IsVisible)
        {
            _mascot.SetState(PeggyState.Thinking);
        }

        try
        {
            var reactivated = _alertManager.ReactivateDue(DateTimeOffset.UtcNow);
            if (reactivated.Count > 0)
            {
                await _state.SaveAsync(_shutdown.Token);
                if (generation != _monitoringGeneration)
                {
                    return;
                }
                resultAnimationActive |= HasVisibleMaterialAlerts(
                    reactivated);
                SurfaceMaterialAlerts(reactivated);
            }

            if (_state.EnsureDailySession())
            {
                await ReplaceCopilotAsync(
                    _state.State.Settings);
                await _state.SaveAsync(_shutdown.Token);
                if (generation != _monitoringGeneration)
                {
                    return;
                }
            }

            var hasMore = true;
            var batches = 0;
            while (hasMore && batches < 20)
            {
                batches++;
                var nowUtc = DateTimeOffset.UtcNow;
                retrievalReferenceUtc = nowUtc;
                retrievalCursorSnapshot = CloneMonitoringCursor(
                    _state.State.MonitoringCursor);
                var cursorBefore = JsonSerializer.Serialize(
                    _state.State.MonitoringCursor);
                checkStage = "retrieval";
                var batch = await _provider.GetChangesAsync(
                    _state.State.MonitoringCursor,
                    batchLimit,
                    nowUtc,
                    _shutdown.Token);
                if (!string.IsNullOrWhiteSpace(batch.Warning))
                {
                    _monitoringCycleWarning = batch.Warning;
                    if (await TryRestoreWorkIqEulaAsync(
                            new InvalidOperationException(batch.Warning)))
                    {
                        Log("WORKIQ EULA RESTORED acceptedVersion=true");
                        _monitoringRemediation = null;
                        _monitoringCycleWarning = null;
                        _restartCheckRequested = true;
                        _restartCheckForced |= force;
                        return;
                    }
                    _monitoringRemediation = batch.Warning;
                }
                if (generation != _monitoringGeneration)
                {
                    return;
                }
                if (batch.HasMore &&
                    string.Equals(
                        cursorBefore,
                        JsonSerializer.Serialize(batch.Cursor),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The message provider reported more work without advancing its cursor.");
                }
                var seen = _state.GetSeenMessageIds();
                var fresh = batch.Messages
                    .Where(message => !seen.Contains(message.Id))
                    .ToList();
                CacheMessages(fresh);

                AlertApplyResult? applied = null;
                if (fresh.Count > 0)
                {
                    checkStage = "classification";
                    var result = await _copilot.RunAlertEvaluationAsync(
                        fresh,
                        _alertManager.GetActiveAlerts(),
                        _state.State.FeedbackRules,
                        _shutdown.Token);
                    if (generation != _monitoringGeneration)
                    {
                        return;
                    }
                    _state.RecordCredit(
                        "monitoring",
                        result.Usage.Model,
                        result.Usage.OperationCredits,
                        result.Usage.SessionCredits,
                        nowUtc);
                    applied = _alertManager.Apply(
                        result.Decisions,
                        fresh,
                        nowUtc);
                    _state.MarkMessagesSeen(
                        fresh.Select(message => message.Id),
                        nowUtc);
                    resultAnimationActive |= HasVisibleMaterialAlerts(
                        applied.MaterialUpdates);
                    SurfaceMaterialAlerts(applied.MaterialUpdates);
                }

                checkStage = "persistence";
                _state.State.MonitoringCursor = batch.Cursor;
                hasMore = batch.HasMore;
                await _state.SaveAsync(_shutdown.Token);
                if (generation != _monitoringGeneration)
                {
                    return;
                }
                RefreshUi(showPersistentCards: false);
            }

            if (hasMore)
            {
                keepThinkingForContinuation = true;
                _ = Dispatcher.BeginInvoke(
                    new Action(() => StartCheck(force)),
                    DispatcherPriority.Background);
            }
            else
            {
                await CaptureObservedWorkIqEulaAcceptanceAsync(
                    _monitoringCycleWarning);
                _workIqEulaRepairRetryPending = false;
                _state.RecordMonitoringSuccess(DateTimeOffset.UtcNow);
                _monitoringRemediation = _monitoringCycleWarning;
                _monitoringCycleWarning = null;
                await _state.SaveAsync(_shutdown.Token);
                RefreshUi(showPersistentCards: false);
            }
            Log(
                hasMore
                    ? $"CHECK CONTINUE batches={batches}"
                    : $"CHECK SUCCESS batches={batches}");
        }
        catch (OperationCanceledException) when (
            _shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation != _monitoringGeneration)
            {
                return;
            }

            if (await TryRestoreWorkIqEulaAsync(ex))
            {
                Log("WORKIQ EULA RESTORED acceptedVersion=true");
                _monitoringRemediation = null;
                _monitoringCycleWarning = null;
                _restartCheckRequested = true;
                _restartCheckForced |= force;
                return;
            }

            _workIqEulaRepairRetryPending = false;
            if (ex is CopilotClassificationException classificationFailure)
            {
                _state.RecordCredit(
                    "monitoring-rejected",
                    classificationFailure.Usage.Model,
                    classificationFailure.Usage.OperationCredits,
                    classificationFailure.Usage.SessionCredits,
                    DateTimeOffset.UtcNow);
            }
            var failureCode = GetMonitoringFailureCode(ex, checkStage);
            var failure = MonitoringFailureInspector.Create(
                ex,
                checkStage,
                failureCode,
                DateTimeOffset.UtcNow);
            failure.BatchLimit = batchLimit;
            failure.RetrievalReferenceUtc = retrievalReferenceUtc;
            failure.CursorSnapshot = CloneMonitoringCursor(
                retrievalCursorSnapshot ??
                _state.State.MonitoringCursor);
            var becameInterrupted = _state.RecordMonitoringFailure(failure);
            _monitoringRemediation = GetMonitoringRemediation(
                ex,
                checkStage);
            _monitoringCycleWarning = null;
            try
            {
                await _state.SaveAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (
                _shutdown.IsCancellationRequested)
            {
            }
            Log(
                $"CHECK ERROR stage={checkStage} " +
                $"code={failureCode} " +
                $"type={ex.GetType().Name} " +
                $"status={failure.StatusCode?.ToString() ?? "none"} " +
                $"batchLimit={batchLimit} " +
                $"failures={_state.State.ConsecutiveMonitoringFailures}");
            if (becameInterrupted ||
                _monitoringRemediation is not null)
            {
                ShowAlertCenter();
            }
            RefreshUi(showPersistentCards: false);
        }
        finally
        {
            _checking = false;
            if (!_conversation.IsActive &&
                !resultAnimationActive &&
                !keepThinkingForContinuation &&
                _mascot.IsVisible)
            {
                RestoreRestingState();
            }
            if (_restartCheckRequested &&
                !_quitting &&
                (_restartCheckForced ||
                 MonitoringSchedule.CanRunAutomatic(
                     _state.State.Settings,
                     DateTime.Now)))
            {
                var restartForce = _restartCheckForced;
                _restartCheckRequested = false;
                _restartCheckForced = false;
                _ = Dispatcher.BeginInvoke(
                    new Action(() => StartCheck(restartForce)),
                    DispatcherPriority.Background);
            }
        }
    }

    private void SurfaceMaterialAlerts(
        IReadOnlyList<AlertRecord> materialAlerts)
    {
        var visible = materialAlerts
            .Where(alert => alert.Tier is
                AlertTier.Critical or
                AlertTier.Attention)
            .OrderBy(alert => alert.Tier)
            .ThenByDescending(alert => alert.LastActivityUtc)
            .ToList();
        if (visible.Count == 0)
        {
            return;
        }

        var top = visible[0];
        if (!_mascot.IsVisible)
        {
            _mascot.ShowAnimated();
        }
        _mascot.SetState(PeggyState.NewDigest);
        var text = _privacy.ShouldMask
            ? $"A work item needs your attention. Open " +
              $"{AssistantName} for details."
            : top.Summary;
        _bubble.Say(text, _mascot.HeadAnchor(), seconds: 12);
        ReturnToRestAfter(TimeSpan.FromSeconds(13));
        ShowAlertCenter();

        var critical = visible.FirstOrDefault(alert =>
            alert.Tier == AlertTier.Critical);
        if (critical is not null && _conversation.IsActive)
        {
            _conversation.ShowCritical(critical, _privacy.ShouldMask);
            _alertCenter.ShowCriticalBanner(
                critical,
                _privacy.ShouldMask);
        }
    }

    private static bool HasVisibleMaterialAlerts(
        IEnumerable<AlertRecord> alerts)
        => alerts.Any(alert => alert.Tier is
            AlertTier.Critical or
            AlertTier.Attention);

    private void RefreshUi(bool showPersistentCards)
    {
        var active = _alertManager.GetActiveAlerts();
        _alertCenter.UpdateAlerts(active, ShouldMaskAlertContent());
        var critical = active.FirstOrDefault(alert =>
            alert.Tier == AlertTier.Critical);
        if (_conversation.IsActive && critical is not null)
        {
            _conversation.ShowCritical(critical, _privacy.ShouldMask);
            _alertCenter.ShowCriticalBanner(critical, _privacy.ShouldMask);
        }
        else
        {
            _conversationWindow.ClearCritical();
            _alertCenter.ClearCriticalBanner();
        }
        _alertCenter.SetMonitoringInterrupted(
            _state.State.MonitoringInterrupted,
            _state.State.ConsecutiveMonitoringFailures,
            _state.State.LastSuccessfulMonitoringUtc,
            _monitoringRemediation);

        var persistent = active.Count(alert => alert.Tier is
            AlertTier.Critical or
            AlertTier.Attention);
        var awareness = active.Count(alert =>
            alert.Tier == AlertTier.Awareness);
        var credits = _state.GetTodayCredits();
        _alertsMenu.Text =
            $"Open alerts ({persistent} active, {awareness} awareness)";
        var scheduleStatus = _scheduledSleeping
            ? $"Sleeping until {MonitoringSchedule.FormatMinute(
                _state.State.Settings.MonitoringWakeMinute)}"
            : "Awake";
        _tray.Text = LimitTrayText(
            $"{AssistantName} | {scheduleStatus} | " +
            $"{persistent} alerts | {awareness} awareness | " +
            $"{credits:0.##} credits");
        RebuildCreditMenu(credits);

        if (showPersistentCards &&
            (persistent > 0 || _state.State.MonitoringInterrupted))
        {
            ShowAlertCenter();
        }
    }

    private void RebuildCreditMenu(decimal todayCredits)
    {
        _creditsMenu.Text = $"AI credits today: {todayCredits:0.##}";
        _creditsMenu.DropDownItems.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var entries = _state.State.CreditHistory
            .Where(entry =>
                DateOnly.FromDateTime(
                    entry.TimestampUtc.ToLocalTime().DateTime) == today)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(20)
            .ToList();
        if (entries.Count == 0)
        {
            _creditsMenu.DropDownItems.Add(
                new Forms.ToolStripMenuItem("No usage today")
                {
                    Enabled = false
                });
            return;
        }

        foreach (var entry in entries)
        {
            _creditsMenu.DropDownItems.Add(
                new Forms.ToolStripMenuItem(
                    $"{entry.TimestampUtc.ToLocalTime():HH:mm} | " +
                    $"+{entry.Credits:0.##} | {entry.Operation} | {entry.Model}")
                {
                    Enabled = false
                });
        }
    }

    private async Task UpdateAlertAsync(
        string alertId,
        AlertStatus status)
    {
        _alertManager.Resolve(alertId, status, DateTimeOffset.UtcNow);
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
    }

    private async Task DismissAllAlertsAsync()
    {
        if (_alertManager.DismissAllActive(DateTimeOffset.UtcNow) == 0)
        {
            return;
        }
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
    }

    private async Task AddFeedbackAsync(
        string alertId,
        FeedbackKind kind)
    {
        _alertManager.AddFeedback(alertId, kind, DateTimeOffset.UtcNow);
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
    }

    private async Task SnoozeAlertAsync(string alertId)
    {
        _alertManager.Snooze(
            alertId,
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow);
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
    }

    private async Task CorrectTierAsync(
        string alertId,
        AlertTier tier)
    {
        _alertManager.CorrectTier(alertId, tier, DateTimeOffset.UtcNow);
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
    }

    private async Task SetPausedAsync(bool paused)
    {
        if (_state.State.Settings.MonitoringPaused == paused)
        {
            return;
        }
        _state.State.Settings.MonitoringPaused = paused;
        if (paused)
        {
            _monitoringGeneration++;
        }
        await _state.SaveAsync(_shutdown.Token);
        RefreshUi(showPersistentCards: false);
        if (!paused)
        {
            if (!_scheduledSleeping)
            {
                StartCheck(force: false);
            }
            else
            {
                RestoreRestingState();
            }
        }
    }

    private async Task ShowSettingsAsync()
    {
        var settings = CloneSettings(_state.State.Settings);
        var window = new SettingsWindow(
            settings,
            _provider,
            _state.State.PinnedAccount,
            _copilot.ListAvailableModelsAsync)
        {
            Owner = _alertCenter.IsVisible
                ? _alertCenter
                : null
        };
        var dialogResult = window.ShowDialog();
        if (window.ReauthenticatedIdentity is { } identity)
        {
            await _state.PinWorkIqIdentityAsync(
                identity.Account,
                identity.Mail ?? identity.UserPrincipalName,
                _shutdown.Token);
            await _state.RecordWorkIqEulaAcceptanceAsync(
                identity.EulaAcceptedVersion,
                _shutdown.Token);
        }

        if (dialogResult != true ||
            window.SavedSettings is null)
        {
            return;
        }

        var previous = _state.State.Settings;
        var updated = window.SavedSettings;
        var wasScheduledSleeping = _scheduledSleeping;
        var outlookScopeChanged =
            previous.IncludeActiveCasesFolder !=
                updated.IncludeActiveCasesFolder ||
            !previous.AdditionalOutlookFolders.SequenceEqual(
                updated.AdditionalOutlookFolders,
                StringComparer.OrdinalIgnoreCase);
        var teamsScopeChanged =
            previous.TeamsScope != updated.TeamsScope ||
            previous.MonitorTeamsChatsInSelectedScope !=
                updated.MonitorTeamsChatsInSelectedScope ||
            !string.Equals(
                JsonSerializer.Serialize(previous.TeamsTeamWatchlist),
                JsonSerializer.Serialize(updated.TeamsTeamWatchlist),
                StringComparison.Ordinal) ||
            !string.Equals(
                JsonSerializer.Serialize(previous.TeamsChannelWatchlist),
                JsonSerializer.Serialize(updated.TeamsChannelWatchlist),
                StringComparison.Ordinal);
        var copilotConfigurationChanged =
            !string.Equals(
                previous.CopilotModel,
                updated.CopilotModel,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                previous.CopilotReasoningEffort,
                updated.CopilotReasoningEffort,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                previous.CopilotContextTier,
                updated.CopilotContextTier,
                StringComparison.OrdinalIgnoreCase);
        var speechChanged =
            previous.SpeechEnabled != updated.SpeechEnabled ||
            previous.SpeechThreads != updated.SpeechThreads ||
            previous.SpeechVoiceId != updated.SpeechVoiceId ||
            previous.SpeechSpeed != updated.SpeechSpeed ||
            previous.SpeechInputGain != updated.SpeechInputGain ||
            !string.Equals(
                previous.SpeechModelDirectory,
                updated.SpeechModelDirectory,
                StringComparison.OrdinalIgnoreCase);
        _monitoringGeneration++;
        if (_conversation.IsActive)
        {
            await _conversation.StopAsync();
        }
        var replacementSessionId = copilotConfigurationChanged
            ? Guid.NewGuid().ToString()
            : _state.State.CopilotSessionId;
        var replacementSessionCredits = copilotConfigurationChanged
            ? 0
            : _state.State.CopilotSessionCredits;
        var replacementProvider = MessageProviderFactory.Create(
            updated,
            _state.State.PinnedAccount);
        var replacementCopilot = new CopilotRunner(
            updated,
            replacementSessionId,
            replacementSessionCredits,
            account: _state.State.PinnedAccount);
        var startupChanged =
            previous.StartAtSignIn != updated.StartAtSignIn;
        try
        {
            if (startupChanged)
            {
                StartupManager.Apply(updated.StartAtSignIn);
            }
            await _state.ApplySettingsAsync(
                updated,
                resetOutlookMonitoring: outlookScopeChanged,
                resetTeamsMonitoring: teamsScopeChanged,
                resetCopilotSession: copilotConfigurationChanged,
                replacementSessionId,
                replacementSessionCredits,
                DateTimeOffset.UtcNow,
                _shutdown.Token);
        }
        catch (Exception applyFailure)
        {
            var rollbackFailures = new List<Exception>();
            if (startupChanged)
            {
                try
                {
                    StartupManager.Apply(previous.StartAtSignIn);
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailures.Add(rollbackFailure);
                }
            }

            try
            {
                await replacementCopilot.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                rollbackFailures.Add(cleanupFailure);
            }
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "Settings could not be applied and rollback was incomplete.",
                    new[] { applyFailure }.Concat(rollbackFailures));
            }
            throw;
        }

        _provider = replacementProvider;
        var activationFailures = new List<Exception>();
        try
        {
            await ReplaceCopilotAsync(replacementCopilot);
        }
        catch (Exception activationFailure)
        {
            activationFailures.Add(activationFailure);
        }
        if (speechChanged)
        {
            try
            {
                _conversation.UnloadSpeech();
            }
            catch (Exception activationFailure)
            {
                activationFailures.Add(activationFailure);
            }
        }
        _pauseMenu.Checked = updated.MonitoringPaused;
        try
        {
            await EvaluateMonitoringScheduleAsync(
                animateWake: true,
                startCheckOnWake: true);
        }
        catch (Exception activationFailure)
        {
            activationFailures.Add(activationFailure);
        }
        RefreshUi(showPersistentCards: false);
        if (!updated.MonitoringPaused &&
            !_scheduledSleeping &&
            !wasScheduledSleeping)
        {
            StartCheck(force: false);
        }
        if (activationFailures.Count > 0)
        {
            throw new AggregateException(
                "Settings were saved, but one or more runtime services could " +
                "not fully refresh.",
                activationFailures);
        }
    }

    private Task ShowTroubleshootingAsync()
    {
        if (_troubleshootingWindow is
            {
                IsVisible: true
            } existing)
        {
            existing.Activate();
            return Task.CompletedTask;
        }

        var failure = _state.State.LastMonitoringFailure ??
            new MonitoringFailureDetails
            {
                OccurredUtc = DateTimeOffset.UtcNow,
                Stage = "unknown",
                Code = "details-unavailable",
                ExceptionType = "Unknown",
                Message =
                    "The original failure details were not captured by this " +
                    "Peggy version."
            };
        var diagnosticSettings = CloneSettings(_state.State.Settings);
        diagnosticSettings.CopilotModel = _copilot.Model;
        var troubleshooter = new MonitoringTroubleshooter(
            failure,
            diagnosticSettings,
            CloneMonitoringCursor(
                failure.CursorSnapshot ??
                _state.State.MonitoringCursor),
            _state.State.PinnedAccount,
            _state.State.ConsecutiveMonitoringFailures,
            _copilot.ListAvailableModelsAsync);
        var window = new TroubleshootingWindow(
            failure,
            troubleshooter,
            DiagnosticCollectorRunner.CreateAsync)
        {
            Owner = _alertCenter
        };
        window.SignInRequested += () =>
        {
            window.Close();
            RunUiAction(
                ReauthenticateWorkIqAsync,
                "Microsoft 365 sign-in");
        };
        window.OpenSettingsRequested += () =>
        {
            window.Close();
            RunUiAction(
                ShowSettingsAsync,
                $"{AssistantName} settings");
        };
        window.RetryMonitoringRequested += () =>
        {
            window.Close();
            StartCheck(force: true);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_troubleshootingWindow, window))
            {
                _troubleshootingWindow = null;
            }
        };
        _troubleshootingWindow = window;
        window.Show();
        return Task.CompletedTask;
    }

    private static MonitoringCursorState CloneMonitoringCursor(
        MonitoringCursorState cursor)
        => JsonSerializer.Deserialize<MonitoringCursorState>(
               JsonSerializer.Serialize(cursor))
           ?? throw new InvalidOperationException(
               "Peggy could not clone its monitoring cursor.");

    private async Task<bool> EnsureWorkIqConnectionAsync()
    {
        if (MessageProviderFactory.UsesMockProvider)
        {
            return true;
        }

        var setup = new WorkIqSetupService();
        var account = _state.State.PinnedAccount;
        if (!string.IsNullOrWhiteSpace(account) &&
            setup.TryGetExecutablePath(out _))
        {
            if (await RestoreRecordedWorkIqEulaAsync(setup))
            {
                try
                {
                    var identity = await setup.VerifyAccountAsync(
                        account,
                        _shutdown.Token);
                    await _state.PinWorkIqIdentityAsync(
                        identity.Account,
                        identity.Mail ?? identity.UserPrincipalName,
                        _shutdown.Token);
                    return true;
                }
                catch (WorkIqSetupException)
                {
                    // Continue to the explicit connection flow.
                }
            }
        }

        var window = new WorkIqSignInWindow(account, setup);
        if (window.ShowDialog() != true ||
            string.IsNullOrWhiteSpace(window.AuthenticatedAccount))
        {
            return false;
        }

        await _state.PinWorkIqIdentityAsync(
            window.AuthenticatedAccount,
            window.AuthenticatedMailbox,
            _shutdown.Token);
        await _state.RecordWorkIqEulaAcceptanceAsync(
            window.AcceptedEulaVersion
                ?? throw new InvalidOperationException(
                    "WorkIQ did not return the accepted EULA version."),
            _shutdown.Token);
        return true;
    }

    private async Task ReauthenticateWorkIqAsync()
    {
        if (MessageProviderFactory.UsesMockProvider)
        {
            throw new InvalidOperationException(
                "Microsoft 365 sign-in is unavailable while Peggy uses the " +
                "mock message provider.");
        }

        var window = new WorkIqSignInWindow(
            _state.State.PinnedAccount,
            new WorkIqSetupService())
        {
            Owner = _alertCenter.IsVisible
                ? _alertCenter
                : null
        };
        if (window.ShowDialog() != true ||
            string.IsNullOrWhiteSpace(window.AuthenticatedAccount))
        {
            return;
        }

        await _state.PinWorkIqIdentityAsync(
            window.AuthenticatedAccount,
            window.AuthenticatedMailbox,
            _shutdown.Token);
        await _state.RecordWorkIqEulaAcceptanceAsync(
            window.AcceptedEulaVersion
                ?? throw new InvalidOperationException(
                    "WorkIQ did not return the accepted EULA version."),
            _shutdown.Token);
        Say($"Microsoft 365 is connected as {window.AuthenticatedAccount}.");
    }

    private async Task<bool> RestoreRecordedWorkIqEulaAsync(
        WorkIqSetupService setup)
    {
        var acceptedVersion = _state.State.WorkIqEulaAcceptedVersion;
        if (string.IsNullOrWhiteSpace(acceptedVersion))
        {
            return true;
        }

        var installedVersion = await setup.GetVersionAsync(_shutdown.Token);
        if (!string.Equals(
                acceptedVersion,
                installedVersion,
                StringComparison.Ordinal))
        {
            Log("WORKIQ EULA REVIEW REQUIRED versionChanged=true");
            return false;
        }

        await setup.AcceptEulaAsync(
            acceptedVersion,
            _shutdown.Token);
        Log("WORKIQ EULA RESTORED AT STARTUP acceptedVersion=true");
        return true;
    }

    private async Task<bool> TryRestoreWorkIqEulaAsync(Exception failure)
    {
        if (MessageProviderFactory.UsesMockProvider)
        {
            return false;
        }

        if (_workIqEulaRepairRetryPending)
        {
            return false;
        }

        var restored =
            await new WorkIqSetupService().TryRestoreAcceptedEulaAsync(
                _state.State.WorkIqEulaAcceptedVersion,
                failure,
                _shutdown.Token);
        _workIqEulaRepairRetryPending = restored;
        return restored;
    }

    private async Task CaptureObservedWorkIqEulaAcceptanceAsync(
        string? warning)
    {
        if (MessageProviderFactory.UsesMockProvider ||
            !string.IsNullOrWhiteSpace(warning) ||
            !string.IsNullOrWhiteSpace(
                _state.State.WorkIqEulaAcceptedVersion))
        {
            return;
        }

        var setup = new WorkIqSetupService();
        var version = await setup.GetVersionAsync(_shutdown.Token);
        await setup.AcceptEulaAsync(version, _shutdown.Token);
        await _state.RecordWorkIqEulaAcceptanceAsync(
            version,
            _shutdown.Token);
        Log("WORKIQ EULA ACCEPTANCE CAPTURED versionBound=true");
    }

    private async Task OpenAlertContextAsync(string alertId)
    {
        var alert = _state.State.Alerts.FirstOrDefault(item =>
            string.Equals(item.Id, alertId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "This alert no longer exists.");
        var allowMaskedContext = false;
        if (_privacy.ShouldMask)
        {
            allowMaskedContext = ConfirmContextReveal();
            if (!allowMaskedContext)
            {
                return;
            }
        }

        if (AlertSourceLink.TryCreate(
                alert.SourceReference,
                out var sourceUri))
        {
            await OpenSourceUriAsync(sourceUri, allowMaskedContext);
            return;
        }

        var animateThinking =
            !_checking &&
            !_conversation.IsActive &&
            _mascot.IsVisible;
        var previousState = _mascot.CurrentState;
        if (animateThinking)
        {
            _mascot.SetState(PeggyState.Thinking);
        }

        try
        {
            var thread = await _provider.GetThreadAsync(
                alert.ThreadId,
                _shutdown.Token);
            if (_privacy.ShouldMask && !allowMaskedContext)
            {
                allowMaskedContext = ConfirmContextReveal();
                if (!allowMaskedContext)
                {
                    return;
                }
            }

            thread ??= CreateCachedThread(alert);
            var originalUri = thread is null
                ? null
                : AlertSourceLink.GetMessageUri(thread.Messages);
            MessageContextResolution? resolution = null;
            if (originalUri is null)
            {
                resolution = await _provider.ResolveContextAsync(
                    alert.Source,
                    alert.Summary,
                    alert.Reason,
                    alert.LastActivityUtc,
                    _shutdown.Token);
                if (_privacy.ShouldMask && !allowMaskedContext)
                {
                    allowMaskedContext = ConfirmContextReveal();
                    if (!allowMaskedContext)
                    {
                        return;
                    }
                }
                if (AlertSourceLink.TryCreate(
                        resolution?.WebUrl,
                        out var resolvedUri))
                {
                    alert.SourceReference = resolvedUri.AbsoluteUri;
                    alert.UpdatedUtc = DateTimeOffset.UtcNow;
                    await _state.SaveAsync(_shutdown.Token);
                    RefreshUi(showPersistentCards: false);
                    await OpenSourceUriAsync(
                        resolvedUri,
                        allowMaskedContext);
                    return;
                }
            }
            var window = new AlertContextWindow(
                alert,
                thread,
                originalUri is not null,
                allowMaskedContext
                    ? null
                    : () => _privacy.ShouldMask,
                resolution?.Context,
                AssistantName)
            {
                Owner = _alertCenter
            };
            if (originalUri is not null)
            {
                window.OpenOriginalRequested += () =>
                    RunUiAction(
                        () => OpenSourceUriAsync(
                            originalUri,
                            allowMaskedContext),
                        $"{AssistantName} alert context");
            }
            window.Closed += (_, _) =>
                _alertContextWindows.Remove(window);
            _alertContextWindows.Add(window);
            window.Show();
            window.Activate();
        }
        finally
        {
            if (animateThinking &&
                !_checking &&
                !_conversation.IsActive &&
                _mascot.IsVisible &&
                _mascot.CurrentState == PeggyState.Thinking)
            {
                _mascot.SetState(previousState);
            }
        }
    }

    private MessageThread? CreateCachedThread(AlertRecord alert)
    {
        var messages = GetFocusMessages(alert.Id)
            .OrderBy(message => message.ReceivedAt)
            .ToList();
        return messages.Count == 0
            ? null
            : new MessageThread(
                alert.ThreadId,
                alert.Source,
                messages[0].Subject,
                messages);
    }

    private Task OpenSourceUriAsync(
        Uri uri,
        bool allowMaskedContext = false)
    {
        if (_privacy.ShouldMask &&
            !allowMaskedContext &&
            !ConfirmContextReveal())
        {
            return Task.CompletedTask;
        }

        AlertSourceLauncher.Open(
            uri,
            _state.State.PinnedMailbox);
        return Task.CompletedTask;
    }

    private bool ConfirmContextReveal()
    {
        var confirmed = MessageBox.Show(
            _alertCenter,
            $"{AssistantName} is hiding work details while your screen may be visible. " +
            "Opening this context can reveal private Outlook or Teams content. " +
            "Open it anyway?",
            $"{AssistantName} privacy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (confirmed)
        {
            RevealAlertDetails();
        }
        return confirmed;
    }

    private bool ShouldMaskAlertContent()
        => _privacy.ShouldMask &&
           (_alertRevealUntilUtc is null ||
            _alertRevealUntilUtc <= DateTimeOffset.UtcNow);

    private DateTimeOffset? _alertRevealUntilUtc;

    private void RevealAlertDetails()
    {
        _alertRevealUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        _alertRevealTimer?.Stop();
        _alertRevealTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _alertRevealTimer.Tick += (_, _) =>
        {
            ClearAlertReveal();
            RefreshUi(showPersistentCards: false);
        };
        _alertRevealTimer.Start();
        RefreshUi(showPersistentCards: false);
    }

    private void ClearAlertReveal()
    {
        _alertRevealTimer?.Stop();
        _alertRevealTimer = null;
        _alertRevealUntilUtc = null;
    }

    private async Task StartConversationAsync(string? alertId)
    {
        if (_state.EnsureDailySession())
        {
            await ReplaceCopilotAsync(
                _state.State.Settings);
            await _state.SaveAsync(_shutdown.Token);
        }

        try
        {
            await _conversation.StartAsync(alertId);
        }
        catch (OperationCanceledException) when (
            !_conversation.IsActive ||
            _shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                $"{AssistantName} voice",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RunUiAction(
        Func<Task> action,
        string title)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (
            _shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"UI ACTION ERROR type={ex.GetType().Name}");
            MessageBox.Show(
                ex.Message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private IReadOnlyCollection<Message> GetFocusMessages(string? alertId)
    {
        if (alertId is null)
        {
            return [];
        }
        var alert = _state.State.Alerts.FirstOrDefault(item =>
            string.Equals(item.Id, alertId, StringComparison.Ordinal));
        if (alert is null)
        {
            return [];
        }

        return alert.MessageIds
            .Where(_messageCache.ContainsKey)
            .Select(id => _messageCache[id])
            .ToList();
    }

    private void CacheMessages(IEnumerable<Message> messages)
    {
        foreach (var message in messages)
        {
            _messageCache[message.Id] = message;
        }
        if (_messageCache.Count <= 500)
        {
            return;
        }

        foreach (var stale in _messageCache.Values
                     .OrderByDescending(message => message.ReceivedAt)
                     .Skip(500)
                     .Select(message => message.Id)
                     .ToList())
        {
            _messageCache.Remove(stale);
        }
    }

    private void ShowAlertCenter()
    {
        _alertCenter.ShowNearTaskbar();
    }

    private void OnSessionSwitch(
        object sender,
        SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _privacy.SetSessionLocked(true);
            ClearAlertReveal();
            CloseAlertContextWindows();
            if (_conversation is not null)
            {
                _conversation.EndConversation();
            }
            if (_bubble is not null)
            {
                _bubble.HideNow();
            }
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _privacy.SetSessionLocked(false);
            QueueScheduleEvaluation(rebaseWhenAwake: true);
        }
        RefreshUi(showPersistentCards: false);
    }

    private void OnPowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            QueueScheduleEvaluation(rebaseWhenAwake: true);
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
        => QueueScheduleEvaluation(rebaseWhenAwake: false);

    private void QueueScheduleEvaluation(bool rebaseWhenAwake)
    {
        if (_quitting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
            _ = EvaluateMonitoringScheduleAsync(
                animateWake: true,
                startCheckOnWake: true,
                rebaseWhenAwake: rebaseWhenAwake)));
    }

    private void StartScheduleTimer()
    {
        _scheduleTimer = new DispatcherTimer();
        _scheduleTimer.Tick += async (_, _) =>
        {
            _scheduleTimer.Stop();
            try
            {
                await EvaluateMonitoringScheduleAsync(
                    animateWake: true,
                    startCheckOnWake: true);
            }
            catch (OperationCanceledException) when (
                _shutdown.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log(
                    $"SCHEDULE ERROR type={ex.GetType().Name}");
                ScheduleNextEvaluation(
                    MonitoringSchedule.Evaluate(
                        _state.State.Settings,
                        DateTime.Now),
                    DateTime.Now);
            }
        };
        ScheduleNextEvaluation(
            MonitoringSchedule.Evaluate(
                _state.State.Settings,
                DateTime.Now),
            DateTime.Now);
    }

    private async Task EvaluateMonitoringScheduleAsync(
        bool animateWake,
        bool startCheckOnWake,
        bool rebaseWhenAwake = false)
    {
        var now = DateTime.Now;
        var status = MonitoringSchedule.Evaluate(
            _state.State.Settings,
            now);
        var wasSleeping = _scheduledSleeping;
        _scheduledSleeping = status.IsSleeping;
        ScheduleNextEvaluation(status, now);

        if (wasSleeping == _scheduledSleeping &&
            !(rebaseWhenAwake &&
              !_scheduledSleeping &&
              _state.State.Settings.MonitoringScheduleEnabled))
        {
            RefreshUi(showPersistentCards: false);
            return;
        }

        _monitoringGeneration++;
        _restartCheckRequested = false;
        _restartCheckForced = false;
        if (_scheduledSleeping)
        {
            _wakeCheckTimer?.Stop();
            _wakeCheckTimer = null;
            _idleTimer?.Stop();
            _bubble.HideNow();
            if (!_conversation.IsActive)
            {
                _mascot.SetState(PeggyState.Sleeping);
            }
            Log(
                $"SCHEDULE SLEEP wake=" +
                MonitoringSchedule.FormatMinute(
                    _state.State.Settings.MonitoringWakeMinute));
            RefreshUi(showPersistentCards: false);
            return;
        }

        _state.PrepareMonitoringForWake(DateTimeOffset.UtcNow);
        await _state.SaveAsync(_shutdown.Token);
        Log(
            $"SCHEDULE WAKE lookbackMinutes=" +
            PeggyDefaults.StartupLookbackMinutes);
        var shouldCheck =
            startCheckOnWake &&
            !_state.State.Settings.MonitoringPaused;
        if (animateWake &&
            _mascot.IsVisible &&
            !_conversation.IsActive)
        {
            _mascot.SetState(PeggyState.Waking);
            ScheduleWakeCheck(shouldCheck);
        }
        else
        {
            RestoreRestingState();
            if (shouldCheck)
            {
                StartCheck(force: false);
            }
        }
        RefreshUi(showPersistentCards: false);
    }

    private void ScheduleNextEvaluation(
        MonitoringScheduleStatus status,
        DateTime localNow)
    {
        if (_scheduleTimer is null || _quitting)
        {
            return;
        }

        var delay = status.NextTransitionLocal is DateTime transition
            ? transition - localNow
            : TimeSpan.FromMinutes(1);
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }
        if (delay > TimeSpan.FromMinutes(1))
        {
            delay = TimeSpan.FromMinutes(1);
        }
        _scheduleTimer.Interval = delay;
        _scheduleTimer.Start();
    }

    private void OnWakingCompleted()
    {
        RestoreRestingState();
    }

    private void ScheduleWakeCheck(bool shouldCheck)
    {
        _wakeCheckTimer?.Stop();
        _wakeCheckTimer = null;
        if (!shouldCheck)
        {
            return;
        }

        _wakeCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _wakeCheckTimer.Tick += (_, _) =>
        {
            _wakeCheckTimer!.Stop();
            _wakeCheckTimer = null;
            if (!_scheduledSleeping &&
                !_state.State.Settings.MonitoringPaused)
            {
                StartCheck(force: false);
            }
        };
        _wakeCheckTimer.Start();
    }

    private static string? GetMonitoringRemediation(
        Exception exception,
        string stage)
        => exception is CopilotClassificationException
            ? "Copilot returned an invalid monitoring result. Peggy will " +
              "retry with a smaller isolated batch."
            : exception is TimeoutException &&
           string.Equals(
               stage,
               "classification",
               StringComparison.Ordinal)
            ? "Copilot timed out while classifying a monitoring batch. " +
              "Peggy will retry the same bounded batch."
            : exception is TimeoutException &&
              string.Equals(
                  stage,
                  "retrieval",
                  StringComparison.Ordinal)
                ? "WorkIQ timed out while reading Microsoft 365. " +
                  "Peggy will retry."
            : exception.Message.Contains(
               "accept-eula",
               StringComparison.OrdinalIgnoreCase) ||
           exception.Message.Contains(
               "End User License",
               StringComparison.OrdinalIgnoreCase)
            ? "The WorkIQ EULA must be accepted. Open Settings, select " +
              "'Sign in again', then review and accept " +
              "https://github.com/microsoft/work-iq/tree/main/EULA."
            : null;

    private static string GetMonitoringFailureCode(
        Exception exception,
        string stage)
        => exception is CopilotClassificationException classification
            ? classification.Code
            : exception is TimeoutException
                ? $"{stage}-timeout"
                : exception is InvalidDataException
                    ? $"{stage}-invalid-data"
                    : exception is InvalidOperationException
                        ? $"{stage}-operation-failed"
                        : $"{stage}-unexpected";

    private void CloseAlertContextWindows()
    {
        foreach (var window in _alertContextWindows.ToList())
        {
            window.Close();
        }
        _alertContextWindows.Clear();
    }

    private static PeggySettings CloneSettings(PeggySettings settings)
        => new()
        {
            CopilotModel = settings.CopilotModel,
            CopilotReasoningEffort = settings.CopilotReasoningEffort,
            CopilotContextTier = settings.CopilotContextTier,
            IncludeActiveCasesFolder =
                settings.IncludeActiveCasesFolder,
            AdditionalOutlookFolders =
                settings.AdditionalOutlookFolders.ToList(),
            TeamsScope = settings.TeamsScope,
            MonitorTeamsChatsInSelectedScope =
                settings.MonitorTeamsChatsInSelectedScope,
            TeamsTeamWatchlist = settings.TeamsTeamWatchlist
                .Select(item => new TeamsTeamWatch
                {
                    TeamId = item.TeamId,
                    DisplayName = item.DisplayName,
                    ExcludedChannelIds =
                        item.ExcludedChannelIds.ToList()
                })
                .ToList(),
            TeamsChannelWatchlist = settings.TeamsChannelWatchlist
                .Select(item => new TeamsChannelWatch
                {
                    TeamId = item.TeamId,
                    ChannelId = item.ChannelId,
                    DisplayName = item.DisplayName
                })
                .ToList(),
            StartAtSignIn = settings.StartAtSignIn,
            MonitoringPaused = settings.MonitoringPaused,
            MonitoringScheduleEnabled =
                settings.MonitoringScheduleEnabled,
            MonitoringWakeMinute = settings.MonitoringWakeMinute,
            MonitoringSleepMinute = settings.MonitoringSleepMinute,
            SpeechEnabled = settings.SpeechEnabled,
            SpeechModelDirectory = settings.SpeechModelDirectory,
            SpeechThreads = settings.SpeechThreads,
            SpeechVoiceId = settings.SpeechVoiceId,
            SpeechSpeed = settings.SpeechSpeed,
            SpeechInputGain = settings.SpeechInputGain,
            SpeechTuningVersion = settings.SpeechTuningVersion
        };

    private async Task ReplaceCopilotAsync(PeggySettings settings)
        => await ReplaceCopilotAsync(new CopilotRunner(
            settings,
            _state.State.CopilotSessionId,
            _state.State.CopilotSessionCredits,
            account: _state.State.PinnedAccount));

    private async Task ReplaceCopilotAsync(CopilotRunner replacement)
    {
        var previous = _copilot;
        _copilot = replacement;
        StartCopilotWarmup(_copilot);
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    private void StartCopilotWarmup(CopilotRunner runner)
    {
        _copilotWarmup = WarmCopilotAsync(runner);
    }

    private async Task WarmCopilotAsync(CopilotRunner runner)
    {
        try
        {
            await runner.WarmConversationAsync(_shutdown.Token);
            Log("COPILOT SDK READY");
        }
        catch (OperationCanceledException) when (
            _shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"COPILOT SDK WARMUP ERROR type={ex.GetType().Name}");
        }
    }

    private static string LimitTrayText(string value)
        => value.Length <= 63 ? value : value[..63];

    private void RestoreRestingState()
    {
        if (_conversation is not null &&
            _conversation.IsActive)
        {
            return;
        }
        _alertCenter.ClearCriticalBanner();
        _conversationWindow.ClearCritical();

        _mascot.SetState(
            _scheduledSleeping
                ? PeggyState.Sleeping
                : _checking
                    ? PeggyState.Thinking
                    : PeggyState.Idle);
    }

    private void ReturnToRestAfter(TimeSpan delay)
    {
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer { Interval = delay };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer!.Stop();
            if (!_checking && !_conversation.IsActive)
            {
                RestoreRestingState();
            }
        };
        _idleTimer.Start();
    }

    private void Say(string text)
    {
        if (!_mascot.IsVisible)
        {
            _mascot.ShowAnimated();
        }
        _bubble.Say(text, _mascot.HeadAnchor());
    }

    private void ShowState(PeggyState state, string? bubble)
    {
        if (!_mascot.IsVisible)
        {
            _mascot.ShowAnimated();
        }
        _mascot.SetState(state);
        if (bubble is not null)
        {
            _bubble.Say(bubble, _mascot.HeadAnchor(), 5);
        }
        else
        {
            _bubble.HideNow();
        }
    }

    private async void EntranceDemo()
    {
        _bubble.HideNow();
        if (!_mascot.IsVisible)
        {
            _mascot.ShowAnimated();
            return;
        }
        _mascot.HideAnimated();
        await Task.Delay(1300);
        _mascot.ShowAnimated();
    }

    private async void PlayAllAnimations()
    {
        if (!_mascot.IsVisible)
        {
            _mascot.ShowAnimated();
        }
        await Task.Delay(400);

        _mascot.SetState(PeggyState.Idle);
        _bubble.Say(
            IdleDescription,
            _mascot.HeadAnchor(),
            3);
        await Task.Delay(3200);
        _mascot.SetState(PeggyState.Listening);
        _bubble.Say(
            "Listening, head tilt and ripples.",
            _mascot.HeadAnchor(),
            3);
        await Task.Delay(3200);
        _bubble.HideNow();
        _mascot.SetState(PeggyState.Thinking);
        await Task.Delay(3500);
        _mascot.SetState(PeggyState.NewDigest);
        _bubble.Say(
            CelebrationDescription,
            _mascot.HeadAnchor(),
            4);
        await Task.Delay(4200);
        _mascot.SetState(PeggyState.Sleeping);
        _bubble.Say(
            $"Sleeping until {MonitoringSchedule.FormatMinute(
                _state.State.Settings.MonitoringWakeMinute)}.",
            _mascot.HeadAnchor(),
            3);
        await Task.Delay(3200);
        _mascot.SetState(PeggyState.Waking);
        _bubble.Say(
            $"Good morning from {AssistantName}.",
            _mascot.HeadAnchor(),
            3);
        await Task.Delay(1800);
        RestoreRestingState();
    }

    private void ToggleMascot()
    {
        if (_mascot.IsVisible)
        {
            _bubble.HideNow();
            _mascot.HideAnimated();
        }
        else
        {
            _mascot.ShowAnimated();
        }
    }

    private void ScheduleMockSmokeExit(IReadOnlyCollection<string> args)
    {
        const string prefix = "--mock-smoke-exit-seconds=";
        var configured = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (configured is null ||
            !string.Equals(
                Environment.GetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER"),
                "mock",
                StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                configured[prefix.Length..],
                out var seconds) ||
            seconds is < 5 or > 300)
        {
            return;
        }

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds)
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await QuitAppAsync();
        };
        timer.Start();
    }

    private async Task QuitAppAsync()
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        _poll?.Stop();
        _scheduleTimer?.Stop();
        _idleTimer?.Stop();
        _alertRevealTimer?.Stop();
        _wakeCheckTimer?.Stop();
        await _conversation.DisposeAsync();
        _bubble.HideNow();
        _tray.Visible = false;
        _shutdown.Cancel();

        try
        {
            await _activeCheck;
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            await _copilotWarmup;
        }
        catch (OperationCanceledException)
        {
        }
        await _copilot.DisposeAsync();
        try
        {
            await _state.SaveAsync();
        }
        catch (Exception ex)
        {
            Log($"STATE SAVE ERROR type={ex.GetType().Name}");
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        var trayIcon = _tray.Icon;
        _tray.Dispose();
        trayIcon?.Dispose();
        _troubleshootingWindow?.Close();
        CloseAlertContextWindows();
        _alertCenter.Close();
        _conversationWindow.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
        _shutdown.Dispose();
        _singleInstance.Dispose();
        base.OnExit(e);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                ResolveLogPath(),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveLogPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            "PEGGY_LOG_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "peggy.log")
            : Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configured.Trim().Trim('"')));
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var body = new SolidBrush(
                Color.FromArgb(0x8a, 0x5a, 0x3b));
            using var belly = new SolidBrush(
                Color.FromArgb(0xe2, 0x69, 0x4a));
            using var white = new SolidBrush(Color.White);
            using var beak = new SolidBrush(
                Color.FromArgb(0xf6, 0xa9, 0x3a));
            using var eye = new SolidBrush(
                Color.FromArgb(0x2b, 0x2b, 0x2b));
            graphics.FillEllipse(body, 5, 3, 22, 26);
            graphics.FillEllipse(belly, 9, 14, 13, 13);
            graphics.FillEllipse(white, 12, 8, 7, 7);
            graphics.FillEllipse(eye, 14, 10, 3, 3);
            graphics.FillPolygon(
                beak,
                [
                    new System.Drawing.Point(21, 12),
                    new System.Drawing.Point(28, 15),
                    new System.Drawing.Point(21, 18)
                ]);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
