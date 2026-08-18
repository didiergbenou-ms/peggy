namespace Pippa.Mcp;

/// <summary>
/// Abstraction over the user's message sources (Outlook + Teams).
/// The mock implementation backs the vertical slice; a Microsoft Graph
/// implementation will replace it later without changing the tool surface.
/// </summary>
public interface IMessageProvider
{
    Task<IReadOnlyList<Message>> ListUnreadAsync(MessageSource? source, int limit, CancellationToken ct = default);

    Task<MessageThread?> GetThreadAsync(string threadId, CancellationToken ct = default);

    Task<OperationResult> MarkReadAsync(string messageId, CancellationToken ct = default);

    Task<DraftResult> DraftReplyAsync(string messageId, string body, CancellationToken ct = default);
}
