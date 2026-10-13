using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Peggy.Core;

internal sealed class WorkIqTaskClient
{
    private const string TaskPrefix = "todo-task:";
    private const string DefaultListName = "Tasks";
    private const string DefaultListKind = "defaultList";
    private const int PageSize = 100;
    private const int MaximumPages = 10;

    private readonly WorkIqClient _client;

    public WorkIqTaskClient(WorkIqClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<TaskItem>> ListAsync(
        bool includeCompleted,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var list = await GetDefaultListAsync(ct);
        var tasks = await FetchAllPagesAsync<TodoTask>(
            BuildTasksUrl(list.Id!, "?$top=100&$select=id,title,status,dueDateTime"),
            "Microsoft To Do tasks",
            ct);

        return tasks
            .Where(task =>
                includeCompleted ||
                !string.Equals(
                    task.Status,
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
            .Select(task => ToTaskItem(task, list.Id!))
            .OrderBy(task => task.DueDate)
            .Take(limit)
            .ToList();
    }

    public async Task<WorkActionExecutionResult> ExecuteAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        switch (request.Kind)
        {
            case WorkActionKind.CreateTask:
                return await CreateAsync(request, ct);
            case WorkActionKind.UpdateTask:
                return await UpdateAsync(request, ct);
            case WorkActionKind.CompleteTask:
                return await CompleteAsync(request, ct);
            case WorkActionKind.DeleteTask:
                return await DeleteAsync(request, ct);
            default:
                throw new InvalidOperationException(
                    $"'{request.Kind}' is not a Microsoft To Do task action.");
        }
    }

    internal static string EncodeTaskId(string listId, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return OpaqueIdentifier.Encode(
            TaskPrefix,
            JsonSerializer.Serialize(new TaskTarget(listId, taskId)));
    }

    private async Task<WorkActionExecutionResult> CreateAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        var list = await GetDefaultListAsync(ct);
        var created = await _client.CreateAsync<TodoTask>(
            BuildTasksUrl(list.Id!, string.Empty),
            BuildCreateBody(request),
            ct);
        if (string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidDataException(
                "WorkIQ created a Microsoft To Do task without returning its ID.");
        }

        var targetId = EncodeTaskId(list.Id!, created.Id);
        var verified = await GetTaskAsync(list.Id!, created.Id, ct);
        var success = TaskMatchesRequest(verified, request, includeTitle: true);
        return new WorkActionExecutionResult(
            success,
            success,
            targetId,
            success ? "task_created_and_verified" : "task_verification_failed");
    }

    private async Task<WorkActionExecutionResult> UpdateAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        var (list, taskId) = await ResolveTargetAsync(request.TargetId, ct);
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Subject))
        {
            body["title"] = request.Subject;
        }
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            body["body"] = BuildTaskBody(request.Body);
        }
        if (request.DueDate is not null)
        {
            body["dueDateTime"] = BuildDueDate(request.DueDate.Value);
        }

        await _client.UpdateAsync(
            BuildTaskUrl(list.Id!, taskId),
            body,
            ct);
        var verified = await GetTaskAsync(list.Id!, taskId, ct);
        var success =
            (string.IsNullOrWhiteSpace(request.Subject) ||
             string.Equals(verified.Title, request.Subject, StringComparison.Ordinal)) &&
            (string.IsNullOrWhiteSpace(request.Body) ||
             TaskBodyMatches(verified.Body?.Content, request.Body)) &&
            (request.DueDate is null ||
             ParseDueDate(verified.DueDateTime) == request.DueDate);
        return new WorkActionExecutionResult(
            success,
            success,
            request.TargetId,
            success ? "task_updated_and_verified" : "task_verification_failed");
    }

    private async Task<WorkActionExecutionResult> CompleteAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        var (list, taskId) = await ResolveTargetAsync(request.TargetId, ct);
        await _client.UpdateAsync(
            BuildTaskUrl(list.Id!, taskId),
            new { status = "completed" },
            ct);
        var verified = await GetTaskAsync(list.Id!, taskId, ct);
        var success = string.Equals(
            verified.Status,
            "completed",
            StringComparison.OrdinalIgnoreCase);
        return new WorkActionExecutionResult(
            success,
            success,
            request.TargetId,
            success ? "task_completed_and_verified" : "task_verification_failed");
    }

    private async Task<WorkActionExecutionResult> DeleteAsync(
        WorkActionRequest request,
        CancellationToken ct)
    {
        var (list, taskId) = await ResolveTargetAsync(request.TargetId, ct);
        var taskUrl = BuildTaskUrl(list.Id!, taskId);
        try
        {
            await _client.DeleteAsync(taskUrl, ct);
        }
        catch (WorkIqClient.WorkIqRequestException ex) when (ex.StatusCode == 404)
        {
            return new WorkActionExecutionResult(
                true,
                true,
                request.TargetId,
                "task_deleted_and_verified");
        }

        var verified = false;
        try
        {
            await GetTaskAsync(list.Id!, taskId, ct);
        }
        catch (WorkIqClient.WorkIqRequestException ex) when (ex.StatusCode == 404)
        {
            verified = true;
        }

        return new WorkActionExecutionResult(
            verified,
            verified,
            request.TargetId,
            verified ? "task_deleted_and_verified" : "task_verification_failed");
    }

    private async Task<(TodoTaskList List, string TaskId)> ResolveTargetAsync(
        string targetId,
        CancellationToken ct)
    {
        var (listId, taskId) = DecodeTaskId(targetId);
        var list = await GetDefaultListAsync(ct);
        if (!string.Equals(list.Id, listId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Microsoft To Do task is outside the default Tasks list.");
        }

        return (list, taskId);
    }

    private async Task<TodoTaskList> GetDefaultListAsync(CancellationToken ct)
    {
        var lists = await FetchAllPagesAsync<TodoTaskList>(
            $"/me/todo/lists?$top={PageSize}&$select=id,displayName,wellknownListName",
            "Microsoft To Do task lists",
            ct);
        var matches = lists
            .Where(list => string.Equals(
                list.WellKnownListName,
                DefaultListKind,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1 ||
            string.IsNullOrWhiteSpace(matches[0].Id))
        {
            throw new InvalidDataException(
                "WorkIQ did not return exactly one default Microsoft To Do Tasks list.");
        }

        return matches[0];
    }

    private async Task<TodoTask> GetTaskAsync(
        string listId,
        string taskId,
        CancellationToken ct)
    {
        var task = await _client.FetchAsync<TodoTask>(
            BuildTaskUrl(
                listId,
                taskId) + "?$select=id,title,status,body,dueDateTime",
            ct);
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            throw new InvalidDataException(
                "WorkIQ returned a Microsoft To Do task without its ID.");
        }

        return task;
    }

    private async Task<IReadOnlyList<T>> FetchAllPagesAsync<T>(
        string initialUrl,
        string description,
        CancellationToken ct)
    {
        var results = new List<T>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var expectedPath = GetResourcePath(initialUrl);
        string? url = initialUrl;

        for (var pageNumber = 0;
             url is not null && pageNumber < MaximumPages;
             pageNumber++)
        {
            if (!visited.Add(url))
            {
                throw new InvalidDataException(
                    $"WorkIQ repeated a page while reading {description}.");
            }

            var page = await _client.FetchAsync<GraphCollection<T>>(url, ct);
            results.AddRange(page.Value);
            if (page.NextLink is null)
            {
                return results;
            }

            url = NormalizeContinuation(page.NextLink, expectedPath);
        }

        if (url is not null)
        {
            throw new InvalidDataException(
                $"WorkIQ exceeded the page limit while reading {description}.");
        }

        return results;
    }

    private static string NormalizeContinuation(
        string nextLink,
        string expectedPath)
    {
        if (!nextLink.StartsWith("/", StringComparison.Ordinal) &&
            (!Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute) ||
             absolute.Scheme != Uri.UriSchemeHttps ||
             !string.Equals(
                 absolute.Host,
                 "graph.microsoft.com",
                 StringComparison.OrdinalIgnoreCase) ||
             !absolute.IsDefaultPort ||
             absolute.Fragment.Length > 0 ||
             absolute.UserInfo.Length > 0))
        {
            throw new InvalidDataException(
                "WorkIQ returned an invalid Microsoft To Do continuation URL.");
        }

        var normalized = WorkIqMessageProvider.NormalizeGraphUrl(nextLink);
        if (normalized is null ||
            !string.Equals(
                GetResourcePath(normalized),
                expectedPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "WorkIQ returned a continuation for another Microsoft To Do resource.");
        }

        return normalized;
    }

    private static string GetResourcePath(string url)
        => url.Split('?', 2)[0];

    private static object BuildCreateBody(WorkActionRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = request.Subject,
            ["body"] = BuildTaskBody(request.Body)
        };
        if (request.DueDate is not null)
        {
            body["dueDateTime"] = BuildDueDate(request.DueDate.Value);
        }

        return body;
    }

    private static object BuildTaskBody(string content)
        => new { content, contentType = "text" };

    private static object BuildDueDate(DateOnly dueDate)
        => new
        {
            dateTime = dueDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture) + "T00:00:00.0000000",
            timeZone = "UTC"
        };

    private static DateOnly? ParseDueDate(TodoDateTimeTimeZone? value)
    {
        if (string.IsNullOrWhiteSpace(value?.DateTime) ||
            value.DateTime.Length < 10)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value.DateTime.AsSpan(0, 10),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dueDate)
            ? dueDate
            : null;
    }

    private static TaskItem ToTaskItem(TodoTask task, string listId)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            throw new InvalidDataException(
                "WorkIQ returned a Microsoft To Do task without its ID.");
        }

        return new TaskItem(
            EncodeTaskId(listId, task.Id),
            task.Title ?? string.Empty,
            ParseDueDate(task.DueDateTime),
            string.Equals(
                task.Status,
                "completed",
                StringComparison.OrdinalIgnoreCase),
            DefaultListName);
    }

    private static bool TaskMatchesRequest(
        TodoTask task,
        WorkActionRequest request,
        bool includeTitle)
        => (!includeTitle ||
            string.Equals(task.Title, request.Subject, StringComparison.Ordinal)) &&
           TaskBodyMatches(task.Body?.Content, request.Body) &&
           (request.DueDate is null ||
            ParseDueDate(task.DueDateTime) == request.DueDate);

    private static bool TaskBodyMatches(string? actual, string expected)
        => string.Equals(
            NormalizeTaskBody(actual),
            NormalizeTaskBody(expected),
            StringComparison.Ordinal);

    private static string NormalizeTaskBody(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string BuildTasksUrl(string listId, string suffix)
        => $"/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks{suffix}";

    private static string BuildTaskUrl(string listId, string taskId)
        => $"{BuildTasksUrl(listId, string.Empty)}/{Uri.EscapeDataString(taskId)}";

    private static (string ListId, string TaskId) DecodeTaskId(string targetId)
    {
        if (!OpaqueIdentifier.TryDecode(targetId, TaskPrefix, out var payload))
        {
            throw new InvalidOperationException(
                "The Microsoft To Do task identifier is invalid.");
        }

        try
        {
            var target = JsonSerializer.Deserialize<TaskTarget>(payload);
            if (!string.IsNullOrWhiteSpace(target?.ListId) &&
                !string.IsNullOrWhiteSpace(target.TaskId))
            {
                return (target.ListId, target.TaskId);
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "The Microsoft To Do task identifier is invalid.");
        }

        throw new InvalidOperationException(
            "The Microsoft To Do task identifier is invalid.");
    }

    private sealed record TaskTarget(string ListId, string TaskId);

    private sealed class GraphCollection<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed class TodoTaskList
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("wellknownListName")]
        public string? WellKnownListName { get; init; }
    }

    private sealed class TodoTask
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("body")]
        public TodoTaskBody? Body { get; init; }

        [JsonPropertyName("dueDateTime")]
        public TodoDateTimeTimeZone? DueDateTime { get; init; }
    }

    private sealed class TodoTaskBody
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class TodoDateTimeTimeZone
    {
        [JsonPropertyName("dateTime")]
        public string? DateTime { get; init; }
    }
}
