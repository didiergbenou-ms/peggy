namespace Peggy.Core;

/// <summary>Where a message came from.</summary>
public enum MessageSource
{
    Email,
    Teams
}

/// <summary>A single message from Outlook email or Microsoft Teams.</summary>
public record Message(
    string Id,
    MessageSource Source,
    string ThreadId,
    string From,
    string Subject,
    string Preview,
    DateTimeOffset ReceivedAt,
    bool IsRead)
{
    public bool IsFromMe { get; init; }

    public DateTimeOffset? SentAtUtc { get; init; }

    public bool IsLikedByMe { get; init; }

    public string? WebUrl { get; init; }

    public string? TeamId { get; init; }

    public string? ChannelId { get; init; }

    public string? RootMessageId { get; init; }

    public bool IsMention { get; init; }

    public bool IsWatchlistedChannel { get; init; }
}

/// <summary>A conversation thread and its messages, oldest to newest.</summary>
public record MessageThread(
    string Id,
    MessageSource Source,
    string Subject,
    IReadOnlyList<Message> Messages);

/// <summary>
/// Fresh thread context. IsComplete is false whenever relevant messages may be
/// missing; partial or unsupported reads must never justify suppressing an alert.
/// </summary>
public sealed record AlertThreadRefreshResult(
    MessageThread? Thread,
    bool IsComplete,
    string? Warning = null);

public sealed record MessageContextResolution(
    string? WebUrl,
    string Context);

/// <summary>Result of creating a draft reply. The draft is never sent.</summary>
public record DraftResult(
    string DraftId,
    string MessageId,
    string Body,
    string Status);

/// <summary>Generic result for an action tool.</summary>
public record OperationResult(bool Success, string Detail);

public sealed record MessageChangeBatch(
    IReadOnlyList<Message> Messages,
    MonitoringCursorState Cursor,
    bool HasMore,
    string? Warning = null);

public sealed class WorkActionRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public WorkActionKind Kind { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public string RelatedMessageId { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public List<string> Recipients { get; set; } = [];

    public DateTimeOffset? StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    public string TimeZone { get; set; } = "UTC";

    public string TaskList { get; set; } = "Tasks";

    public DateOnly? DueDate { get; set; }

    public string ExactDescription { get; set; } = string.Empty;

    public string TypedConfirmationPhrase { get; set; } = string.Empty;

    public bool IsDestructive
        => Kind is
            WorkActionKind.CancelCalendarEvent or
            WorkActionKind.DeleteMessage or
            WorkActionKind.DeleteTask;
}

public sealed record WorkActionExecutionResult(
    bool Success,
    bool Verified,
    string ResultId,
    string Detail);

public sealed record CalendarItem(
    string Id,
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Location,
    IReadOnlyList<string> Attendees,
    bool IsCancelled);

public sealed record TaskItem(
    string Id,
    string Title,
    DateOnly? DueDate,
    bool IsCompleted,
    string ListName);

public sealed record OutlookFolderInfo(
    string FolderId,
    string FolderName,
    string FolderPath,
    int? ChildFolderCount,
    bool IsSelectable = true)
{
    public bool HasChildren => ChildFolderCount is null or > 0;
}

public sealed record TeamsTeamInfo(
    string TeamId,
    string TeamName);

public sealed record TeamsChannelInfo(
    string TeamId,
    string TeamName,
    string ChannelId,
    string ChannelName);
