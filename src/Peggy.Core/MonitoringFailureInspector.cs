using System.Text.RegularExpressions;

namespace Peggy.Core;

public static class MonitoringFailureInspector
{
    private const int MaximumFieldLength = 2_000;
    private static readonly Regex RequestIdRegex = new(
        @"(?i)\brequest\s*id\s*[:=]\s*(?<id>[A-Za-z0-9:._-]+)",
        RegexOptions.Compiled);
    private static readonly Regex AuthorizationRegex = new(
        @"(?im)(authorization\s*:\s*)[^\r\n]+",
        RegexOptions.Compiled);
    private static readonly Regex SecretRegex = new(
        @"(?i)(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|" +
        @"client[_-]?secret|password|api[_-]?key|cookie)\b\s*[:=]\s*)" +
        @"(""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled);
    private static readonly Regex JwtRegex = new(
        @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]+\." +
        @"[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.Compiled);

    public static MonitoringFailureDetails Create(
        Exception exception,
        string stage,
        string code,
        DateTimeOffset occurredUtc)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = new List<string>();
        int? statusCode = null;
        double? retryAfterSeconds = null;
        var operation = string.Empty;
        var requestId = string.Empty;
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            var message = Sanitize(current.Message);
            chain.Add($"{current.GetType().Name}: {message}");
            if (current is WorkIqClient.WorkIqRequestException request)
            {
                statusCode ??= request.StatusCode > 0
                    ? request.StatusCode
                    : null;
                retryAfterSeconds ??= request.RetryAfter?.TotalSeconds;
                if (operation.Length == 0)
                {
                    operation = Sanitize(request.Operation);
                }
                if (requestId.Length == 0)
                {
                    requestId = Sanitize(request.RequestId);
                }
            }
            if (operation.Length == 0 &&
                current.Data["Peggy.WorkIqOperation"] is string
                {
                    Length: > 0
                } taggedOperation)
            {
                operation = Sanitize(taggedOperation);
            }
            if (requestId.Length == 0)
            {
                var match = RequestIdRegex.Match(message);
                if (match.Success)
                {
                    requestId = match.Groups["id"].Value;
                }
            }
        }

        return new MonitoringFailureDetails
        {
            OccurredUtc = occurredUtc,
            Stage = Sanitize(stage),
            Code = Sanitize(code),
            ExceptionType = exception.GetType().Name,
            Message = Sanitize(exception.Message),
            StatusCode = statusCode,
            RetryAfterSeconds = retryAfterSeconds,
            Operation = operation,
            RequestId = requestId,
            ExceptionChain = chain.Take(8).ToList()
        };
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var sanitized = AuthorizationRegex.Replace(
            value,
            "${1}[redacted]");
        sanitized = SecretRegex.Replace(sanitized, "${1}[redacted]");
        sanitized = JwtRegex.Replace(sanitized, "[jwt-redacted]");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        return sanitized.Length <= MaximumFieldLength
            ? sanitized
            : sanitized[..MaximumFieldLength] + "...";
    }
}
