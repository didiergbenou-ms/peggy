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
    {
        var normalized = account?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException(
                "A verified Microsoft 365 account is required.",
                nameof(account));
        }

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
            State.PinnedAccount = normalized;
            try
            {
                await _store.SaveAsync(State, ct);
            }
            catch
            {
                State.PinnedAccount = previous;
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
        bool resetMonitoring,
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
                MonitoringCursor = resetMonitoring
                    ? CreateRebasedMonitoringCursor(
                        previous.MonitoringCursor,
                        nowUtc)
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
            ParticipatedChannelThreads = participatedThreads
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
