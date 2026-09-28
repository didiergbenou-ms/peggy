using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

internal sealed class AlertNotificationRefresher(
    IMessageProvider provider,
    Action<string> log,
    TimeSpan? budget = null)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _budget = budget ?? TimeSpan.FromSeconds(60);
    private readonly Dictionary<(string ThreadId, DateTimeOffset Since), AlertThreadRefreshResult> _snapshots = [];

    public async Task<AlertDecision?> RefreshAsync(
        AlertDecision candidate,
        IReadOnlyCollection<Message> baseline,
        DateTimeOffset sinceUtc,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = _budget - _elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            log("ALERT REFRESH RETAINED reason=budget-exhausted");
            return candidate;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(remaining);
        try
        {
            var key = (candidate.ThreadId, sinceUtc);
            if (!_snapshots.TryGetValue(key, out var refreshed))
            {
                refreshed = await provider.RefreshThreadForAlertAsync(
                    candidate.ThreadId, sinceUtc, timeout.Token);
                _snapshots.Add(key, refreshed);
            }
            timeout.Token.ThrowIfCancellationRequested();
            var thread = refreshed.Thread;
            if (!refreshed.IsComplete || thread is null ||
                !string.Equals(thread.Id, candidate.ThreadId, StringComparison.Ordinal) ||
                thread.Messages.Count == 0 ||
                thread.Messages.Any(message =>
                    string.IsNullOrWhiteSpace(message.Id) ||
                    message.ThreadId != candidate.ThreadId || message.Source != thread.Source) ||
                baseline.Any(message => message.ThreadId == candidate.ThreadId && message.Source != thread.Source) ||
                thread.Messages.Select(message => message.Id)
                    .Distinct(StringComparer.Ordinal).Count() != thread.Messages.Count ||
                candidate.MessageIds.Count == 0 ||
                candidate.MessageIds.Any(id => !thread.Messages.Any(message => message.Id == id)))
            {
                var detail = !refreshed.IsComplete
                    ? refreshed.Warning ?? "provider-incomplete"
                    : "missing-or-mismatched-thread-messages";
                log("ALERT REFRESH RETAINED reason=incomplete-context detail=" +
                    SetupDiagnostics.Protect(detail));
                return candidate;
            }

            var triggers = thread.Messages
                .Where(message => candidate.MessageIds.Contains(message.Id, StringComparer.Ordinal))
                .ToList();
            if (triggers.Any(message => message.IsFromMe || message.ReceivedAt == DateTimeOffset.MinValue))
            {
                log("ALERT REFRESH RETAINED reason=invalid-incoming-anchor");
                return candidate;
            }
            var latestReply = thread.Messages
                .Where(message => message.IsFromMe && message.SentAtUtc.HasValue)
                .Select(message => message.SentAtUtc)
                .Max();
            // Cover every triggering item; liking one message must not hide another request.
            var acknowledged = triggers.All(message =>
                (latestReply.HasValue && latestReply.Value > message.ReceivedAt) ||
                (message.Source == MessageSource.Teams && message.IsLikedByMe));
            if (acknowledged)
            {
                var repliedToAll = latestReply.HasValue &&
                    triggers.All(message => latestReply.Value > message.ReceivedAt);
                log(repliedToAll
                    ? "ALERT REFRESH SUPPRESSED reason=already-replied"
                    : "ALERT REFRESH SUPPRESSED reason=acknowledged-by-like");
                return null;
            }
            log("ALERT REFRESH RETAINED reason=not-acknowledged");
            return candidate;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log("ALERT REFRESH RETAINED reason=timeout");
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
            TimeoutException or UnauthorizedAccessException or HttpRequestException or JsonException)
        {
            ct.ThrowIfCancellationRequested();
            log($"ALERT REFRESH RETAINED reason=read-failure type={ex.GetType().Name}");
            return candidate;
        }
    }
}
