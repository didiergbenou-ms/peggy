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

    public async Task LoadForSetupAsync(CancellationToken ct = default)
    {
        await _stateGate.WaitAsync(ct);
        try { State = await _store.LoadAsync(ct); }
        finally { _stateGate.Release(); }
    }

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
        => await PinWorkIqIdentityAsync(
            account,
            mailbox,
            userPrincipalName: null,
            principalId: null,
            ct);

    public async Task PinWorkIqIdentityAsync(
        string account,
        string? mailbox,
        string? userPrincipalName,
        CancellationToken ct = default)
        => await PinWorkIqIdentityAsync(
            account,
            mailbox,
            userPrincipalName,
            principalId: null,
            ct);

    public async Task PinWorkIqIdentityAsync(
        string account,
        string? mailbox,
        string? userPrincipalName,
        string? principalId,
        CancellationToken ct = default)
    {
        var normalized = WorkIqSetupService.NormalizeAccount(account);
        var normalizedMailbox = string.IsNullOrWhiteSpace(mailbox)
            ? normalized
            : WorkIqSetupService.NormalizeAccount(mailbox);
        var normalizedUserPrincipalName =
            string.IsNullOrWhiteSpace(userPrincipalName)
                ? string.Empty
                : WorkIqSetupService.NormalizeAccount(userPrincipalName);
        var normalizedPrincipalId = principalId?.Trim() ?? string.Empty;

        await _stateGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(State.PinnedPrincipalId) &&
                (string.IsNullOrWhiteSpace(normalizedPrincipalId) ||
                 !string.Equals(
                     State.PinnedPrincipalId,
                     normalizedPrincipalId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Peggy is already pinned to a different Microsoft 365 " +
                    "principal. Use Peggy's account-switch flow to change it.");
            }
            if (string.IsNullOrWhiteSpace(State.PinnedPrincipalId) &&
                !string.IsNullOrWhiteSpace(State.PinnedAccount) &&
                !string.Equals(
                    State.PinnedAccount,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Peggy is already pinned to a different Microsoft 365 " +
                    "account. Use Peggy's account-switch flow to change it.");
            }

            var previous = State.PinnedAccount;
            var previousMailbox = State.PinnedMailbox;
            var previousUserPrincipalName =
                State.PinnedUserPrincipalName;
            var previousPrincipalId = State.PinnedPrincipalId;
            State.PinnedAccount = normalized;
            State.PinnedMailbox = normalizedMailbox;
            State.PinnedUserPrincipalName =
                normalizedUserPrincipalName.Length == 0
                    ? previousUserPrincipalName
                    : normalizedUserPrincipalName;
            State.PinnedPrincipalId =
                normalizedPrincipalId.Length == 0
                    ? previousPrincipalId
                    : normalizedPrincipalId;
            try
            {
                await _store.SaveAsync(State, ct);
            }
            catch
            {
                State.PinnedAccount = previous;
                State.PinnedMailbox = previousMailbox;
                State.PinnedUserPrincipalName =
                    previousUserPrincipalName;
                State.PinnedPrincipalId = previousPrincipalId;
                throw;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task SwitchWorkIqIdentityAsync(
        WorkIqAccountIdentity identity,
        string copilotSessionId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedAccount = WorkIqSetupService.NormalizeAccount(
            identity.Account);
        var normalizedMailbox = WorkIqSetupService.NormalizeAccount(
            identity.Mail ?? identity.UserPrincipalName ?? normalizedAccount);
        var acceptedVersion = identity.EulaAcceptedVersion?.Trim();
        if (string.IsNullOrWhiteSpace(acceptedVersion))
        {
            throw new ArgumentException(
                "An accepted WorkIQ version is required.",
                nameof(identity));
        }
        if (string.IsNullOrWhiteSpace(identity.PrincipalId))
        {
            throw new ArgumentException(
                "A verified Microsoft 365 principal ID is required.",
                nameof(identity));
        }
        if (!Guid.TryParse(copilotSessionId, out _))
        {
            throw new ArgumentException(
                "A valid replacement Copilot session ID is required.",
                nameof(copilotSessionId));
        }

        await _stateGate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(State.PinnedAccount) ||
                WorkIqSetupService.RepresentsSameAccount(
                    State.PinnedAccount,
                    State.PinnedMailbox,
                    State.PinnedUserPrincipalName,
                    State.PinnedPrincipalId,
                    identity))
            {
                throw new InvalidOperationException(
                    "Account switching requires a different pinned Microsoft " +
                    "365 account.");
            }

            var candidate = new PeggyStateDocument
            {
                Version = State.Version,
                PinnedAccount = normalizedAccount,
                PinnedMailbox = normalizedMailbox,
                PinnedUserPrincipalName =
                    string.IsNullOrWhiteSpace(identity.UserPrincipalName)
                        ? string.Empty
                        : WorkIqSetupService.NormalizeAccount(
                            identity.UserPrincipalName),
                PinnedPrincipalId = identity.PrincipalId.Trim(),
                WorkIqEulaAcceptedVersion = acceptedVersion,
                Settings = CreateSettingsForAccountSwitch(State.Settings),
                CopilotSessionDate = DateOnly.FromDateTime(
                    nowUtc.ToLocalTime().DateTime),
                CopilotSessionId = copilotSessionId,
                MonitoringCursor = CreateRebasedMonitoringCursor(
                    new MonitoringCursorState(),
                    nowUtc)
            };
            await _store.SaveAsync(candidate, ct);
            State = candidate;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task RebindWorkIqIdentityAsync(
        WorkIqAccountIdentity identity,
        string copilotSessionId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var normalizedAccount = WorkIqSetupService.NormalizeAccount(
            identity.Account);
        var normalizedMailbox = WorkIqSetupService.NormalizeAccount(
            identity.Mail ?? identity.UserPrincipalName ?? normalizedAccount);
        var normalizedUserPrincipalName =
            string.IsNullOrWhiteSpace(identity.UserPrincipalName)
                ? string.Empty
                : WorkIqSetupService.NormalizeAccount(
                    identity.UserPrincipalName);
        var principalId = identity.PrincipalId?.Trim();
        var acceptedVersion = identity.EulaAcceptedVersion?.Trim();
        if (string.IsNullOrWhiteSpace(principalId) ||
            string.IsNullOrWhiteSpace(acceptedVersion) ||
            !Guid.TryParse(copilotSessionId, out _))
        {
            throw new ArgumentException(
                "A verified identity, accepted WorkIQ version, and valid " +
                "Copilot session ID are required.",
                nameof(identity));
        }
        await _stateGate.WaitAsync(ct);
        try
        {
            if (!WorkIqSetupService.RepresentsSameAccount(
                    State.PinnedAccount,
                    State.PinnedMailbox,
                    State.PinnedUserPrincipalName,
                    State.PinnedPrincipalId,
                    identity))
            {
                throw new InvalidOperationException(
                    "A different Microsoft 365 principal requires Peggy's " +
                    "account-switch flow.");
            }

            var previousAccount = State.PinnedAccount;
            var previousMailbox = State.PinnedMailbox;
            var previousUserPrincipalName =
                State.PinnedUserPrincipalName;
            var previousPrincipalId = State.PinnedPrincipalId;
            var previousEulaVersion =
                State.WorkIqEulaAcceptedVersion;
            var previousSessionDate = State.CopilotSessionDate;
            var previousSessionId = State.CopilotSessionId;
            var previousSessionCredits = State.CopilotSessionCredits;
            State.PinnedAccount = normalizedAccount;
            State.PinnedMailbox = normalizedMailbox;
            State.PinnedUserPrincipalName =
                normalizedUserPrincipalName;
            State.PinnedPrincipalId = principalId;
            State.WorkIqEulaAcceptedVersion = acceptedVersion;
            State.CopilotSessionDate = DateOnly.FromDateTime(
                nowUtc.ToLocalTime().DateTime);
            State.CopilotSessionId = copilotSessionId;
            State.CopilotSessionCredits = 0;
            try
            {
                await _store.SaveAsync(State, ct);
            }
            catch
            {
                State.PinnedAccount = previousAccount;
                State.PinnedMailbox = previousMailbox;
                State.PinnedUserPrincipalName =
                    previousUserPrincipalName;
                State.PinnedPrincipalId = previousPrincipalId;
                State.WorkIqEulaAcceptedVersion =
                    previousEulaVersion;
                State.CopilotSessionDate = previousSessionDate;
                State.CopilotSessionId = previousSessionId;
                State.CopilotSessionCredits = previousSessionCredits;
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
                PinnedUserPrincipalName =
                    previous.PinnedUserPrincipalName,
                PinnedPrincipalId = previous.PinnedPrincipalId,
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
                LastMonitoringFailure = previous.LastMonitoringFailure,
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

    public static PeggySettings CreateSettingsForAccountSwitch(
        PeggySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new()
        {
            CopilotModel = settings.CopilotModel,
            CopilotReasoningEffort = settings.CopilotReasoningEffort,
            CopilotContextTier = settings.CopilotContextTier,
            IncludeActiveCasesFolder = false,
            OutlookScope = settings.OutlookScope,
            TeamsScope = settings.TeamsScope,
            MonitorTeamsChatsInSelectedScope =
                settings.MonitorTeamsChatsInSelectedScope,
            StartAtSignIn = settings.StartAtSignIn,
            MonitoringPaused = settings.MonitoringPaused,
            MonitoringIntervalMinutes = settings.MonitoringIntervalMinutes,
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
    }

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

    public bool RecordMonitoringFailure(
        MonitoringFailureDetails? details = null)
    {
        var wasInterrupted = State.MonitoringInterrupted;
        State.ConsecutiveMonitoringFailures++;
        if (details is not null)
        {
            State.LastMonitoringFailure = details;
        }
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
        State.LastMonitoringFailure = null;
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
