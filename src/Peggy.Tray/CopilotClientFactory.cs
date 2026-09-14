using System;
using GitHub.Copilot;

namespace Peggy.Tray;

internal static class CopilotClientFactory
{
    public static CopilotClient Create(string copilotPath)
        => new(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(path: copilotPath),
            WorkingDirectory = AppContext.BaseDirectory,
            LogLevel = CopilotLogLevel.Error,
            EnableRemoteSessions = false
        });
}
