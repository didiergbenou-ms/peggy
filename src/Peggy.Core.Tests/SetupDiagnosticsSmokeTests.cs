using System.IO;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Core.Tests;

internal static class SetupDiagnosticsSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        var path = Path.Combine(directory, "synthetic-setup.log");
        var lines = new List<string>();
        var diagnostics = new SetupDiagnostics(line =>
        {
            lines.Add(line);
            File.AppendAllText(path, line + Environment.NewLine);
        }, "0.1.14-beta.test");
        TestAssert.Equal(42, await diagnostics.RunAsync("finish.test", () => Task.FromResult(42)),
            "Tracing changed the operation result.");
        var first = Parse(lines[0]);
        var last = Parse(lines[1]);
        TestAssert.True(first.GetProperty("outcome").GetString() == "started" &&
            last.GetProperty("outcome").GetString() == "completed" &&
            first.GetProperty("operationId").GetInt32() == last.GetProperty("operationId").GetInt32() &&
            last.GetProperty("elapsedMs").GetDouble() >= 0 &&
            first.GetProperty("pid").GetInt32() == Environment.ProcessId &&
            first.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero &&
            first.GetProperty("version").GetString() == "0.1.14-beta.test",
            "Structured diagnostics lost process, version, timing or operation correlation.");

        var original = new InvalidOperationException("Could not save account person@example.test.",
            new IOException(@"Access denied to C:\Users\SyntheticPerson\Peggy\state.dat"));
        original.Data["DoNotLog"] = "PRIVATE-EXCEPTION-DATA";
        var thrown = await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => diagnostics.RunAsync("finish.save-identity", () => ThrowAsync(original)),
            "Diagnostics swallowed a real operation failure.");
        TestAssert.True(ReferenceEquals(original, thrown), "Tracing replaced the original exception.");
        var failed = Parse(lines[^1]);
        TestAssert.True(failed.GetProperty("stage").GetString() == "finish.save-identity" &&
            failed.GetProperty("outcome").GetString() == "failed" &&
            failed.GetProperty("errors").GetArrayLength() == 2 &&
            failed.GetProperty("errors")[0].GetProperty("message").GetString()!.Contains("Could not save account") &&
            failed.GetProperty("errors")[0].GetProperty("hresult").GetString()!.StartsWith("0x") &&
            failed.GetProperty("errors")[0].GetProperty("stack").GetArrayLength() > 0 &&
            !lines[^1].Contains("PRIVATE-EXCEPTION-DATA", StringComparison.Ordinal),
            "Failure diagnostics lost message, exception chain, stack or HRESULT, or dumped arbitrary exception data.");
        diagnostics.Run("finish.save-identity", () => { });
        var retry = Parse(lines[^1]);
        TestAssert.True(retry.GetProperty("operationId").GetInt32() != failed.GetProperty("operationId").GetInt32() &&
            retry.GetProperty("outcome").GetString() == "completed",
            "A retry reused its failed attempt's operation identity.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await TestAssert.ThrowsAsync<OperationCanceledException>(
            () => diagnostics.RunAsync("account.probe-inbox", () => Task.FromCanceled(cancellation.Token)),
            "Tracing swallowed cancellation.");
        TestAssert.Equal("cancelled", Parse(lines[^1]).GetProperty("outcome").GetString(),
            "Cancellation was logged as success.");
        var request = new WorkIqClient.WorkIqRequestException(403, TimeSpan.FromSeconds(5),
            "Forbidden.", "request-correlation");
        request.AttachOperation("fetch /me");
        diagnostics.Failure("finish.verify-account", request);
        var error = Parse(lines[^1]).GetProperty("errors")[0];
        TestAssert.True(error.GetProperty("statusCode").GetInt32() == 403 &&
            error.GetProperty("retryAfterSeconds").GetDouble() == 5 &&
            error.GetProperty("operation").GetString() == "fetch /me",
            "WorkIQ transport context was not preserved.");

        var secrets = new[]
        {
            "Bearer opaque-bearer-value",
            "Authorization: Basic c3ludGhldGljOnNlY3JldA==",
            "\"access_token\":\"access-secret\"",
            "'refresh_token': 'refresh-secret'",
            "id_token=id-secret",
            "token=generic-secret",
            "--password \"command-secret\"",
            "--access-token \"cli-access-secret\"",
            """password="escaped\"credential-suffix" """,
            "client_secret=client-secret-value",
            "api-key=api-secret",
            "cookie=session-secret",
            "user_code=human-code-secret",
            "device_code=device-secret",
            "A1B2-C3D4",
            "ghp_abcdefghijklmnopqrstuvwxyz1234567890",
            "github_pat_abcdefghijklmnopqrstuvwxyz1234567890",
            "eyJhbGciOiJub25lIn0.eyJ1c2VyIjoiZm9vIn0.c2lnbmF0dXJl",
            "person@example.test",
            @"C:\Users\SyntheticPerson\Peggy\state.dat",
            "https://login.example.test/?code=private-code",
            "01234567-89ab-cdef-0123-456789abcdef"
        };
        var allSecrets = string.Join("\n", secrets);
        diagnostics.Failure("redaction", new IOException(allSecrets));
        var persisted = File.ReadAllText(path);
        foreach (var secret in new[]
        {
            "opaque-bearer-value", "c3ludGhldGljOnNlY3JldA", "access-secret", "refresh-secret",
            "id-secret", "generic-secret", "command-secret", "cli-access-secret", "credential-suffix",
            "client-secret-value", "api-secret",
            "session-secret", "human-code-secret", "device-secret", "A1B2-C3D4", "ghp_abcdef",
            "github_pat_abcdef", "eyJhbGci", "person@example.test", "SyntheticPerson",
            "login.example.test", "private-code", "01234567-89ab-cdef-0123-456789abcdef"
        })
        {
            TestAssert.True(!persisted.Contains(secret, StringComparison.Ordinal),
                $"Setup logging failed to redact a synthetic secret: {secret}");
        }
        TestAssert.True(SetupDiagnostics.Protect("exitCode=1603; errorCode=403; status=0x80070005")
                .Contains("exitCode=1603", StringComparison.Ordinal),
            "Credential redaction removed diagnostic exit codes.");

        await TestAssert.ThrowsAsync<IOException>(() =>
            diagnostics.RunAsync("copilot.device-sign-in",
                () => ThrowAsync(new IOException("UNSTRUCTURED-AUTH-SECRET", new Exception("INNER-AUTH-SECRET"))),
                includeExceptionMessages: false), "Authentication failure was swallowed.");
        TestAssert.True(!lines[^1].Contains("UNSTRUCTURED-AUTH-SECRET") &&
            !lines[^1].Contains("INNER-AUTH-SECRET") &&
            lines[^1].Contains("authentication-detail-omitted"),
            "Sensitive authentication exception text escaped its explicit exclusion.");

        diagnostics.Failure("aggregate", new AggregateException(
            Enumerable.Range(0, 12).Select(index => new IOException($"Failure {index}"))));
        var bounded = Parse(lines[^1]);
        TestAssert.True(bounded.GetProperty("errors").GetArrayLength() == 8 &&
            bounded.GetProperty("errorsTruncated").GetBoolean(),
            "Aggregate diagnostics were unbounded or truncation was hidden.");
        var otherLines = new List<string>();
        new SetupDiagnostics(otherLines.Add, "0.1.14-beta.test").Event("session", "opened");
        TestAssert.True(Parse(otherLines[0]).GetProperty("session").GetString() !=
            first.GetProperty("session").GetString(),
            "Separate installer instances were not distinguishable.");
        TestAssert.True(File.ReadAllLines(path).All(line => Parse(line).GetProperty("schema").GetInt32() == 1),
            "Persisted diagnostics could not be read after the failure.");
    }

    private static async Task ThrowAsync(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }

    internal static JsonElement Parse(string line)
    {
        using var document = JsonDocument.Parse(line["PEGGY_SETUP ".Length..]);
        return document.RootElement.Clone();
    }
}
