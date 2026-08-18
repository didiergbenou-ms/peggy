using System.ComponentModel;
using ModelContextProtocol.Server;
using Pippa.Core;

namespace Pippa.Mcp;

/// <summary>
/// The tools Pippa exposes to GitHub Copilot over MCP. Copilot can pull the
/// user's unread messages, read a thread, mark messages read, and draft replies.
/// </summary>
[McpServerToolType]
public static class MessageTools
{
    [McpServerTool(Name = "list_unread")]
    [Description("List the user's unread messages across Outlook email and Microsoft Teams, newest first.")]
    public static async Task<IReadOnlyList<Message>> ListUnread(
        IMessageProvider provider,
        [Description("Optional source filter: \"email\" or \"teams\". Omit for both.")] string? source = null,
        [Description("Maximum number of messages to return (default 20).")] int limit = 20,
        CancellationToken ct = default)
    {
        MessageSource? filter = source?.Trim().ToLowerInvariant() switch
        {
            "email" or "outlook" or "mail" => MessageSource.Email,
            "teams" or "chat" => MessageSource.Teams,
            _ => null,
        };
        return await provider.ListUnreadAsync(filter, limit, ct);
    }

    [McpServerTool(Name = "get_thread")]
    [Description("Get the full conversation thread for a thread id returned by list_unread.")]
    public static async Task<MessageThread?> GetThread(
        IMessageProvider provider,
        [Description("The thread id, for example \"t-200\".")] string threadId,
        CancellationToken ct = default)
        => await provider.GetThreadAsync(threadId, ct);

    [McpServerTool(Name = "mark_read")]
    [Description("Mark a message as read by its message id.")]
    public static async Task<OperationResult> MarkRead(
        IMessageProvider provider,
        [Description("The message id, for example \"m-2001\".")] string messageId,
        CancellationToken ct = default)
        => await provider.MarkReadAsync(messageId, ct);

    [McpServerTool(Name = "draft_reply")]
    [Description("Create a draft reply to a message. This does NOT send anything; it leaves a draft for the user to review.")]
    public static async Task<DraftResult> DraftReply(
        IMessageProvider provider,
        [Description("The message id to reply to, for example \"m-1001\".")] string messageId,
        [Description("The reply body text.")] string body,
        CancellationToken ct = default)
        => await provider.DraftReplyAsync(messageId, body, ct);
}
