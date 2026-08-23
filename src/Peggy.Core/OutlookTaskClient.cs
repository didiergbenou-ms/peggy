using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Peggy.Core;

[SupportedOSPlatform("windows")]
internal sealed class OutlookTaskClient
{
    private const string TaskPrefix = "todo-task:";
    private const int OutlookTaskItem = 3;
    private const int OutlookTasksFolder = 13;
    private const int MapiENotFound = unchecked((int)0x8004010F);
    private readonly string _account;

    public OutlookTaskClient(string account)
    {
        _account = string.IsNullOrWhiteSpace(account)
            ? throw new ArgumentException(
                "A pinned Outlook account is required.",
                nameof(account))
            : account.Trim();
    }

    public Task<WorkActionExecutionResult> ExecuteAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => Execute(request, _account), ct);
    }

    public Task<IReadOnlyList<TaskItem>> ListAsync(
        bool includeCompleted,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(
            () => List(
                includeCompleted,
                Math.Clamp(limit, 1, 100),
                _account),
            ct);
    }

    private static WorkActionExecutionResult Execute(
        WorkActionRequest request,
        string account)
    {
        dynamic? application = null;
        dynamic? session = null;
        dynamic? store = null;
        try
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException(
                    "Classic Outlook is required for Microsoft To Do task actions.");
            application = Activator.CreateInstance(outlookType)
                ?? throw new InvalidOperationException(
                    "Classic Outlook could not be started.");
            session = application.Session;
            store = FindPinnedStore(session, account);

            return request.Kind switch
            {
                WorkActionKind.CreateTask =>
                    CreateTask(store, request),
                WorkActionKind.UpdateTask =>
                    UpdateTask(session, store, request),
                WorkActionKind.CompleteTask =>
                    CompleteTask(session, store, request),
                WorkActionKind.DeleteTask =>
                    DeleteTask(session, store, request),
                _ => throw new InvalidOperationException(
                    $"'{request.Kind}' is not a Microsoft To Do task action.")
            };
        }
        finally
        {
            ReleaseCom(store);
            ReleaseCom(session);
            ReleaseCom(application);
        }
    }

    private static IReadOnlyList<TaskItem> List(
        bool includeCompleted,
        int limit,
        string account)
    {
        dynamic? application = null;
        dynamic? session = null;
        dynamic? store = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException(
                    "Classic Outlook is required for Microsoft To Do task access.");
            application = Activator.CreateInstance(outlookType)
                ?? throw new InvalidOperationException(
                    "Classic Outlook could not be started.");
            session = application.Session;
            store = FindPinnedStore(session, account);
            folder = store.GetDefaultFolder(OutlookTasksFolder);
            items = folder.Items;
            items.Sort("[DueDate]", false);

            var result = new List<TaskItem>();
            var inspected = 0;
            for (var index = 1;
                 index <= (int)items.Count && result.Count < limit && inspected < 500;
                 index++, inspected++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(index);
                    var completed = (bool)item.Complete;
                    if (!includeCompleted && completed)
                    {
                        continue;
                    }

                    var due = (DateTime)item.DueDate;
                    DateOnly? dueDate = due.Year >= 4000
                        ? null
                        : DateOnly.FromDateTime(due);
                    result.Add(new TaskItem(
                        OpaqueIdentifier.Encode(TaskPrefix, (string)item.EntryID),
                        (string)item.Subject,
                        dueDate,
                        completed,
                        "Tasks"));
                }
                finally
                {
                    ReleaseCom(item);
                }
            }

            return result;
        }
        finally
        {
            ReleaseCom(items);
            ReleaseCom(folder);
            ReleaseCom(store);
            ReleaseCom(session);
            ReleaseCom(application);
        }
    }

    private static WorkActionExecutionResult CreateTask(
        dynamic store,
        WorkActionRequest request)
    {
        if (!string.Equals(
                request.TaskList,
                "Tasks",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This WorkIQ version cannot safely target custom Microsoft To Do lists. Use the Tasks list.");
        }

        dynamic? folder = null;
        dynamic? task = null;
        dynamic? verified = null;
        try
        {
            folder = store.GetDefaultFolder(OutlookTasksFolder);
            task = folder.Items.Add(OutlookTaskItem);
            task.Subject = request.Subject;
            task.Body = request.Body;
            if (request.DueDate is not null)
            {
                task.DueDate = request.DueDate.Value.ToDateTime(TimeOnly.MinValue);
            }
            task.Save();

            var entryId = (string)task.EntryID;
            verified = folder.Session.GetItemFromID(entryId, store.StoreID);
            var isVerified =
                string.Equals(
                    (string)verified.Subject,
                    request.Subject,
                    StringComparison.Ordinal) &&
                TaskBodyMatches(
                    (string?)verified.Body,
                    request.Body) &&
                (request.DueDate is null ||
                 DateOnly.FromDateTime((DateTime)verified.DueDate) ==
                 request.DueDate);
            return new WorkActionExecutionResult(
                isVerified,
                isVerified,
                OpaqueIdentifier.Encode(TaskPrefix, entryId),
                isVerified ? "task_created_and_verified" : "task_verification_failed");
        }
        finally
        {
            ReleaseCom(verified);
            ReleaseCom(task);
            ReleaseCom(folder);
        }
    }

    private static WorkActionExecutionResult UpdateTask(
        dynamic session,
        dynamic store,
        WorkActionRequest request)
    {
        var entryId = DecodeTaskId(request.TargetId);
        dynamic? task = null;
        dynamic? verified = null;
        try
        {
            task = session.GetItemFromID(entryId, store.StoreID);
            if (!string.IsNullOrWhiteSpace(request.Subject))
            {
                task.Subject = request.Subject;
            }
            if (!string.IsNullOrWhiteSpace(request.Body))
            {
                task.Body = request.Body;
            }
            if (request.DueDate is not null)
            {
                task.DueDate = request.DueDate.Value.ToDateTime(TimeOnly.MinValue);
            }
            task.Save();

            verified = session.GetItemFromID(entryId, store.StoreID);
            var isVerified =
                (string.IsNullOrWhiteSpace(request.Subject) ||
                 string.Equals(
                     (string)verified.Subject,
                     request.Subject,
                     StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(request.Body) ||
                 TaskBodyMatches(
                     (string?)verified.Body,
                     request.Body)) &&
                (request.DueDate is null ||
                 DateOnly.FromDateTime((DateTime)verified.DueDate) ==
                 request.DueDate);
            return new WorkActionExecutionResult(
                isVerified,
                isVerified,
                request.TargetId,
                isVerified ? "task_updated_and_verified" : "task_verification_failed");
        }
        finally
        {
            ReleaseCom(verified);
            ReleaseCom(task);
        }
    }

    private static WorkActionExecutionResult CompleteTask(
        dynamic session,
        dynamic store,
        WorkActionRequest request)
    {
        var entryId = DecodeTaskId(request.TargetId);
        dynamic? task = null;
        dynamic? verified = null;
        try
        {
            task = session.GetItemFromID(entryId, store.StoreID);
            task.MarkComplete();
            task.Save();
            verified = session.GetItemFromID(entryId, store.StoreID);
            var isVerified = (bool)verified.Complete;
            return new WorkActionExecutionResult(
                isVerified,
                isVerified,
                request.TargetId,
                isVerified ? "task_completed_and_verified" : "task_verification_failed");
        }
        finally
        {
            ReleaseCom(verified);
            ReleaseCom(task);
        }
    }

    private static WorkActionExecutionResult DeleteTask(
        dynamic session,
        dynamic store,
        WorkActionRequest request)
    {
        var entryId = DecodeTaskId(request.TargetId);
        dynamic? task = null;
        try
        {
            task = session.GetItemFromID(entryId, store.StoreID);
            task.Delete();
        }
        finally
        {
            ReleaseCom(task);
        }

        var verified = false;
        dynamic? unexpected = null;
        try
        {
            unexpected = session.GetItemFromID(entryId, store.StoreID);
        }
        catch (COMException ex) when (
            ex.HResult == MapiENotFound)
        {
            verified = true;
        }
        finally
        {
            ReleaseCom(unexpected);
        }

        return new WorkActionExecutionResult(
            verified,
            verified,
            request.TargetId,
            verified ? "task_deleted_and_verified" : "task_verification_failed");
    }

    private static dynamic FindPinnedStore(
        dynamic session,
        string pinnedAccount)
    {
        dynamic? accounts = null;
        try
        {
            accounts = session.Accounts;
            for (var index = 1; index <= (int)accounts.Count; index++)
            {
                dynamic? account = null;
                try
                {
                    account = accounts.Item(index);
                    var address = (string?)account.SmtpAddress;
                    if (string.Equals(
                            address,
                            pinnedAccount,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return account.DeliveryStore;
                    }
                }
                finally
                {
                    ReleaseCom(account);
                }
            }
        }
        finally
        {
            ReleaseCom(accounts);
        }

        throw new InvalidOperationException(
            $"Classic Outlook is not signed in as {pinnedAccount}.");
    }

    private static string DecodeTaskId(string opaqueId)
    {
        if (!OpaqueIdentifier.TryDecode(opaqueId, TaskPrefix, out var entryId))
        {
            throw new InvalidOperationException(
                "The Microsoft To Do task identifier is invalid.");
        }

        return entryId;
    }

    private static bool TaskBodyMatches(
        string? actual,
        string expected)
        => string.Equals(
            NormalizeTaskBody(actual),
            NormalizeTaskBody(expected),
            StringComparison.Ordinal);

    private static string NormalizeTaskBody(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
