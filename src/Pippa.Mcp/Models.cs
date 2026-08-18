namespace Pippa.Mcp;

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
    bool IsRead);

/// <summary>A conversation thread and its messages, oldest to newest.</summary>
public record MessageThread(
    string Id,
    MessageSource Source,
    string Subject,
    IReadOnlyList<Message> Messages);

/// <summary>Result of creating a draft reply. The draft is never sent.</summary>
public record DraftResult(
    string DraftId,
    string MessageId,
    string Body,
    string Status);

/// <summary>Generic result for an action tool.</summary>
public record OperationResult(bool Success, string Detail);
