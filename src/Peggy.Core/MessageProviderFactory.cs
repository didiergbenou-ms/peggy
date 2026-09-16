namespace Peggy.Core;

public static class MessageProviderFactory
{
    public static bool UsesMockProvider
        => string.Equals(
            GetConfiguredMode(),
            "mock",
            StringComparison.Ordinal);

    public static IMessageProvider Create(
        PeggySettings? settings = null,
        string? account = null,
        string? principalId = null)
    {
        var mode = GetConfiguredMode();
        return mode switch
        {
            null or "" or "live" or "workiq" =>
                new WorkIqMessageProvider(
                    account: account,
                    principalId: principalId,
                    settings: settings),
            "mock" => new MockMessageProvider(settings),
            _ => throw new InvalidOperationException(
                $"Unknown PEGGY_MESSAGE_PROVIDER value '{mode}'. Use 'workiq' or 'mock'.")
        };
    }

    private static string? GetConfiguredMode()
        => Environment.GetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER")
            ?.Trim()
            .ToLowerInvariant();
}
