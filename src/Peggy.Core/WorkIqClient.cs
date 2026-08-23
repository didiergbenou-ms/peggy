using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Peggy.Core;

internal sealed record WorkIqPromptReference(
    string TargetLink,
    bool IsCited);

internal sealed record WorkIqPromptResult(
    string Answer,
    IReadOnlyList<WorkIqPromptReference> References);

internal sealed class WorkIqClient
{
    private const int MaxTransientAttempts = 5;
    private const int MaxTransportAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _executablePath;
    private readonly string? _account;

    public WorkIqClient(string? executablePath = null, string? account = null)
    {
        _executablePath = executablePath is null
            ? ResolveExecutable()
            : ResolveConfiguredPath(executablePath);
        _account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
    }

    public async Task<T> FetchAsync<T>(string url, CancellationToken ct)
        => DeserializeRequired<T>(
            await RunCommandAsync(
                "fetch",
                "--urls",
                url,
                null,
                retryTransientFailures: true,
                ct));

    public async Task<T> CallFunctionAsync<T>(
        string url,
        CancellationToken ct)
        => DeserializeRequired<T>(
            await RunCommandAsync(
                "call-function",
                "--url",
                url,
                null,
                retryTransientFailures: true,
                ct));

    public async Task<string> AskAsync(
        string question,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "A WorkIQ question is required.",
                nameof(question));
        }

        try
        {
            using var response = await RunMcpToolAsync(
                "ask",
                new Dictionary<string, object?>
                {
                    ["question"] = question
                },
                TimeSpan.FromSeconds(120),
                ct);
            return ExtractMcpAnswer(response.RootElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is TimeoutException or
            InvalidOperationException or
            InvalidDataException or
            IOException)
        {
            throw new WorkIqPromptException(
                "WorkIQ prompt retrieval failed.",
                ex);
        }
    }

    public async Task<WorkIqPromptResult> AskWithReferencesAsync(
        string question,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "A WorkIQ question is required.",
                nameof(question));
        }

        try
        {
            var payload = await RunCommandAsync(
                "ask",
                "--question",
                question,
                null,
                retryTransientFailures: true,
                ct);
            var response = DeserializeRequired<WorkIqCliAskResponse>(
                payload);
            if (response.IsError ||
                string.IsNullOrWhiteSpace(response.Response))
            {
                throw new InvalidDataException(
                    "WorkIQ returned no grounded answer.");
            }

            return new WorkIqPromptResult(
                response.Response.Trim(),
                response.References
                    .Where(reference =>
                        !string.IsNullOrWhiteSpace(
                            reference.TargetLink))
                    .Select(reference => new WorkIqPromptReference(
                        reference.TargetLink!.Trim(),
                        reference.IsCitedInResponse))
                    .ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is TimeoutException or
            InvalidOperationException or
            InvalidDataException or
            IOException)
        {
            throw new WorkIqPromptException(
                "WorkIQ context resolution failed.",
                ex);
        }
    }

    public async Task<T> CreateAsync<T>(
        string url,
        object body,
        CancellationToken ct)
        => DeserializeRequired<T>(
            await RunMcpMutationAsync(
                "create_entity",
                "parentUrl",
                url,
                body,
                ct));

    public async Task<T> UpdateAsync<T>(
        string url,
        object body,
        CancellationToken ct)
        => DeserializeRequired<T>(
            await RunMcpMutationAsync(
                "update_entity",
                "entityUrl",
                url,
                body,
                ct));

    public async Task UpdateAsync(
        string url,
        object body,
        CancellationToken ct)
        => await RunMcpMutationAsync(
            "update_entity",
            "entityUrl",
            url,
            body,
            ct);

    public async Task<T> DoActionAsync<T>(
        string url,
        object? body,
        CancellationToken ct)
        => DeserializeRequired<T>(
            await RunMcpMutationAsync(
                "do_action",
                "actionUrl",
                url,
                body,
                ct));

    public async Task DoActionAsync(
        string url,
        object? body,
        CancellationToken ct)
        => await RunMcpMutationAsync(
            "do_action",
            "actionUrl",
            url,
            body,
            ct);

    public async Task DeleteAsync(string url, CancellationToken ct)
        => await RunMcpMutationAsync(
            "delete_entity",
            "entityUrl",
            url,
            null,
            ct);

    private async Task<string> RunMcpMutationAsync(
        string toolName,
        string urlParameter,
        string url,
        object? body,
        CancellationToken ct)
    {
        var arguments = new Dictionary<string, object?>
        {
            [urlParameter] = url
        };
        if (body is not null)
        {
            arguments["jsonBody"] =
                JsonSerializer.SerializeToElement(
                    body,
                    JsonOptions);
        }
        else if (string.Equals(
                     toolName,
                     "do_action",
                     StringComparison.Ordinal))
        {
            arguments["jsonBody"] = null;
        }
        using var response = await RunMcpToolAsync(
            toolName,
            arguments,
            TimeSpan.FromSeconds(45),
            ct);
        return ExtractMcpPayload(response.RootElement);
    }

    private async Task<JsonDocument> RunMcpToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan timeoutDuration,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add("Error");
        if (_account is not null)
        {
            startInfo.ArgumentList.Add("--account");
            startInfo.ArgumentList.Add(_account);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start WorkIQ MCP at '{_executablePath}'.",
                ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            ct);
        timeout.CancelAfter(timeoutDuration);
        try
        {
            await WriteMcpMessageAsync(
                process,
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { },
                        clientInfo = new
                        {
                            name = "peggy",
                            version = "1.0"
                        }
                    }
                });
            _ = await ReadMcpResponseAsync(
                process,
                expectedId: 1,
                timeout.Token);
            await WriteMcpMessageAsync(
                process,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { }
                });

            await WriteMcpMessageAsync(
                process,
                new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new
                    {
                        name = toolName,
                        arguments
                    }
                });
            using var response = await ReadMcpResponseAsync(
                process,
                expectedId: 2,
                timeout.Token);
            return JsonDocument.Parse(
                response.RootElement.GetRawText());
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"WorkIQ MCP tool '{toolName}' did not respond within " +
                $"{timeoutDuration.TotalSeconds:0} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
            TryKill(process);
            _ = await stderrTask;
        }
    }

    private static async Task WriteMcpMessageAsync(
        Process process,
        object message)
    {
        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(message, JsonOptions));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadMcpResponseAsync(
        Process process,
        int expectedId,
        CancellationToken ct)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct)
                ?? throw new InvalidDataException(
                    "WorkIQ MCP closed before returning a response.");
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "WorkIQ MCP returned an invalid JSON-RPC message.",
                    ex);
            }

            if (document.RootElement.TryGetProperty(
                    "id",
                    out var id) &&
                id.TryGetInt32(out var parsedId) &&
                parsedId == expectedId)
            {
                return document;
            }
            document.Dispose();
        }
    }

    private static string ExtractMcpPayload(JsonElement root)
    {
        if (root.TryGetProperty("error", out var rpcError))
        {
            throw new InvalidOperationException(
                $"WorkIQ MCP request failed: {FindErrorMessage(rpcError)}");
        }
        if (!root.TryGetProperty("result", out var result))
        {
            throw new InvalidDataException(
                "WorkIQ MCP returned no result.");
        }
        if (!result.TryGetProperty(
                "structuredContent",
                out var structured) ||
            !structured.TryGetProperty(
                "results",
                out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                "WorkIQ MCP returned an unsupported result shape.");
        }

        var first = results[0];
        var statusCode =
            first.TryGetProperty("statusCode", out var status) &&
            status.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : 0;
        if (statusCode >= 400 ||
            first.TryGetProperty("error", out _))
        {
            var message = FindErrorMessage(first);
            throw new WorkIqRequestException(
                statusCode,
                FindRetryAfter(first),
                message.Length == 0
                    ? $"WorkIQ request failed with status {statusCode}."
                    : $"WorkIQ request failed with status {statusCode}: {message}");
        }
        if (!first.TryGetProperty("data", out var data) ||
            data.ValueKind is JsonValueKind.Null or
                JsonValueKind.Undefined)
        {
            return "{}";
        }

        return data.GetRawText();
    }

    private static string ExtractMcpAnswer(JsonElement root)
    {
        if (root.TryGetProperty("error", out var rpcError))
        {
            throw new InvalidOperationException(
                $"WorkIQ MCP request failed: {FindErrorMessage(rpcError)}");
        }
        if (!root.TryGetProperty("result", out var result))
        {
            throw new InvalidDataException(
                "WorkIQ MCP returned no result.");
        }
        if (result.TryGetProperty("isError", out var isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                "WorkIQ reported that the prompt could not be completed.");
        }
        if (result.TryGetProperty(
                "structuredContent",
                out var structured) &&
            structured.TryGetProperty("answer", out var answer) &&
            answer.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(answer.GetString()))
        {
            return answer.GetString()!.Trim();
        }
        if (result.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) &&
                    string.Equals(
                        type.GetString(),
                        "text",
                        StringComparison.OrdinalIgnoreCase) &&
                    item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    return text.GetString()!.Trim();
                }
            }
        }

        throw new InvalidDataException(
            "WorkIQ MCP returned no answer text.");
    }

    private async Task<string> RunCommandAsync(
        string command,
        string urlOption,
        string url,
        object? body,
        bool retryTransientFailures,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunCommandOnceAsync(
                    command,
                    urlOption,
                    url,
                    body,
                    ct);
            }
            catch (WorkIqRequestException ex) when (
                retryTransientFailures &&
                IsTransientStatus(ex.StatusCode) &&
                attempt < MaxTransientAttempts)
            {
                var delay = ex.RetryAfter ??
                    TimeSpan.FromSeconds(
                        (ex.StatusCode == 429 ? 5 : 2) *
                        Math.Pow(2, attempt - 1));
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60)),
                    ct);
            }
            catch (TimeoutException) when (
                retryTransientFailures &&
                attempt < MaxTransportAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    ct);
            }
            catch (WorkIqTransportException) when (
                retryTransientFailures &&
                attempt < MaxTransportAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    ct);
            }
        }
    }

    private static bool IsTransientStatus(int statusCode)
        => statusCode is 429 or 502 or 503 or 504;

    private async Task<string> RunCommandOnceAsync(
        string command,
        string urlOption,
        string url,
        object? body,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(urlOption);
        startInfo.ArgumentList.Add(url);
        if (body is not null)
        {
            startInfo.ArgumentList.Add("--body");
            startInfo.ArgumentList.Add(JsonSerializer.Serialize(body, JsonOptions));
        }
        if (string.Equals(
                command,
                "ask",
                StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--json");
        }
        startInfo.ArgumentList.Add("--log-level");
        startInfo.ArgumentList.Add("Error");
        if (_account is not null)
        {
            startInfo.ArgumentList.Add("--account");
            startInfo.ArgumentList.Add(_account);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start WorkIQ command '{command}' at '{_executablePath}'.",
                ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(
            string.Equals(
                command,
                "ask",
                StringComparison.Ordinal)
                ? TimeSpan.FromSeconds(120)
                : TimeSpan.FromSeconds(45));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw new TimeoutException(
                $"WorkIQ command '{command}' did not respond within 45 seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        TryThrowStructuredError(stdout);
        TryThrowStructuredError(stderr);

        if (process.ExitCode != 0)
        {
            var detail = NormalizeError(
                !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout);
            var message = detail.Length == 0
                ? $"WorkIQ exited with code {process.ExitCode}."
                : $"WorkIQ exited with code {process.ExitCode}: {detail}";
            if (IsTransientProcessError(detail))
            {
                throw new WorkIqTransportException(message);
            }

            throw new InvalidOperationException(message);
        }

        var payload = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }
        if (payload.Contains(
                "must accept the End User License",
                StringComparison.OrdinalIgnoreCase) ||
            payload.Contains(
                "workiq accept-eula",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WorkIQ's pre-release terms have not been accepted. " +
                "Run 'workiq accept-eula' and review " +
                "https://github.com/microsoft/work-iq/tree/main/EULA.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "WorkIQ returned an unexpected non-JSON response.", ex);
        }

        using (document)
        {
            ThrowIfWorkIqError(document.RootElement);
        }

        return payload;
    }

    private static T DeserializeRequired<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidDataException("WorkIQ returned an empty response.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new InvalidDataException("WorkIQ returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "WorkIQ returned an unsupported JSON response.", ex);
        }
    }

    private static void TryThrowStructuredError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            ThrowIfWorkIqError(document.RootElement);
        }
        catch (JsonException)
        {
            // WorkIQ can write ordinary diagnostics to stderr. Only JSON error
            // envelopes are interpreted here.
        }
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("PEGGY_WORKIQ_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ResolveConfiguredPath(configured);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "workiq.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue with known install locations.
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var scoutExecutable = Path.Combine(
            localAppData,
            "Programs",
            "Clawpilot",
            "Microsoft Scout",
            "resources",
            "app.asar.unpacked",
            "node_modules",
            "@microsoft",
            "workiq",
            "bin",
            "win-x64",
            "workiq.exe");
        if (File.Exists(scoutExecutable))
        {
            return scoutExecutable;
        }

        var wrapper = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".scout",
            "bin",
            "workiq.cmd");
        var wrappedExecutable = TryResolveFromWrapper(wrapper);
        if (wrappedExecutable is not null)
        {
            return wrappedExecutable;
        }

        throw new FileNotFoundException(
            "WorkIQ was not found. Install or sign in to Microsoft Scout, or set PEGGY_WORKIQ_PATH.");
    }

    private static string ResolveConfiguredPath(string configuredPath)
    {
        var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (File.Exists(path) &&
            !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(path);
        }

        var wrappedExecutable = TryResolveFromWrapper(path);
        if (wrappedExecutable is not null)
        {
            return wrappedExecutable;
        }

        throw new FileNotFoundException(
            $"The configured WorkIQ executable was not found or is not runnable: '{path}'.");
    }

    private static string? TryResolveFromWrapper(string wrapperPath)
    {
        if (!File.Exists(wrapperPath))
        {
            return null;
        }

        var contents = File.ReadAllText(wrapperPath);
        var matches = Regex.Matches(
            contents,
            "\"(?<path>[^\"]*workiq\\.exe)\"",
            RegexOptions.IgnoreCase);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var candidate = Environment.ExpandEnvironmentVariables(
                matches[index].Groups["path"].Value);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static void ThrowIfWorkIqError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var result in results.EnumerateArray())
        {
            var statusCode = result.TryGetProperty("statusCode", out var status) &&
                             status.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : 0;
            if (statusCode < 400 && !result.TryGetProperty("error", out _))
            {
                continue;
            }

            var message = FindErrorMessage(result);
            throw new WorkIqRequestException(
                statusCode,
                FindRetryAfter(result),
                message.Length == 0
                    ? $"WorkIQ request failed with status {statusCode}."
                    : $"WorkIQ request failed with status {statusCode}: {message}");
        }
    }

    private static TimeSpan? FindRetryAfter(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        "retry-after",
                        StringComparison.OrdinalIgnoreCase) &&
                    TryReadSeconds(property.Value, out var seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                var nested = FindRetryAfter(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindRetryAfter(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryReadSeconds(JsonElement element, out double seconds)
    {
        seconds = 0;
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out seconds))
        {
            return seconds > 0;
        }

        return element.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   element.GetString(),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out seconds) &&
               seconds > 0;
    }

    private static string FindErrorMessage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (element.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? string.Empty;
        }

        if (element.TryGetProperty("error", out var error))
        {
            return FindErrorMessage(error);
        }

        return string.Empty;
    }

    private static string NormalizeError(string stderr)
    {
        var detail = Regex.Replace(stderr, @"\s+", " ").Trim();
        return detail.Length <= 300 ? detail : detail[..300] + "...";
    }

    private static bool IsTransientProcessError(string detail)
        => new[]
        {
            "ERR_NETWORK_CHANGED",
            "No such host is known",
            "HttpRequestException",
            "NameResolutionError",
            "connection reset",
            "network is unreachable",
            "temporarily unavailable"
        }.Any(value => detail.Contains(
            value,
            StringComparison.OrdinalIgnoreCase));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
    }

    private sealed class WorkIqCliAskResponse
    {
        public bool IsError { get; init; }

        public string? Response { get; init; }

        public List<WorkIqCliReference> References { get; init; } = [];
    }

    private sealed class WorkIqCliReference
    {
        public string? TargetLink { get; init; }

        public bool IsCitedInResponse { get; init; }
    }

    internal sealed class WorkIqRequestException : InvalidOperationException
    {
        public WorkIqRequestException(
            int statusCode,
            TimeSpan? retryAfter,
            string message)
            : base(message)
        {
            StatusCode = statusCode;
            RetryAfter = retryAfter;
        }

        public int StatusCode { get; }

        public TimeSpan? RetryAfter { get; }
    }

    internal sealed class WorkIqPromptException : InvalidOperationException
    {
        public WorkIqPromptException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class WorkIqTransportException : InvalidOperationException
    {
        public WorkIqTransportException(string message)
            : base(message)
        {
        }
    }
}
