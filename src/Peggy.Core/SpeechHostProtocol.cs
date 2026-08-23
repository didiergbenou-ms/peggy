namespace Peggy.Core;

public static class SpeechHostCommands
{
    public const string Ping = "ping";
    public const string WarmUp = "warmup";
    public const string Recognize = "recognize";
    public const string Speak = "speak";
    public const string Shutdown = "shutdown";
}

public sealed class SpeechHostRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Command { get; init; } = string.Empty;

    public string? Text { get; init; }

    public int SilenceTimeoutMs { get; init; } = 30000;
}

public sealed class SpeechHostResponse
{
    public string Id { get; init; } = string.Empty;

    public bool Success { get; init; }

    public bool IsPartial { get; init; }

    public string? Text { get; init; }

    public string? Error { get; init; }
}
