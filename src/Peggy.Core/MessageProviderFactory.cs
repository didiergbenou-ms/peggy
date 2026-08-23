namespace Peggy.Core;

public static class MessageProviderFactory
{
    public static IMessageProvider Create(
        PeggySettings? settings = null,
        string? account = null)
    {
        var mode = Environment.GetEnvironmentVariable("PEGGY_MESSAGE_PROVIDER")
            ?.Trim()
            .ToLowerInvariant();
        return mode switch
        {
            null or "" or "live" or "workiq" =>
                new WorkIqMessageProvider(
                    account: account,
                    settings: settings),
            "mock" => new MockMessageProvider(),
            _ => throw new InvalidOperationException(
                $"Unknown PEGGY_MESSAGE_PROVIDER value '{mode}'. Use 'workiq' or 'mock'.")
        };
    }
}
