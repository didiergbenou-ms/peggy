using System.Collections.Concurrent;

namespace Pippa.Mcp;

/// <summary>In-memory sample data so the slice runs with zero external dependencies.</summary>
public sealed class MockMessageProvider : IMessageProvider
{
    private readonly ConcurrentDictionary<string, Message> _messages = new();
    private readonly List<MessageThread> _threads;

    public MockMessageProvider()
    {
        var now = DateTimeOffset.Now;

        var seed = new List<Message>
        {
            new("m-1001", MessageSource.Email, "t-100", "Aisha Khan",
                "Q3 budget review - numbers by Thursday",
                "Hi, could you send your team's Q3 figures before the review on Thursday? Thanks!",
                now.AddMinutes(-18), false),
            new("m-1002", MessageSource.Email, "t-101", "IT Service Desk",
                "Action required: your password expires in 3 days",
                "Your corporate password expires soon. Reset it to avoid losing access.",
                now.AddMinutes(-52), false),
            new("m-2001", MessageSource.Teams, "t-200", "Marco Bianchi",
                "Pippa mascot mocks",
                "Hey! When you get a sec, can you review the Pippa mascot mocks I dropped in the channel?",
                now.AddMinutes(-6), false),
            new("m-2002", MessageSource.Teams, "t-201", "Standup Bot",
                "Daily standup in 15 minutes",
                "Reminder: the team standup starts in 15 minutes in the Engineering channel.",
                now.AddMinutes(-3), false),
        };
        foreach (var m in seed) _messages[m.Id] = m;

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
            new("t-200", MessageSource.Teams, "Pippa mascot mocks",
                new List<Message> { _messages["m-2001"] }),
            new("t-201", MessageSource.Teams, "Daily standup in 15 minutes",
                new List<Message> { _messages["m-2002"] }),
        };
    }

    public Task<IReadOnlyList<Message>> ListUnreadAsync(MessageSource? source, int limit, CancellationToken ct = default)
    {
        IEnumerable<Message> q = _messages.Values.Where(m => !m.IsRead);
        if (source is not null) q = q.Where(m => m.Source == source);
        var result = q.OrderByDescending(m => m.ReceivedAt)
                      .Take(Math.Clamp(limit, 1, 100))
                      .ToList();
        return Task.FromResult<IReadOnlyList<Message>>(result);
    }

    public Task<MessageThread?> GetThreadAsync(string threadId, CancellationToken ct = default)
        => Task.FromResult(_threads.FirstOrDefault(t => t.Id == threadId));

    public Task<OperationResult> MarkReadAsync(string messageId, CancellationToken ct = default)
    {
        if (_messages.TryGetValue(messageId, out var m))
        {
            _messages[messageId] = m with { IsRead = true };
            return Task.FromResult(new OperationResult(true, $"Message {messageId} marked as read."));
        }
        return Task.FromResult(new OperationResult(false, $"Message {messageId} not found."));
    }

    public Task<DraftResult> DraftReplyAsync(string messageId, string body, CancellationToken ct = default)
    {
        var status = _messages.ContainsKey(messageId) ? "draft_created" : "message_not_found";
        var draftId = "draft-" + Guid.NewGuid().ToString("N")[..8];
        return Task.FromResult(new DraftResult(draftId, messageId, body, status));
    }
}
