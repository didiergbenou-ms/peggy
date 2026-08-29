using System.Collections.Concurrent;

namespace Peggy.Core;

/// <summary>In-memory sample data so the slice runs with zero external dependencies.</summary>
public sealed class MockMessageProvider : IMessageProvider
{
    private readonly ConcurrentDictionary<string, Message> _messages = new();
    private readonly ConcurrentDictionary<string, CalendarItem> _events = new();
    private readonly ConcurrentDictionary<string, TaskItem> _tasks = new();
    private readonly ConcurrentDictionary<string, WorkActionRequest> _executedActions =
        new();
    private readonly List<MessageThread> _threads;

    public int ListJoinedTeamsCallCount { get; private set; }

    public int ListTeamChannelsCallCount { get; private set; }

    public MockMessageProvider()
    {
        var now = DateTimeOffset.Now;

        var seed = new List<Message>
        {
            new Message("m-1001", MessageSource.Email, "t-100", "Aisha Khan",
                "Q3 budget review - numbers by Thursday",
                "Hi, could you send your team's Q3 figures before the review on Thursday? Thanks!",
                now.AddMinutes(-2), false)
            {
                WebUrl =
                    "https://outlook.office.com/mail/deeplink/read/mock-1001"
            },
            new Message("m-1002", MessageSource.Email, "t-101", "IT Service Desk",
                "Action required: your password expires in 3 days",
                "Your corporate password expires soon. Reset it to avoid losing access.",
                now.AddMinutes(-52), false)
            {
                WebUrl =
                    "https://outlook.office.com/mail/deeplink/read/mock-1002"
            },
            new Message("m-2001", MessageSource.Teams, "t-200", "Marco Bianchi",
                "Peggy mascot mocks",
                "Hey! When you get a sec, can you review the Peggy mascot mocks I dropped in the channel?",
                now.AddMinutes(-6), false)
            {
                WebUrl =
                    "https://teams.microsoft.com/l/message/mock-chat/m-2001"
            },
            new Message("m-2002", MessageSource.Teams, "t-201", "Standup Bot",
                "Daily standup in 15 minutes",
                "Reminder: the team standup starts in 15 minutes in the Engineering channel.",
                now.AddMinutes(-3), false)
            {
                WebUrl =
                    "https://teams.microsoft.com/l/message/mock-chat/m-2002"
            },
        };
        foreach (var m in seed) _messages[m.Id] = m;

        var eventItem = new CalendarItem(
            OpaqueIdentifier.Encode("calendar-event:", "mock-event"),
            "Mock planning review",
            now.AddHours(2),
            now.AddHours(3),
            "Microsoft Teams",
            ["aisha@example.com"],
            false);
        _events[eventItem.Id] = eventItem;
        var taskItem = new TaskItem(
            OpaqueIdentifier.Encode("todo-task:", "mock-task"),
            "Review Peggy alerts",
            DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
            false,
            "Tasks");
        _tasks[taskItem.Id] = taskItem;

        _threads = new List<MessageThread>
        {
            new("t-100", MessageSource.Email, "Q3 budget review - numbers by Thursday", new List<Message>
            {
                new("m-1000", MessageSource.Email, "t-100", "Aisha Khan",
                    "Q3 budget review - numbers by Thursday",
                    "Scheduling the Q3 review for Thursday at 2pm.",
                    now.AddHours(-26), true),
                _messages["m-1001"],
            }),
            new("t-101", MessageSource.Email, "Action required: your password expires in 3 days",
                new List<Message> { _messages["m-1002"] }),
            new("t-200", MessageSource.Teams, "Peggy mascot mocks",
                new List<Message> { _messages["m-2001"] }),
            new("t-201", MessageSource.Teams, "Daily standup in 15 minutes",
                new List<Message> { _messages["m-2002"] }),
        };
    }

    public Task<IReadOnlyList<Message>> ListUnreadAsync(MessageSource? source, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IEnumerable<Message> q = _messages.Values.Where(m => !m.IsRead);
        if (source is not null) q = q.Where(m => m.Source == source);
        var result = q.OrderByDescending(m => m.ReceivedAt)
                      .Take(Math.Clamp(limit, 1, 100))
                      .ToList();
        return Task.FromResult<IReadOnlyList<Message>>(result);
    }

    public Task<MessageChangeBatch> GetChangesAsync(
        MonitoringCursorState cursor,
        int limit,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var updated = CloneCursor(cursor);
        updated.InitialScanStartedUtc ??=
            nowUtc.AddMinutes(-PeggyDefaults.StartupLookbackMinutes);
        var cutoff = updated.TeamsWatermarkUtc
            ?? updated.InitialScanStartedUtc.Value;
        var boundaryIds = updated.TeamsBoundaryMessageIds.ToHashSet(
            StringComparer.Ordinal);
        var candidates = _messages.Values
            .Where(message =>
                message.ReceivedAt > cutoff ||
                (message.ReceivedAt == cutoff &&
                 !boundaryIds.Contains(message.Id)))
            .Where(message => updated.Initialized || !message.IsRead)
            .OrderBy(message => message.ReceivedAt)
            .ThenBy(message => message.Id, StringComparer.Ordinal)
            .ToList();
        var selected = candidates
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
        var hasMore = candidates.Count > selected.Count;
        updated.Initialized = true;
        updated.InitialOutlookScanComplete = !hasMore;
        if (hasMore)
        {
            var boundary = selected[^1].ReceivedAt;
            var emittedAtBoundary = selected
                .Where(message => message.ReceivedAt == boundary)
                .Select(message => message.Id);
            updated.TeamsBoundaryMessageIds = boundary == cutoff
                ? boundaryIds
                    .Concat(emittedAtBoundary)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : emittedAtBoundary.ToList();
            updated.TeamsWatermarkUtc = boundary;
        }
        else
        {
            updated.TeamsWatermarkUtc = nowUtc.AddMinutes(-1);
            updated.TeamsBoundaryMessageIds.Clear();
        }
        return Task.FromResult(
            new MessageChangeBatch(selected, updated, hasMore));
    }

    public Task<MessageThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_threads.FirstOrDefault(t => t.Id == threadId));
    }

    public Task<IReadOnlyList<CalendarItem>> ListUpcomingEventsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (endUtc <= startUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endUtc));
        }

        return Task.FromResult<IReadOnlyList<CalendarItem>>(
            _events.Values
                .Where(item =>
                    item.Start >= startUtc &&
                    item.Start < endUtc)
                .OrderBy(item => item.Start)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList());
    }

    public Task<IReadOnlyList<TaskItem>> ListTasksAsync(
        bool includeCompleted,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TaskItem>>(
            _tasks.Values
                .Where(task => includeCompleted || !task.IsCompleted)
                .OrderBy(task => task.DueDate)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList());
    }

    public Task<IReadOnlyList<TeamsTeamInfo>> ListJoinedTeamsAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListJoinedTeamsCallCount++;
        return Task.FromResult<IReadOnlyList<TeamsTeamInfo>>(
        [
            new TeamsTeamInfo("mock-team", "Engineering"),
            new TeamsTeamInfo("mock-operations", "Operations")
        ]);
    }

    public Task<IReadOnlyList<TeamsChannelInfo>> ListTeamChannelsAsync(
        string teamId,
        string teamName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ListTeamChannelsCallCount++;
        if (!string.Equals(
                teamId,
                "mock-team",
                StringComparison.Ordinal))
        {
            return Task.FromResult<IReadOnlyList<TeamsChannelInfo>>([]);
        }

        return Task.FromResult<IReadOnlyList<TeamsChannelInfo>>(
        [
            new TeamsChannelInfo(
                teamId,
                teamName,
                "mock-channel",
                "General"),
            new TeamsChannelInfo(
                teamId,
                teamName,
                "mock-incidents",
                "Incidents")
        ]);
    }

    public Task<string> AskWorkAsync(
        string question,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "A WorkIQ question is required.",
                nameof(question));
        }

        return Task.FromResult(
            "Mock WorkIQ answer grounded in Outlook and Teams.");
    }

    public Task<MessageContextResolution?> ResolveContextAsync(
        MessageSource source,
        string summary,
        string reason,
        DateTimeOffset activityUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var webUrl = _messages.Values
            .Where(message => message.Source == source)
            .OrderByDescending(message => message.ReceivedAt)
            .Select(message => message.WebUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        return Task.FromResult<MessageContextResolution?>(
            webUrl is null
                ? null
                : new MessageContextResolution(
                    webUrl,
                    "Mock source context."));
    }

    public Task<OperationResult> MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_messages.TryGetValue(messageId, out var m))
        {
            _messages[messageId] = m with { IsRead = true };
            return Task.FromResult(new OperationResult(true, $"Message {messageId} marked as read."));
        }
        return Task.FromResult(new OperationResult(false, $"Message {messageId} not found."));
    }

    public Task<DraftResult> DraftReplyAsync(string messageId, string body, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var status = _messages.ContainsKey(messageId) ? "draft_created" : "message_not_found";
        var draftId = "draft-" + Guid.NewGuid().ToString("N")[..8];
        return Task.FromResult(new DraftResult(draftId, messageId, body, status));
    }

    public async Task<WorkActionExecutionResult> ExecuteActionAsync(
        WorkActionRequest request,
        CancellationToken ct = default)
    {
        request = WorkActionPolicy.NormalizeAndValidate(request);
        switch (request.Kind)
        {
            case WorkActionKind.MarkMessageRead:
            {
                var result = await MarkReadAsync(request.TargetId, ct);
                return new WorkActionExecutionResult(
                    result.Success,
                    result.Success &&
                    _messages.TryGetValue(request.TargetId, out var message) &&
                    message.IsRead,
                    request.TargetId,
                    result.Detail);
            }
            case WorkActionKind.CreateReplyDraft:
            {
                var result = await DraftReplyAsync(
                    request.RelatedMessageId,
                    request.Body,
                    ct);
                return new WorkActionExecutionResult(
                    result.Status == "draft_created",
                    result.Status == "draft_created",
                    result.DraftId,
                    result.Status);
            }
            case WorkActionKind.SendReply:
                return StoreAction(
                    request,
                    _messages.ContainsKey(request.RelatedMessageId),
                    "reply_sent_and_verified");
            case WorkActionKind.CreateEmailDraft:
                return StoreAction(
                    request,
                    success: true,
                    "email_draft_created_and_verified");
            case WorkActionKind.SendEmail:
                return StoreAction(
                    request,
                    success: true,
                    "email_sent_and_verified");
            case WorkActionKind.SendChatMessage:
                return StoreAction(
                    request,
                    success: true,
                    "teams_message_sent_and_verified");
            case WorkActionKind.CreateCalendarEvent:
            {
                var id = OpaqueIdentifier.Encode(
                    "calendar-event:",
                    Guid.NewGuid().ToString("N"));
                var item = new CalendarItem(
                    id,
                    request.Subject,
                    request.StartUtc!.Value,
                    request.EndUtc!.Value,
                    string.Empty,
                    request.Recipients,
                    false);
                _events[id] = item;
                _executedActions[id] = request;
                return Verified(id, "event_created_and_verified");
            }
            case WorkActionKind.UpdateCalendarEvent:
            {
                if (!_events.TryGetValue(
                        request.TargetId,
                        out var existing))
                {
                    return Failed("event_not_found");
                }
                _events[request.TargetId] = existing with
                {
                    Subject = string.IsNullOrWhiteSpace(request.Subject)
                        ? existing.Subject
                        : request.Subject,
                    Start = request.StartUtc ?? existing.Start,
                    End = request.EndUtc ?? existing.End,
                    Attendees = request.Recipients.Count == 0
                        ? existing.Attendees
                        : request.Recipients
                };
                return StoreAction(
                    request,
                    success: true,
                    "event_updated_and_verified");
            }
            case WorkActionKind.CancelCalendarEvent:
            {
                if (!_events.TryGetValue(
                        request.TargetId,
                        out var existing))
                {
                    return Failed("event_not_found");
                }
                _events[request.TargetId] = existing with
                {
                    IsCancelled = true
                };
                return StoreAction(
                    request,
                    success: true,
                    "event_cancelled_and_verified");
            }
            case WorkActionKind.CreateTask:
            {
                var id = OpaqueIdentifier.Encode(
                    "todo-task:",
                    Guid.NewGuid().ToString("N"));
                _tasks[id] = new TaskItem(
                    id,
                    request.Subject,
                    request.DueDate,
                    false,
                    "Tasks");
                _executedActions[id] = request;
                return Verified(id, "task_created_and_verified");
            }
            case WorkActionKind.UpdateTask:
            {
                if (!_tasks.TryGetValue(
                        request.TargetId,
                        out var existing))
                {
                    return Failed("task_not_found");
                }
                _tasks[request.TargetId] = existing with
                {
                    Title = string.IsNullOrWhiteSpace(request.Subject)
                        ? existing.Title
                        : request.Subject,
                    DueDate = request.DueDate ?? existing.DueDate
                };
                return StoreAction(
                    request,
                    success: true,
                    "task_updated_and_verified");
            }
            case WorkActionKind.CompleteTask:
            {
                if (!_tasks.TryGetValue(
                        request.TargetId,
                        out var existing))
                {
                    return Failed("task_not_found");
                }
                _tasks[request.TargetId] = existing with
                {
                    IsCompleted = true
                };
                return StoreAction(
                    request,
                    success: true,
                    "task_completed_and_verified");
            }
            case WorkActionKind.DeleteTask:
                return _tasks.TryRemove(request.TargetId, out _)
                    ? StoreAction(
                        request,
                        success: true,
                        "task_deleted_and_verified")
                    : Failed("task_not_found");
            case WorkActionKind.DeleteMessage:
                return _messages.TryRemove(request.TargetId, out _)
                    ? StoreAction(
                        request,
                        success: true,
                        "message_deleted_and_verified")
                    : Failed("message_not_found");
            default:
                return Failed("unsupported_mock_action");
        }
    }

    private WorkActionExecutionResult StoreAction(
        WorkActionRequest request,
        bool success,
        string detail)
    {
        if (!success)
        {
            return Failed(detail);
        }
        var resultId = "mock-" + Guid.NewGuid().ToString("N")[..8];
        _executedActions[resultId] = request;
        return Verified(resultId, detail);
    }

    private static WorkActionExecutionResult Verified(
        string resultId,
        string detail)
        => new(true, true, resultId, detail);

    private static WorkActionExecutionResult Failed(string detail)
        => new(false, false, string.Empty, detail);

    private static MonitoringCursorState CloneCursor(MonitoringCursorState cursor)
        => new()
        {
            Initialized = cursor.Initialized,
            InitialOutlookScanComplete = cursor.InitialOutlookScanComplete,
            InitialScanStartedUtc = cursor.InitialScanStartedUtc,
            TeamsWatermarkUtc = cursor.TeamsWatermarkUtc,
            TeamsBoundaryMessageIds = cursor.TeamsBoundaryMessageIds.ToList(),
            TeamsSearchWindowEndUtc = cursor.TeamsSearchWindowEndUtc,
            TeamsSearchOffset = cursor.TeamsSearchOffset,
            TeamsSearchProcessedHitIds =
                cursor.TeamsSearchProcessedHitIds.ToList(),
            PromptWatermarkUtc = cursor.PromptWatermarkUtc,
            OutlookWatermarkUtc = cursor.OutlookWatermarkUtc,
            OutlookBoundaryMessageIds =
                cursor.OutlookBoundaryMessageIds.ToList(),
            OutlookDeltaLinks = new Dictionary<string, string>(
                cursor.OutlookDeltaLinks,
                StringComparer.Ordinal),
            ChannelWatermarksUtc = new Dictionary<string, DateTimeOffset>(
                cursor.ChannelWatermarksUtc,
                StringComparer.Ordinal),
            ChannelBoundaryMessageIds = cursor.ChannelBoundaryMessageIds.ToDictionary(
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
}
