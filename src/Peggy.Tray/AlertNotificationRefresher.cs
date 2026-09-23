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

internal enum NotificationVerdict { Keep, Suppress, Uncertain }

internal sealed record AlertNotificationReview(
    NotificationVerdict Verdict,
    AlertTier Tier,
    string Summary,
    string Reason,
    IReadOnlyList<string> EvidenceMessageIds);

internal sealed record CopilotNotificationReviewResult(
    AlertNotificationReview Review,
    CopilotRunResult Usage);

internal sealed class AlertNotificationRefresher(
    IMessageProvider provider,
    Func<AlertDecision, IReadOnlyList<Message>, CancellationToken,
        Task<CopilotNotificationReviewResult>> review,
    Func<CopilotRunResult, bool, Task> recordUsage,
    Action<string> log,
    TimeSpan? budget = null)
{
    internal const int MaximumContextMessages = 100;
    internal const int MaximumContextCharacters = 64_000;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _budget = budget ?? TimeSpan.FromSeconds(60);

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
        CopilotRunResult? usage = null;
        var rejectedUsage = false;
        try
        {
            var refreshed = await provider.RefreshThreadForAlertAsync(
                candidate.ThreadId, sinceUtc, timeout.Token);
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
                log("ALERT REFRESH RETAINED reason=incomplete-context");
                return candidate;
            }

            var relevant = thread.Messages
                .Where(message => message.ReceivedAt >= sinceUtc ||
                    candidate.MessageIds.Contains(message.Id, StringComparer.Ordinal))
                .OrderBy(message => message.ReceivedAt)
                .ToList();
            if (relevant.Count > MaximumContextMessages)
            {
                log("ALERT REFRESH RETAINED reason=context-limit");
                return candidate;
            }

            var originals = baseline
                .Where(message => message.ThreadId == candidate.ThreadId)
                .ToDictionary(message => message.Id, StringComparer.Ordinal);
            var changed = candidate.MessageIds.Any(id => !originals.ContainsKey(id)) ||
                relevant.Any(message => !originals.TryGetValue(message.Id, out var previous) ||
                    (message.FullText ?? message.Preview) != (previous.FullText ?? previous.Preview) ||
                    message.Subject != previous.Subject ||
                    message.From != previous.From ||
                    message.ReceivedAt != previous.ReceivedAt);
            if (!changed)
            {
                log("ALERT REFRESH UNCHANGED");
                return candidate;
            }

            var context = thread.Messages
                .Where(message => !relevant.Contains(message))
                .OrderByDescending(message => message.ReceivedAt)
                .Take(MaximumContextMessages - relevant.Count)
                .Concat(relevant)
                .OrderBy(message => message.ReceivedAt)
                .ToList();
            if (context.Sum(message => (long)(message.FullText ?? message.Preview).Length +
                    message.Subject.Length + message.From.Length) > MaximumContextCharacters)
            {
                log("ALERT REFRESH RETAINED reason=context-text-limit");
                return candidate;
            }
            var result = await review(candidate, context, timeout.Token);
            usage = result.Usage;
            timeout.Token.ThrowIfCancellationRequested();
            if (result.Review.Verdict == NotificationVerdict.Suppress)
            {
                // Resolution must be supported by fresh evidence, not the original request.
                var latestRequestUtc = relevant
                    .Where(message => candidate.MessageIds.Contains(message.Id, StringComparer.Ordinal))
                    .Max(message => message.ReceivedAt);
                var hasNewEvidence = result.Review.EvidenceMessageIds.Any(id =>
                    relevant.Any(message => message.Id == id &&
                        message.ReceivedAt >= latestRequestUtc &&
                        (originals.TryGetValue(id, out var original)
                            ? message.Preview != original.Preview
                            : !candidate.MessageIds.Contains(id, StringComparer.Ordinal)) &&
                        !string.IsNullOrWhiteSpace(message.Preview)));
                if (!hasNewEvidence)
                {
                    log("ALERT REFRESH RETAINED reason=no-new-resolution-evidence");
                    return candidate;
                }
                log("ALERT REFRESH SUPPRESSED reason=confirmed-no-longer-actionable");
                return null;
            }
            if (result.Review.Verdict == NotificationVerdict.Uncertain)
            {
                log("ALERT REFRESH RETAINED reason=uncertain");
                return candidate;
            }
            log("ALERT REFRESH UPDATED reason=still-actionable");
            return candidate with
            {
                Tier = result.Review.Tier,
                Summary = result.Review.Summary,
                Reason = result.Review.Reason
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log("ALERT REFRESH RETAINED reason=timeout");
            return candidate;
        }
        catch (CopilotClassificationException ex)
        {
            usage = ex.Usage;
            rejectedUsage = true;
            ct.ThrowIfCancellationRequested();
            log("ALERT REFRESH RETAINED reason=invalid-reassessment");
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
            TimeoutException or UnauthorizedAccessException or HttpRequestException or JsonException)
        {
            ct.ThrowIfCancellationRequested();
            log($"ALERT REFRESH RETAINED reason=read-or-review-failure type={ex.GetType().Name}");
            return candidate;
        }
        finally
        {
            if (usage is not null)
            {
                await recordUsage(usage, rejectedUsage);
            }
        }
    }
}
