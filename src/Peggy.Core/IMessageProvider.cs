namespace Peggy.Core;

/// <summary>
/// Abstraction over the user's message sources (Outlook + Teams).
/// The live implementation reads Microsoft Graph data through WorkIQ.
/// </summary>
public interface IMessageProvider
{
    Task<IReadOnlyList<Message>> ListUnreadAsync(MessageSource? source, int limit, CancellationToken ct = default);

    Task<MessageChangeBatch> GetChangesAsync(
        MonitoringCursorState cursor,
        int limit,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<MessageThread?> GetThreadAsync(string threadId, CancellationToken ct = default);

    Task<IReadOnlyList<CalendarItem>> ListUpcomingEventsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> ListTasksAsync(
        bool includeCompleted,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<OutlookFolderInfo>> ListOutlookChildFoldersAsync(
        OutlookFolderInfo? parent,
        CancellationToken ct = default);

    Task<IReadOnlyList<TeamsTeamInfo>> ListJoinedTeamsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<TeamsChannelInfo>> ListTeamChannelsAsync(
        string teamId,
        string teamName,
        CancellationToken ct = default);

    Task<string> AskWorkAsync(
        string question,
        CancellationToken ct = default);

    Task<MessageContextResolution?> ResolveContextAsync(
        MessageSource source,
        string summary,
        string reason,
        DateTimeOffset activityUtc,
        CancellationToken ct = default);

    Task<OperationResult> MarkReadAsync(string messageId, CancellationToken ct = default);

    Task<DraftResult> DraftReplyAsync(string messageId, string body, CancellationToken ct = default);

    Task<WorkActionExecutionResult> ExecuteActionAsync(
        WorkActionRequest request,
        CancellationToken ct = default);
}
