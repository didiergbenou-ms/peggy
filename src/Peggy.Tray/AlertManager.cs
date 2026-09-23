using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Peggy.Core;

namespace Peggy.Tray;

public sealed record AlertApplyResult(
    IReadOnlyList<AlertRecord> MaterialUpdates,
    IReadOnlyList<AlertRecord> ActiveAlerts);

public sealed class AlertManager
{
    private readonly PeggyStateManager _state;

    public AlertManager(PeggyStateManager state)
    {
        _state = state;
    }

    public AlertApplyResult Apply(
        IReadOnlyCollection<AlertDecision> decisions,
        IReadOnlyCollection<Message> messages,
        DateTimeOffset nowUtc,
        DateTimeOffset? observedAtUtc = null)
    {
        ClearPreviousDays(nowUtc);
        var material = new List<AlertRecord>();
        var messageById = messages.ToDictionary(
            message => message.Id,
            StringComparer.Ordinal);
        foreach (var decision in decisions
                     .GroupBy(item => item.ThreadId, StringComparer.Ordinal)
                     .Select(group => group
                         .OrderBy(item => item.Tier)
                         .First()))
        {
            var tier = ApplyExplicitFeedback(
                decision.ThreadId,
                decision.Tier);
            var decisionMessages = decision.MessageIds
                .Where(messageById.ContainsKey)
                .Select(id => messageById[id])
                .ToList();
            if (decisionMessages.Count == 0)
            {
                decisionMessages = messages
                    .Where(message => string.Equals(
                        message.ThreadId,
                        decision.ThreadId,
                        StringComparison.Ordinal))
                    .ToList();
            }
            if (decisionMessages.Count == 0)
            {
                throw new InvalidOperationException(
                    "An alert decision did not map to a monitored message.");
            }

            var newest = decisionMessages.Max(message => message.ReceivedAt);
            var observed = observedAtUtc ?? nowUtc;
            // Discard yesterday's in-flight result, not older messages first detected today.
            if (AlertDayPolicy.IsPreviousDay(Max(newest, observed), nowUtc))
            {
                continue;
            }
            var source = decisionMessages[0].Source;
            var sourceReference = AlertSourceLink.SelectReference(
                decision.SourceReference,
                decisionMessages);
            var allMessageIds = decisionMessages
                .Select(message => message.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var materialHash = ComputeMaterialHash(
                tier,
                decision.Summary,
                decision.Reason,
                allMessageIds);

            var existing = _state.State.Alerts
                .Where(alert =>
                    alert.Status is AlertStatus.Active or AlertStatus.Snoozed &&
                    string.Equals(
                        alert.ThreadId,
                        decision.ThreadId,
                        StringComparison.Ordinal))
                .OrderByDescending(alert => alert.UpdatedUtc)
                .FirstOrDefault();
            if (existing is not null &&
                !AlertSourceLink.TryCreate(sourceReference, out _) &&
                AlertSourceLink.TryCreate(
                    existing.SourceReference,
                    out var existingSourceUri))
            {
                sourceReference = existingSourceUri.AbsoluteUri;
            }
            if (!AlertSourceLink.TryCreate(sourceReference, out _))
            {
                throw new InvalidDataException(
                    "The assistant refused to create an alert without a verified " +
                    "Outlook or Teams source link.");
            }
            if (existing is null)
            {
                existing = new AlertRecord
                {
                    ThreadId = decision.ThreadId,
                    Source = source,
                    Tier = tier,
                    Summary = decision.Summary,
                    Reason = decision.Reason,
                    SourceReference = sourceReference,
                    MessageIds = allMessageIds,
                    FirstSeenUtc = nowUtc,
                    LastActivityUtc = newest,
                    UpdatedUtc = nowUtc,
                    MaterialHash = materialHash
                };
                _state.State.Alerts.Add(existing);
                material.Add(existing);
                continue;
            }

            var mergedIds = existing.MessageIds
                .Concat(allMessageIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            var mergedHash = ComputeMaterialHash(
                tier,
                decision.Summary,
                decision.Reason,
                mergedIds);
            var changed =
                !string.Equals(
                    existing.MaterialHash,
                    mergedHash,
                    StringComparison.Ordinal) ||
                newest > existing.LastActivityUtc;
            existing.Tier = tier;
            existing.Summary = decision.Summary;
            existing.Reason = decision.Reason;
            existing.SourceReference = sourceReference;
            existing.MessageIds = mergedIds;
            existing.LastActivityUtc = Max(
                existing.LastActivityUtc,
                newest);
            existing.UpdatedUtc = nowUtc;
            existing.MaterialHash = mergedHash;
            if (changed)
            {
                existing.Status = AlertStatus.Active;
                existing.SnoozedUntilUtc = null;
                material.Add(existing);
            }
        }

        return new AlertApplyResult(
            material,
            GetActiveAlerts());
    }

    public IReadOnlyList<AlertRecord> GetActiveAlerts()
        => _state.State.Alerts
            .Where(alert => alert.Status == AlertStatus.Active)
            .OrderBy(alert => alert.Tier)
            .ThenByDescending(alert => alert.LastActivityUtc)
            .ToList();

    public int ClearPreviousDays(DateTimeOffset nowUtc)
        => AlertDayPolicy.ClearPreviousDays(_state.State, nowUtc);

    public int DismissAlertsWithoutVerifiedSources(
        DateTimeOffset nowUtc)
    {
        var unsupported = _state.State.Alerts
            .Where(alert =>
                alert.Status is
                    AlertStatus.Active or
                    AlertStatus.Snoozed &&
                !AlertSourceLink.TryCreate(
                    alert.SourceReference,
                    out _))
            .ToList();
        foreach (var alert in unsupported)
        {
            alert.Status = AlertStatus.Dismissed;
            alert.SnoozedUntilUtc = null;
            alert.UpdatedUtc = nowUtc;
        }
        return unsupported.Count;
    }

    public void Resolve(string alertId, AlertStatus status, DateTimeOffset nowUtc)
    {
        if (status is not (AlertStatus.Resolved or AlertStatus.Dismissed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var alert = FindAlert(alertId);
        alert.Status = status;
        alert.UpdatedUtc = nowUtc;
    }

    public int DismissAllActive(DateTimeOffset nowUtc)
    {
        var active = _state.State.Alerts
            .Where(alert => alert.Status == AlertStatus.Active)
            .ToList();
        foreach (var alert in active)
        {
            alert.Status = AlertStatus.Dismissed;
            alert.SnoozedUntilUtc = null;
            alert.UpdatedUtc = nowUtc;
        }
        return active.Count;
    }

    public void Snooze(
        string alertId,
        TimeSpan duration,
        DateTimeOffset nowUtc)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var alert = FindAlert(alertId);
        alert.Status = AlertStatus.Snoozed;
        alert.SnoozedUntilUtc = nowUtc.Add(duration);
        alert.UpdatedUtc = nowUtc;
    }

    public IReadOnlyList<AlertRecord> ReactivateDue(DateTimeOffset nowUtc)
    {
        ClearPreviousDays(nowUtc);
        var due = GetDueForNotification(nowUtc);
        foreach (var alert in due)
        {
            alert.Status = AlertStatus.Active;
            alert.SnoozedUntilUtc = null;
            alert.UpdatedUtc = nowUtc;
        }

        return due;
    }

    public IReadOnlyList<AlertRecord> GetDueForNotification(DateTimeOffset nowUtc)
        => _state.State.Alerts
            .Where(alert => alert.Status == AlertStatus.Snoozed &&
                alert.SnoozedUntilUtc <= nowUtc &&
                !AlertDayPolicy.IsPreviousDay(alert, nowUtc))
            .OrderBy(alert => alert.Tier)
            .ToList();

    public void CompleteSnoozeRefresh(
        AlertRecord alert, AlertDecision? decision, DateTimeOffset nowUtc)
    {
        if (alert.Status != AlertStatus.Snoozed ||
            alert.SnoozedUntilUtc is not { } until || until > nowUtc ||
            (decision is not null && decision.ThreadId != alert.ThreadId))
        {
            throw new InvalidOperationException("Only a due, unchanged snoozed alert can finish notification review.");
        }
        alert.SnoozedUntilUtc = null;
        alert.UpdatedUtc = nowUtc;
        alert.Status = decision is null ? AlertStatus.Dismissed : AlertStatus.Active;
        if (decision is not null)
        {
            alert.Tier = ApplyExplicitFeedback(alert.ThreadId, decision.Tier);
            alert.Summary = decision.Summary;
            alert.Reason = decision.Reason;
            alert.MaterialHash = ComputeMaterialHash(
                alert.Tier, alert.Summary, alert.Reason, alert.MessageIds);
        }
    }

    public void CorrectTier(
        string alertId,
        AlertTier tier,
        DateTimeOffset nowUtc)
    {
        var alert = FindAlert(alertId);
        alert.Tier = tier;
        alert.UpdatedUtc = nowUtc;
        alert.MaterialHash = ComputeMaterialHash(
            tier,
            alert.Summary,
            alert.Reason,
            alert.MessageIds);
        _state.State.FeedbackRules.Add(new FeedbackRule
        {
            ThreadId = alert.ThreadId,
            Kind = FeedbackKind.CorrectedTier,
            CorrectedTier = tier,
            Summary = alert.Summary,
            CreatedUtc = nowUtc
        });
    }

    public void AddFeedback(
        string alertId,
        FeedbackKind kind,
        DateTimeOffset nowUtc)
    {
        if (kind is not (
                FeedbackKind.LessLikeThis or
                FeedbackKind.AlwaysAlert))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var alert = FindAlert(alertId);
        _state.State.FeedbackRules.Add(new FeedbackRule
        {
            ThreadId = alert.ThreadId,
            Kind = kind,
            Summary = alert.Summary,
            CreatedUtc = nowUtc
        });
        if (kind == FeedbackKind.LessLikeThis)
        {
            alert.Tier = AlertTier.Awareness;
        }
        else if (alert.Tier == AlertTier.Awareness)
        {
            alert.Tier = AlertTier.Attention;
        }
        alert.UpdatedUtc = nowUtc;
        alert.MaterialHash = ComputeMaterialHash(
            alert.Tier,
            alert.Summary,
            alert.Reason,
            alert.MessageIds);
    }

    private AlertTier ApplyExplicitFeedback(
        string threadId,
        AlertTier proposed)
    {
        var rule = _state.State.FeedbackRules
            .Where(item => string.Equals(
                item.ThreadId,
                threadId,
                StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
        return rule?.Kind switch
        {
            FeedbackKind.LessLikeThis => AlertTier.Awareness,
            FeedbackKind.AlwaysAlert when proposed == AlertTier.Awareness =>
                AlertTier.Attention,
            FeedbackKind.CorrectedTier when rule.CorrectedTier is not null =>
                rule.CorrectedTier.Value,
            _ => proposed
        };
    }

    private AlertRecord FindAlert(string alertId)
        => _state.State.Alerts.FirstOrDefault(alert => string.Equals(
            alert.Id,
            alertId,
            StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Alert '{alertId}' no longer exists.");

    private static string ComputeMaterialHash(
        AlertTier tier,
        string summary,
        string reason,
        IEnumerable<string> messageIds)
    {
        var payload = string.Join(
            "\n",
            new[]
            {
                tier.ToString(),
                summary.Trim(),
                reason.Trim(),
                string.Join("|", messageIds)
            });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static DateTimeOffset Max(
        DateTimeOffset left,
        DateTimeOffset right)
        => left >= right ? left : right;
}
