using System.Runtime.Versioning;

namespace Peggy.Core;

[SupportedOSPlatform("windows")]
public sealed class PeggyStateManager
{
    private readonly EncryptedStateStore _store;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    public PeggyStateManager(EncryptedStateStore? store = null)
    {
        _store = store ?? new EncryptedStateStore();
    }

    public PeggyStateDocument State { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            State = await _store.LoadAsync(ct);
            EnsureDailySession();
            PrepareMonitoringForProcessStart(DateTimeOffset.UtcNow);
            await _store.SaveAsync(State, ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void PrepareMonitoringForProcessStart(DateTimeOffset nowUtc)
        => RebaseMonitoringWindow(State, nowUtc);

    public void PrepareMonitoringForWake(DateTimeOffset nowUtc)
        => RebaseMonitoringWindow(State, nowUtc);

    public async Task PinWorkIqAccountAsync(
        string account,
        CancellationToken ct = default)
        => await PinWorkIqIdentityAsync(
            account,
            mailbox: null,
            ct);

    public async Task PinWorkIqIdentityAsync(
        string account,
        string? mailbox,
        CancellationToken ct = default)
    {
        var normalized = WorkIqSetupService.NormalizeAccount(account);
        var normalizedMailbox = string.IsNullOrWhiteSpace(mailbox)
            ? normalized
            : WorkIqSetupService.NormalizeAccount(mailbox);

        await _stateGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(State.PinnedAccount) &&
                !string.Equals(
                    State.PinnedAccount,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Peggy is already pinned to a different Microsoft 365 " +
                    "account. Clear Peggy's encrypted state before switching " +
                    "accounts.");
            }

            var previous = State.PinnedAccount;
            var previousMailbox = State.PinnedMailbox;
            State.PinnedAccount = normalized;
            State.PinnedMailbox = normalizedMailbox;
            try
            {
                await _store.SaveAsync(State, ct);
            }
            catch
            {
                State.PinnedAccount = previous;
                State.PinnedMailbox = previousMailbox;
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task RecordWorkIqEulaAcceptanceAsync(
        string version,
        CancellationToken ct = default)
    {
        var normalized = version?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException(
                "An accepted WorkIQ version is required.",
                nameof(version));
        }

        await _stateGate.WaitAsync(ct);
        try
        {
            var previous = State.WorkIqEulaAcceptedVersion;
            State.WorkIqEulaAcceptedVersion = normalized;
            try
            {
                await _store.SaveAsync(State, ct);
            }
            catch
            {
                State.WorkIqEulaAcceptedVersion = previous;
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ApplySettingsAsync(
        PeggySettings settings,
        bool resetOutlookMonitoring,
        bool resetTeamsMonitoring,
        bool resetCopilotSession,
        string copilotSessionId,
        decimal copilotSessionCredits,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _stateGate.WaitAsync(ct);
        try
        {
            var previous = State;
            var candidate = new PeggyStateDocument
            {
                Version = previous.Version,
                PinnedAccount = previous.PinnedAccount,
                PinnedMailbox = previous.PinnedMailbox,
                WorkIqEulaAcceptedVersion =
                    previous.WorkIqEulaAcceptedVersion,
                Settings = settings,
                CopilotSessionDate = resetCopilotSession
                    ? DateOnly.FromDateTime(nowUtc.ToLocalTime().DateTime)
                    : previous.CopilotSessionDate,
                CopilotSessionId = resetCopilotSession
                    ? copilotSessionId
                    : previous.CopilotSessionId,
                CopilotSessionCredits = resetCopilotSession
                    ? copilotSessionCredits
                    : previous.CopilotSessionCredits,
                MonitoringCursor =
                    resetOutlookMonitoring || resetTeamsMonitoring
                    ? CreateSettingsAdjustedMonitoringCursor(
                        previous.MonitoringCursor,
                        nowUtc,
                        resetOutlookMonitoring,
                        resetTeamsMonitoring)
                    : previous.MonitoringCursor,
                LastSuccessfulMonitoringUtc =
                    previous.LastSuccessfulMonitoringUtc,
                ConsecutiveMonitoringFailures =
                    previous.ConsecutiveMonitoringFailures,
                MonitoringInterrupted = previous.MonitoringInterrupted,
                Alerts = previous.Alerts,
                FeedbackRules = previous.FeedbackRules,
                SeenMessages = previous.SeenMessages,
                CreditHistory = previous.CreditHistory,
                AuditLog = previous.AuditLog
            };
            State = candidate;
            try
            {
                await _store.SaveAsync(candidate, ct);
            }
            catch
            {
                previous.LastSuccessfulMonitoringUtc =
                    candidate.LastSuccessfulMonitoringUtc;
                previous.ConsecutiveMonitoringFailures =
                    candidate.ConsecutiveMonitoringFailures;
                previous.MonitoringInterrupted =
                    candidate.MonitoringInterrupted;
                if (!resetCopilotSession)
                {
                    previous.CopilotSessionCredits =
                        candidate.CopilotSessionCredits;
                }
                State = previous;
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static void RebaseMonitoringWindow(
        PeggyStateDocument state,
        DateTimeOffset nowUtc)
        => state.MonitoringCursor = CreateRebasedMonitoringCursor(
            state.MonitoringCursor,
            nowUtc);

    private static MonitoringCursorState CreateRebasedMonitoringCursor(
        MonitoringCursorState current,
        DateTimeOffset nowUtc)
    {
        var participatedThreads = current
            .ParticipatedChannelThreads
            .Select(thread => new ParticipatedChannelThread
            {
                TeamId = thread.TeamId,
                ChannelId = thread.ChannelId,
                RootMessageId = thread.RootMessageId,
                LastActivityUtc = thread.LastActivityUtc
            })
            .ToList();
        return new MonitoringCursorState
        {
            InitialScanStartedUtc = nowUtc.AddMinutes(
                -PeggyDefaults.StartupLookbackMinutes),
            OutlookScanStartedUtc = nowUtc.AddMinutes(
                -PeggyDefaults.StartupLookbackMinutes),
            TeamsScanStartedUtc = nowUtc.AddMinutes(
                -PeggyDefaults.StartupLookbackMinutes),
            ParticipatedChannelThreads = participatedThreads
        };
    }

    private static MonitoringCursorState CreateSettingsAdjustedMonitoringCursor(
        MonitoringCursorState current,
        DateTimeOffset nowUtc,
        bool resetOutlook,
        bool resetTeams)
    {
        var cursor = CloneMonitoringCursor(current);
        var baseline = nowUtc.AddMinutes(
            -PeggyDefaults.StartupLookbackMinutes);
        cursor.Initialized = false;
        if (resetOutlook)
        {
            cursor.InitialOutlookScanComplete = false;
            cursor.OutlookScanStartedUtc = baseline;
            cursor.OutlookWatermarkUtc = null;
            cursor.OutlookBoundaryMessageIds.Clear();
            cursor.OutlookDeltaLinks.Clear();
        }
        if (resetTeams)
        {
            cursor.TeamsScanStartedUtc = baseline;
            cursor.TeamsWatermarkUtc = null;
            cursor.TeamsBoundaryMessageIds.Clear();
            cursor.TeamsSearchWindowEndUtc = null;
            cursor.TeamsSearchOffset = 0;
            cursor.TeamsSearchProcessedHitIds.Clear();
            cursor.DirectChatWatermarkUtc = null;
            cursor.DirectChatBoundaryMessageIds.Clear();
            cursor.DirectChatScanWindowEndUtc = null;
            cursor.DirectChatProcessedMessageIds.Clear();
            cursor.PromptWatermarkUtc = null;
            cursor.ChannelWatermarksUtc.Clear();
            cursor.ChannelBoundaryMessageIds.Clear();
            cursor.ChannelDeltaLinks.Clear();
            cursor.ChannelPageSizes.Clear();
            cursor.ChannelScanResumeKey = null;
            cursor.ChannelContinuationPass = false;
        }
        return cursor;
    }

    private static MonitoringCursorState CloneMonitoringCursor(
        MonitoringCursorState cursor)
        => new()
        {
            Initialized = cursor.Initialized,
            InitialOutlookScanComplete = cursor.InitialOutlookScanComplete,
            InitialScanStartedUtc = cursor.InitialScanStartedUtc,
            OutlookScanStartedUtc = cursor.OutlookScanStartedUtc,
            TeamsScanStartedUtc = cursor.TeamsScanStartedUtc,
            TeamsWatermarkUtc = cursor.TeamsWatermarkUtc,
            TeamsBoundaryMessageIds =
                cursor.TeamsBoundaryMessageIds.ToList(),
            TeamsSearchWindowEndUtc = cursor.TeamsSearchWindowEndUtc,
            TeamsSearchOffset = cursor.TeamsSearchOffset,
            TeamsSearchProcessedHitIds =
                cursor.TeamsSearchProcessedHitIds.ToList(),
            DirectChatWatermarkUtc = cursor.DirectChatWatermarkUtc,
            DirectChatBoundaryMessageIds =
                cursor.DirectChatBoundaryMessageIds.ToList(),
            DirectChatScanWindowEndUtc =
                cursor.DirectChatScanWindowEndUtc,
            DirectChatProcessedMessageIds =
                cursor.DirectChatProcessedMessageIds.ToList(),
            PromptWatermarkUtc = cursor.PromptWatermarkUtc,
            OutlookWatermarkUtc = cursor.OutlookWatermarkUtc,
            OutlookBoundaryMessageIds =
                cursor.OutlookBoundaryMessageIds.ToList(),
            OutlookDeltaLinks = new Dictionary<string, string>(
                cursor.OutlookDeltaLinks,
                StringComparer.Ordinal),
            ChannelWatermarksUtc =
                new Dictionary<string, DateTimeOffset>(
                    cursor.ChannelWatermarksUtc,
                    StringComparer.Ordinal),
            ChannelBoundaryMessageIds =
                cursor.ChannelBoundaryMessageIds.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.Ordinal),
            ChannelDeltaLinks = new Dictionary<string, string>(
                cursor.ChannelDeltaLinks,
                StringComparer.Ordinal),
            ChannelPageSizes = new Dictionary<string, int>(
                cursor.ChannelPageSizes,
                StringComparer.Ordinal),
            ChannelScanResumeKey = cursor.ChannelScanResumeKey,
            ChannelContinuationPass = cursor.ChannelContinuationPass,
            ParticipatedChannelThreads = cursor.ParticipatedChannelThreads
                .Select(thread => new ParticipatedChannelThread
                {
                    TeamId = thread.TeamId,
                    ChannelId = thread.ChannelId,
                    RootMessageId = thread.RootMessageId,
                    LastActivityUtc = thread.LastActivityUtc
                })
                .ToList()
        };

    public bool EnsureDailySession()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (State.CopilotSessionDate == today &&
            !string.IsNullOrWhiteSpace(State.CopilotSessionId))
        {
            return false;
        }

        State.CopilotSessionDate = today;
        State.CopilotSessionId = Guid.NewGuid().ToString();
        State.CopilotSessionCredits = 0;
        return true;
    }

    public decimal GetTodayCredits()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return State.CreditHistory
            .Where(entry =>
                DateOnly.FromDateTime(entry.TimestampUtc.ToLocalTime().DateTime) == today)
            .Sum(entry => entry.Credits);
    }

    public void RecordCredit(
        string operation,
        string model,
        decimal credits,
        decimal sessionCredits,
        DateTimeOffset timestampUtc)
    {
        State.CopilotSessionCredits = sessionCredits;
        State.CreditHistory.Add(new CreditUsageEntry
        {
            TimestampUtc = timestampUtc,
            Operation = operation,
            Model = model,
            Credits = credits,
            SessionCredits = sessionCredits
        });
    }

    public bool RecordMonitoringFailure()
    {
        var wasInterrupted = State.MonitoringInterrupted;
        State.ConsecutiveMonitoringFailures++;
        if (State.ConsecutiveMonitoringFailures >=
            PeggyDefaults.MonitoringFailureThreshold)
        {
            State.MonitoringInterrupted = true;
        }

        return !wasInterrupted && State.MonitoringInterrupted;
    }

    public bool RecordMonitoringSuccess(DateTimeOffset timestampUtc)
    {
        var wasInterrupted = State.MonitoringInterrupted;
        State.ConsecutiveMonitoringFailures = 0;
        State.MonitoringInterrupted = false;
        State.LastSuccessfulMonitoringUtc = timestampUtc;
        return wasInterrupted;
    }

    public HashSet<string> GetSeenMessageIds()
        => State.SeenMessages
            .Select(marker => marker.MessageId)
            .ToHashSet(StringComparer.Ordinal);

    public void MarkMessagesSeen(
        IEnumerable<string> messageIds,
        DateTimeOffset timestampUtc)
    {
        var existing = State.SeenMessages.ToDictionary(
            marker => marker.MessageId,
            StringComparer.Ordinal);
        foreach (var messageId in messageIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (existing.TryGetValue(messageId, out var marker))
            {
                marker.SeenUtc = timestampUtc;
            }
            else
            {
                var added = new SeenMessageMarker
                {
                    MessageId = messageId,
                    SeenUtc = timestampUtc
                };
                State.SeenMessages.Add(added);
                existing[messageId] = added;
            }
        }
    }

    public void RecordAudit(
        WorkActionKind action,
        IEnumerable<string> targetIds,
        ApprovalMethod approvalMethod,
        string result,
        bool verified,
        DateTimeOffset timestampUtc)
    {
        State.AuditLog.Add(new AuditLogEntry
        {
            TimestampUtc = timestampUtc,
            Action = action,
            TargetIds = targetIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            ApprovalMethod = approvalMethod,
            Result = result,
            Verified = verified
        });
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            await _store.SaveAsync(State, ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }
}
