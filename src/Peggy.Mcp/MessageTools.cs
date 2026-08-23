using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using Peggy.Core;

namespace Peggy.Mcp;

/// <summary>
/// Bounded read tools and proposal-only action tools exposed through the assistant gateway.
/// </summary>
[McpServerToolType]
public static class MessageTools
{
    private static readonly object ActionLock = new();

    [McpServerTool(Name = "list_unread")]
    [Description("List the user's unread Outlook email and unread Microsoft Teams chat messages, newest first.")]
    public static async Task<IReadOnlyList<Message>> ListUnread(
        IMessageProvider provider,
        [Description("Optional source filter: \"email\" or \"teams\". Omit for both.")] string? source = null,
        [Description("Maximum number of messages to return (default 50).")] int limit = 50,
        CancellationToken ct = default)
    {
        MessageSource? filter = source?.Trim().ToLowerInvariant() switch
        {
            "email" or "outlook" or "mail" => MessageSource.Email,
            "teams" or "chat" => MessageSource.Teams,
            _ => null,
        };
        var focusedMessages = LoadFocusedMessages();
        if (focusedMessages is not null)
        {
            IEnumerable<Message> result = focusedMessages;
            if (filter is not null)
            {
                result = result.Where(message => message.Source == filter);
            }

            return result
                .OrderByDescending(message => message.ReceivedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();
        }

        return await provider.ListUnreadAsync(filter, limit, ct);
    }

    [McpServerTool(Name = "get_thread")]
    [Description("Get the full conversation thread for a thread id returned by list_unread.")]
    public static async Task<MessageThread?> GetThread(
        IMessageProvider provider,
        [Description("The opaque thread id returned by list_unread.")] string threadId,
        CancellationToken ct = default)
    {
        var focusedMessages = LoadFocusedMessages();
        if (focusedMessages is not null &&
            !focusedMessages.Any(message => string.Equals(
                message.ThreadId,
                threadId,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The requested thread is outside this focused monitoring batch.");
        }

        return await provider.GetThreadAsync(threadId, ct);
    }

    [McpServerTool(Name = "list_upcoming_events")]
    [Description("List bounded upcoming calendar events so an event can be discussed or selected by opaque ID.")]
    public static async Task<IReadOnlyList<CalendarItem>> ListUpcomingEvents(
        IMessageProvider provider,
        [Description("Number of days to inspect from now, between 1 and 30.")] int days = 7,
        [Description("Maximum events to return, between 1 and 50.")] int limit = 20,
        CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow;
        return await provider.ListUpcomingEventsAsync(
            start,
            start.AddDays(Math.Clamp(days, 1, 30)),
            Math.Clamp(limit, 1, 50),
            ct);
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("List bounded Microsoft To Do tasks from the pinned account's Tasks list.")]
    public static async Task<IReadOnlyList<TaskItem>> ListTasks(
        IMessageProvider provider,
        [Description("Include completed tasks. Defaults to false.")] bool includeCompleted = false,
        [Description("Maximum tasks to return, between 1 and 50.")] int limit = 20,
        CancellationToken ct = default)
        => await provider.ListTasksAsync(
            includeCompleted,
            Math.Clamp(limit, 1, 50),
            ct);

    [McpServerTool(Name = "ask_workiq")]
    [Description(
        "Ask WorkIQ a broad, grounded question across the user's Microsoft 365 " +
        "emails, Teams, calendar, meetings, and files. Use for discovery or " +
        "cross-service questions, then use structured tools when exact IDs are needed.")]
    public static Task<string> AskWorkIq(
        IMessageProvider provider,
        [Description("The concise Microsoft 365 question to answer.")] string question,
        CancellationToken ct = default)
        => provider.AskWorkAsync(question, ct);

    [McpServerTool(Name = "get_turn_context")]
    [Description(
        "Get the private JSON context for this assistant turn. " +
        "Call exactly once when the prompt requires it.")]
    public static string GetTurnContext()
    {
        var path = Environment.GetEnvironmentVariable(
            "PEGGY_CONTEXT_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "The assistant did not provide private context for this turn.");
        }

        try
        {
            var turnContext = File.ReadAllText(path);
            using var document = JsonDocument.Parse(turnContext);
            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The assistant's private turn context was not an object.");
            }
            return turnContext;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            throw new InvalidOperationException(
                "The assistant could not load the private turn context.",
                ex);
        }
    }

    [McpServerTool(Name = "prepare_action")]
    [Description(
        "Prepare exactly one bounded work action for the assistant to preview and request approval. " +
        "This tool never executes the action.")]
    public static WorkActionRequest PrepareAction(
        [Description(
            "Action kind: mark_message_read, create_email_draft, send_email, " +
            "create_reply_draft, send_reply, " +
            "send_chat_message, create_calendar_event, update_calendar_event, " +
            "cancel_calendar_event, create_task, update_task, complete_task, " +
            "delete_message, or delete_task.")] string kind,
        [Description("Opaque target ID returned by another assistant tool.")] string targetId = "",
        [Description("Opaque related email message ID for reply actions.")] string relatedMessageId = "",
        [Description("Subject, event title, or task title.")] string subject = "",
        [Description("Exact message, reply, event detail, cancellation note, or task note.")] string body = "",
        [Description("Recipient email addresses for invitations or email sends.")] string[]? recipients = null,
        [Description("ISO 8601 UTC start time for a calendar event.")] string? startUtc = null,
        [Description("ISO 8601 UTC end time for a calendar event.")] string? endUtc = null,
        [Description("Microsoft To Do list name. Defaults to Tasks.")] string taskList = "Tasks",
        [Description("Task due date as YYYY-MM-DD.")] string? dueDate = null)
    {
        var request = new WorkActionRequest
        {
            Kind = ParseActionKind(kind),
            TargetId = targetId,
            RelatedMessageId = relatedMessageId,
            Subject = subject,
            Body = body,
            Recipients = recipients?.ToList() ?? [],
            StartUtc = ParseOptionalTimestamp(startUtc, "startUtc"),
            EndUtc = ParseOptionalTimestamp(endUtc, "endUtc"),
            TaskList = taskList,
            DueDate = ParseOptionalDate(dueDate)
        };
        request = WorkActionPolicy.NormalizeAndValidate(request);
        WriteActionProposal(request);
        return request;
    }

    private static IReadOnlyList<Message>? LoadFocusedMessages()
    {
        var path = Environment.GetEnvironmentVariable("PEGGY_FOCUS_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Message[]>(
                File.ReadAllText(path))
                ?? throw new InvalidDataException(
                    "The focus file was empty.");
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            throw new InvalidOperationException(
                "The assistant could not load the tray's focused message set.",
                ex);
        }
    }

    private static void WriteActionProposal(WorkActionRequest request)
        {
            var path = Environment.GetEnvironmentVariable("PEGGY_ACTION_FILE");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "The assistant did not open an action-proposal channel for this turn.");
            }

            lock (ActionLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    throw new InvalidOperationException(
                        "This turn already has a pending action proposal.");
                }

                File.WriteAllText(path, JsonSerializer.Serialize(request));
            }
        }

    private static WorkActionKind ParseActionKind(string kind)
            => kind.Trim().ToLowerInvariant().Replace('-', '_') switch
            {
                "mark_message_read" => WorkActionKind.MarkMessageRead,
                "create_email_draft" => WorkActionKind.CreateEmailDraft,
                "send_email" => WorkActionKind.SendEmail,
                "create_reply_draft" => WorkActionKind.CreateReplyDraft,
                "send_reply" => WorkActionKind.SendReply,
                "send_chat_message" => WorkActionKind.SendChatMessage,
                "create_calendar_event" => WorkActionKind.CreateCalendarEvent,
                "update_calendar_event" => WorkActionKind.UpdateCalendarEvent,
                "cancel_calendar_event" => WorkActionKind.CancelCalendarEvent,
                "create_task" => WorkActionKind.CreateTask,
                "update_task" => WorkActionKind.UpdateTask,
                "complete_task" => WorkActionKind.CompleteTask,
                "delete_message" => WorkActionKind.DeleteMessage,
                "delete_task" => WorkActionKind.DeleteTask,
                _ => throw new InvalidOperationException(
                    $"Unknown assistant action kind '{kind}'.")
            };

    private static DateTimeOffset? ParseOptionalTimestamp(
            string? value,
            string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                throw new InvalidOperationException(
                    $"{name} must be an ISO 8601 timestamp.");
            }

            return parsed;
        }

    private static DateOnly? ParseOptionalDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                throw new InvalidOperationException(
                    "dueDate must use YYYY-MM-DD.");
            }

            return parsed;
    }
}
