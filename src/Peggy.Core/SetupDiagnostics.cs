using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Peggy.Core;

public sealed class SetupDiagnostics(Action<string> write, string version)
{
    private readonly object _gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly AsyncLocal<int?> _operation = new();
    private int _nextOperation;
    private const string SecretNames =
        @"authorization|cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"token|client[_-]?secret|secret|credential|password|api[_-]?key|device[_-]?code|user[_-]?code|code";
    private static readonly Regex Secrets = new(
        """(?ix)(?<![\w-])(["']?(?:""" + SecretNames +
        """)["']?\s*[:=]\s*|--(?:""" + SecretNames +
        """)\s+)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\s,;]+)""");
    private static readonly Regex Bearer = new(@"(?i)\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_.=-]+");
    private static readonly Regex GitHubToken = new(@"(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,})\b");
    private static readonly Regex DeviceCode = new(@"(?i)\b[A-Z0-9]{4}-[A-Z0-9]{4}\b");
    private static readonly Regex Email = new(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b");
    private static readonly Regex Url = new("""(?i)https?://[^\s"'<>]+""");
    private static readonly Regex UserPath = new(@"(?i)\b[A-Z]:\\Users\\[^\\\r\n]+");
    private static readonly Regex GuidValue = new(@"(?i)\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b");

    public void Event(string stage, string outcome, string? detail = null)
        => Write(stage, outcome, detail, null, null, true);

    public void Failure(string stage, Exception exception, string? detail = null,
        bool includeExceptionMessages = true)
        => Write(stage, "failed", detail, exception, null, includeExceptionMessages);

    public T Run<T>(string stage, Func<T> action, bool includeExceptionMessages = true)
    {
        var previous = _operation.Value;
        _operation.Value = Interlocked.Increment(ref _nextOperation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            Event(stage, "started");
            var result = action();
            Write(stage, "completed", null, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            return result;
        }
        catch (Exception ex)
        {
            Write(stage, ex is OperationCanceledException ? "cancelled" : "failed", null, ex,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, includeExceptionMessages);
            throw;
        }
        finally { _operation.Value = previous; }
    }

    public void Run(string stage, Action action, bool includeExceptionMessages = true)
        => Run(stage, () => { action(); return true; }, includeExceptionMessages);

    public async Task<T> RunAsync<T>(string stage, Func<Task<T>> action,
        bool includeExceptionMessages = true)
    {
        var previous = _operation.Value;
        _operation.Value = Interlocked.Increment(ref _nextOperation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            Event(stage, "started");
            var result = await action();
            Write(stage, "completed", null, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds, true);
            return result;
        }
        catch (Exception ex)
        {
            Write(stage, ex is OperationCanceledException ? "cancelled" : "failed", null, ex,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, includeExceptionMessages);
            throw;
        }
        finally { _operation.Value = previous; }
    }

    public Task RunAsync(string stage, Func<Task> action, bool includeExceptionMessages = true)
        => RunAsync(stage, async () => { await action(); return true; }, includeExceptionMessages);

    public static string Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        // Redact before truncating so a cut-off secret cannot escape a pattern.
        var text = Bearer.Replace(value, "[authorization-redacted]");
        text = Secrets.Replace(text, "$1[redacted]");
        text = GitHubToken.Replace(text, "[github-token-redacted]");
        text = GuidValue.Replace(text, "[guid]");
        text = DeviceCode.Replace(text, "[device-code-redacted]");
        text = Email.Replace(text, "[email]");
        text = Url.Replace(text, "[url]");
        text = UserPath.Replace(text, "%USERPROFILE%");
        return MonitoringFailureInspector.Sanitize(text);
    }

    private void Write(string stage, string outcome, string? detail, Exception? exception,
        double? elapsedMs, bool includeMessages)
    {
        var errors = new List<object>();
        var pending = new Queue<Exception>();
        if (exception is not null) pending.Enqueue(exception);
        while (errors.Count < 8 && pending.TryDequeue(out var current))
        {
            var request = current as WorkIqClient.WorkIqRequestException;
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames()
                .Take(8).Select(frame => frame.GetMethod())
                .Where(method => method is not null)
                .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
                .ToArray();
            errors.Add(new
            {
                type = current.GetType().FullName,
                hresult = $"0x{current.HResult:X8}",
                message = includeMessages ? Protect(current.Message) : "[authentication-detail-omitted]",
                stack = frames,
                statusCode = request?.StatusCode,
                retryAfterSeconds = request?.RetryAfter?.TotalSeconds,
                requestId = Protect(request?.RequestId),
                operation = Protect(request?.Operation ?? current.Data["Peggy.WorkIqOperation"] as string)
            });
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            }
            else if (current.InnerException is not null) pending.Enqueue(current.InnerException);
        }
        var line = "PEGGY_SETUP " + JsonSerializer.Serialize(new
        {
            schema = 1,
            utc = DateTimeOffset.UtcNow,
            session = _session,
            pid = Environment.ProcessId,
            version,
            operationId = _operation.Value,
            stage = Protect(stage),
            outcome,
            elapsedMs = elapsedMs is null ? (double?)null : Math.Round(elapsedMs.Value),
            detail = Protect(detail),
            errors,
            errorsTruncated = pending.Count > 0
        });
        lock (_gate) write(line);
    }
}
