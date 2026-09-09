namespace Peggy.Core;

public enum AlertTier
{
    Critical,
    Attention,
    Awareness
}

public enum AlertStatus
{
    Active,
    Snoozed,
    Resolved,
    Dismissed
}

public enum FeedbackKind
{
    LessLikeThis,
    AlwaysAlert,
    CorrectedTier
}

public enum ApprovalMethod
{
    Click,
    Voice,
    TypedConfirmation
}

public enum WorkActionKind
{
    MarkMessageRead,
    CreateEmailDraft,
    SendEmail,
    CreateReplyDraft,
    SendReply,
    SendChatMessage,
    CreateCalendarEvent,
    UpdateCalendarEvent,
    CancelCalendarEvent,
    CreateTask,
    UpdateTask,
    CompleteTask,
    DeleteMessage,
    DeleteTask
}

public enum TeamsMonitoringScope
{
    AllActivity,
    SelectedTeamsAndChannels,
    Off
}

public sealed class PeggySettings
{
    public string CopilotModel { get; set; } = PeggyDefaults.CopilotModel;

    public List<string> AdditionalOutlookFolders { get; set; } = [];

    public TeamsMonitoringScope TeamsScope { get; set; } =
        TeamsMonitoringScope.AllActivity;

    public bool MonitorTeamsChatsInSelectedScope { get; set; }

    public List<TeamsTeamWatch> TeamsTeamWatchlist { get; set; } = [];

    public List<TeamsChannelWatch> TeamsChannelWatchlist { get; set; } = [];

    public bool StartAtSignIn { get; set; } = true;

    public bool MonitoringPaused { get; set; }

    public bool MonitoringScheduleEnabled { get; set; } = true;

    public int MonitoringWakeMinute { get; set; } =
        PeggyDefaults.DefaultWakeMinute;

    public int MonitoringSleepMinute { get; set; } =
        PeggyDefaults.DefaultSleepMinute;

    public bool SpeechEnabled { get; set; } = true;

    public string SpeechModelDirectory { get; set; } = string.Empty;

    public int SpeechThreads { get; set; } = 8;

    public int SpeechVoiceId { get; set; }

    public float SpeechSpeed { get; set; } = 1.15F;

    public float SpeechInputGain { get; set; } = 12.0F;

    public int SpeechTuningVersion { get; set; }
}

public sealed class TeamsTeamWatch
{
    public string TeamId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> ExcludedChannelIds { get; set; } = [];
}

public sealed class TeamsChannelWatch
{
    public string TeamId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

public sealed class MonitoringCursorState
{
    public bool Initialized { get; set; }

    public bool InitialOutlookScanComplete { get; set; }

    public DateTimeOffset? InitialScanStartedUtc { get; set; }

    public DateTimeOffset? OutlookScanStartedUtc { get; set; }

    public DateTimeOffset? TeamsScanStartedUtc { get; set; }

    public DateTimeOffset? TeamsWatermarkUtc { get; set; }

    public List<string> TeamsBoundaryMessageIds { get; set; } = [];

    public DateTimeOffset? TeamsSearchWindowEndUtc { get; set; }

    public int TeamsSearchOffset { get; set; }

    public List<string> TeamsSearchProcessedHitIds { get; set; } = [];

    public DateTimeOffset? DirectChatWatermarkUtc { get; set; }

    public List<string> DirectChatBoundaryMessageIds { get; set; } = [];

    public DateTimeOffset? DirectChatScanWindowEndUtc { get; set; }

    public List<string> DirectChatProcessedMessageIds { get; set; } = [];

    public DateTimeOffset? PromptWatermarkUtc { get; set; }

    public DateTimeOffset? OutlookWatermarkUtc { get; set; }

    public List<string> OutlookBoundaryMessageIds { get; set; } = [];

    public Dictionary<string, string> OutlookDeltaLinks { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, DateTimeOffset> ChannelWatermarksUtc { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> ChannelBoundaryMessageIds { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, string> ChannelDeltaLinks { get; set; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, int> ChannelPageSizes { get; set; } =
        new(StringComparer.Ordinal);

    public string? ChannelScanResumeKey { get; set; }

    public bool ChannelContinuationPass { get; set; }

    public List<ParticipatedChannelThread> ParticipatedChannelThreads { get; set; } = [];
}

public sealed class ParticipatedChannelThread
{
    public string TeamId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;

    public string RootMessageId { get; set; } = string.Empty;

    public DateTimeOffset LastActivityUtc { get; set; }
}

public sealed class AlertRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ThreadId { get; set; } = string.Empty;

    public MessageSource Source { get; set; }

    public AlertTier Tier { get; set; }

    public AlertStatus Status { get; set; } = AlertStatus.Active;

    public string Summary { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public string SourceReference { get; set; } = string.Empty;

    public List<string> MessageIds { get; set; } = [];

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastActivityUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public DateTimeOffset? SnoozedUntilUtc { get; set; }

    public string MaterialHash { get; set; } = string.Empty;
}

public sealed class FeedbackRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ThreadId { get; set; } = string.Empty;

    public FeedbackKind Kind { get; set; }

    public AlertTier? CorrectedTier { get; set; }

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class SeenMessageMarker
{
    public string MessageId { get; set; } = string.Empty;

    public DateTimeOffset SeenUtc { get; set; }
}

public sealed class CreditUsageEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset TimestampUtc { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public decimal Credits { get; set; }

    public decimal SessionCredits { get; set; }
}

public sealed class AuditLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset TimestampUtc { get; set; }

    public WorkActionKind Action { get; set; }

    public List<string> TargetIds { get; set; } = [];

    public ApprovalMethod ApprovalMethod { get; set; }

    public string Result { get; set; } = string.Empty;

    public bool Verified { get; set; }
}

public sealed class PeggyStateDocument
{
    public int Version { get; set; } = 1;

    public string PinnedAccount { get; set; } = string.Empty;

    public string PinnedMailbox { get; set; } = string.Empty;

    public string WorkIqEulaAcceptedVersion { get; set; } = string.Empty;

    public PeggySettings Settings { get; set; } = new();

    public DateOnly CopilotSessionDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public string CopilotSessionId { get; set; } = Guid.NewGuid().ToString();

    public decimal CopilotSessionCredits { get; set; }

    public MonitoringCursorState MonitoringCursor { get; set; } = new();

    public DateTimeOffset? LastSuccessfulMonitoringUtc { get; set; }

    public int ConsecutiveMonitoringFailures { get; set; }

    public bool MonitoringInterrupted { get; set; }

    public List<AlertRecord> Alerts { get; set; } = [];

    public List<FeedbackRule> FeedbackRules { get; set; } = [];

    public List<SeenMessageMarker> SeenMessages { get; set; } = [];

    public List<CreditUsageEntry> CreditHistory { get; set; } = [];

    public List<AuditLogEntry> AuditLog { get; set; } = [];
}
