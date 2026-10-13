using System.Globalization;
using System.Net.Mail;

namespace Peggy.Core;

public static class WorkActionPolicy
{
    public static WorkActionRequest NormalizeAndValidate(WorkActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Id = string.IsNullOrWhiteSpace(request.Id)
            ? Guid.NewGuid().ToString("N")
            : request.Id.Trim();
        request.TargetId = request.TargetId?.Trim() ?? string.Empty;
        request.RelatedMessageId = request.RelatedMessageId?.Trim() ?? string.Empty;
        request.Subject = Limit(request.Subject, 300);
        request.Body = Limit(request.Body, 10_000);
        request.TaskList = string.IsNullOrWhiteSpace(request.TaskList)
            ? "Tasks"
            : Limit(request.TaskList, 200);
        request.TimeZone = string.IsNullOrWhiteSpace(request.TimeZone)
            ? "UTC"
            : Limit(request.TimeZone, 100);
        request.Recipients = request.Recipients
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new MailAddress(value.Trim()).Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        ValidateRequiredFields(request);
        request.TypedConfirmationPhrase = request.IsDestructive
            ? BuildTypedConfirmationPhrase(request)
            : string.Empty;
        request.ExactDescription = BuildExactDescription(request);
        return request;
    }

    private static void ValidateRequiredFields(WorkActionRequest request)
    {
        switch (request.Kind)
        {
            case WorkActionKind.MarkMessageRead:
            case WorkActionKind.DeleteMessage:
                Require(request.TargetId, "message target");
                break;
            case WorkActionKind.CreateEmailDraft:
            case WorkActionKind.SendEmail:
                Require(request.Subject, "email subject");
                Require(request.Body, "email body");
                if (request.Recipients.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The proposed email is missing its recipients.");
                }
                break;
            case WorkActionKind.CreateReplyDraft:
            case WorkActionKind.SendReply:
                Require(request.RelatedMessageId, "related message");
                Require(request.Body, "reply body");
                if (request.Recipients.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Outlook reply actions use the original conversation participants; " +
                        "explicit recipients are not supported.");
                }
                break;
            case WorkActionKind.SendChatMessage:
                Require(request.TargetId, "Teams thread");
                Require(request.Body, "Teams message body");
                break;
            case WorkActionKind.CreateCalendarEvent:
                Require(request.Subject, "event subject");
                Require(request.StartUtc, "event start");
                Require(request.EndUtc, "event end");
                if (request.EndUtc <= request.StartUtc)
                {
                    throw new InvalidOperationException(
                        "The calendar event must end after it starts.");
                }
                break;
            case WorkActionKind.UpdateCalendarEvent:
                Require(request.TargetId, "calendar event");
                if (request.StartUtc is null != (request.EndUtc is null))
                {
                    throw new InvalidOperationException(
                        "A calendar time update must include both start and end.");
                }
                if (request.StartUtc is not null &&
                    request.EndUtc <= request.StartUtc)
                {
                    throw new InvalidOperationException(
                        "The calendar event must end after it starts.");
                }
                if (string.IsNullOrWhiteSpace(request.Subject) &&
                    string.IsNullOrWhiteSpace(request.Body) &&
                    request.StartUtc is null &&
                    request.Recipients.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The calendar update does not contain any changes.");
                }
                break;
            case WorkActionKind.CancelCalendarEvent:
                Require(request.TargetId, "calendar event");
                break;
            case WorkActionKind.CreateTask:
                Require(request.Subject, "task title");
                RequireSupportedTaskList(request.TaskList);
                break;
            case WorkActionKind.UpdateTask:
                Require(request.TargetId, "task");
                RequireSupportedTaskList(request.TaskList);
                if (string.IsNullOrWhiteSpace(request.Subject) &&
                    string.IsNullOrWhiteSpace(request.Body) &&
                    request.DueDate is null)
                {
                    throw new InvalidOperationException(
                        "The task update does not contain any changes.");
                }
                break;
            case WorkActionKind.CompleteTask:
            case WorkActionKind.DeleteTask:
                Require(request.TargetId, "task");
                RequireSupportedTaskList(request.TaskList);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported work action '{request.Kind}'.");
        }
    }

    private static string BuildExactDescription(WorkActionRequest request)
    {
        var recipients = request.Recipients.Count == 0
            ? "the existing participants"
            : string.Join(", ", request.Recipients);
        var eventAttendees = request.Recipients.Count == 0
            ? "with no attendees"
            : $"with {string.Join(", ", request.Recipients)}";
        return request.Kind switch
        {
            WorkActionKind.MarkMessageRead =>
                "Mark the selected Outlook message as read.",
            WorkActionKind.CreateEmailDraft =>
                $"Create an Outlook email draft to {recipients} with subject " +
                $"'{request.Subject}' and body: {request.Body}",
            WorkActionKind.SendEmail =>
                $"Send an Outlook email to {recipients} with subject " +
                $"'{request.Subject}' and body: {request.Body}",
            WorkActionKind.CreateReplyDraft =>
                "Create an Outlook draft reply addressed by Outlook to the " +
                "original message's Reply-To or sender. " +
                $"The draft will contain: {request.Body}",
            WorkActionKind.SendReply =>
                "Send this Outlook reply to the original message's Reply-To " +
                $"or sender: {request.Body}",
            WorkActionKind.SendChatMessage =>
                $"Post this Microsoft Teams message to the selected conversation: " +
                request.Body,
            WorkActionKind.CreateCalendarEvent =>
                $"Create the calendar event '{request.Subject}' from " +
                $"{FormatTime(request.StartUtc)} to {FormatTime(request.EndUtc)} " +
                $"{eventAttendees}.{FormatBody(request.Body)} " +
                "Attendees will receive an invitation.",
            WorkActionKind.UpdateCalendarEvent =>
                $"Update the selected calendar event{FormatOptionalSubject(request.Subject)}" +
                $"{FormatOptionalTimeRange(request.StartUtc, request.EndUtc)}" +
                $"{FormatOptionalRecipients(request.Recipients)}.{FormatBody(request.Body)} " +
                "Existing attendees may receive an update.",
            WorkActionKind.CancelCalendarEvent =>
                "Cancel the selected calendar event and notify its attendees." +
                FormatBody(
                    string.IsNullOrWhiteSpace(request.Body)
                        ? "Cancelled by the organizer."
                        : request.Body),
            WorkActionKind.CreateTask =>
                $"Create the Microsoft To Do task '{request.Subject}'" +
                FormatDueDate(request.DueDate) + "." +
                FormatTaskBody(request.Body),
            WorkActionKind.UpdateTask =>
                $"Update the selected Microsoft To Do task" +
                FormatOptionalSubject(request.Subject) +
                FormatDueDate(request.DueDate) + "." +
                FormatTaskBody(request.Body),
            WorkActionKind.CompleteTask =>
                "Mark the selected Microsoft To Do task complete.",
            WorkActionKind.DeleteMessage =>
                "Delete the selected Outlook message. Outlook may retain it in " +
                "Deleted Items according to mailbox policy.",
            WorkActionKind.DeleteTask =>
                "Delete the selected Microsoft To Do task.",
            _ => throw new InvalidOperationException(
                $"Unsupported work action '{request.Kind}'.")
        };
    }

    private static string BuildTypedConfirmationPhrase(WorkActionRequest request)
    {
        var suffixSource = string.IsNullOrWhiteSpace(request.TargetId)
            ? request.Id
            : request.TargetId;
        var suffix = new string(suffixSource
            .Where(char.IsLetterOrDigit)
            .TakeLast(6)
            .ToArray())
            .ToUpperInvariant();
        var action = request.Kind switch
        {
            WorkActionKind.CancelCalendarEvent => "CANCEL EVENT",
            WorkActionKind.DeleteMessage => "DELETE MESSAGE",
            WorkActionKind.DeleteTask => "DELETE TASK",
            _ => "CONFIRM ACTION"
        };
        return string.IsNullOrWhiteSpace(suffix)
            ? action
            : $"{action} {suffix}";
    }

    private static string FormatTime(DateTimeOffset? value)
        => value?.ToLocalTime().ToString(
            "dddd, MMMM d 'at' h:mm tt",
            CultureInfo.CurrentCulture) ?? "(unchanged)";

    private static string FormatDueDate(DateOnly? value)
        => value is null
            ? string.Empty
            : $" due {value.Value.ToString("D", CultureInfo.CurrentCulture)}";

    private static string FormatBody(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $" The exact message or description is: {value}";

    private static string FormatTaskBody(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $" Replace its notes with: {value}";

    private static string FormatOptionalSubject(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $" title to '{value}'";

    private static string FormatOptionalTimeRange(
        DateTimeOffset? start,
        DateTimeOffset? end)
        => start is null && end is null
            ? string.Empty
            : $" time from {FormatTime(start)} to {FormatTime(end)}";

    private static string FormatOptionalRecipients(IReadOnlyCollection<string> recipients)
        => recipients.Count == 0
            ? string.Empty
            : $" attendees to {string.Join(", ", recipients)}";

    private static string Limit(string? value, int maximum)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximum)
        {
            throw new InvalidOperationException(
                $"Action text exceeds the {maximum}-character safety limit.");
        }

        return normalized;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The proposed action is missing its {field}.");
        }
    }

    private static void Require<T>(T? value, string field)
        where T : struct
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                $"The proposed action is missing its {field}.");
        }
    }

    private static void RequireSupportedTaskList(string taskList)
    {
        if (!string.Equals(
                taskList,
                "Tasks",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The assistant can currently change tasks only in the pinned account's " +
                "default Tasks list.");
        }
    }
}
