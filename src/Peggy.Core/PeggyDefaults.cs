namespace Peggy.Core;

public static class PeggyDefaults
{
    public const string WorkIqAccountEnvironmentVariable =
        "PEGGY_WORKIQ_ACCOUNT";
    public const string CopilotModel = "gpt-5.4";
    public const string ActiveCasesFolder = "Inbox/Cases/Active";
    public const int AuditRetentionDays = 30;
    public const int StartupLookbackMinutes = 5;
    public const int MonitoringBatchSize = 20;
    public const int MonitoringFailureThreshold = 3;
    public const int DefaultWakeMinute = 7 * 60;
    public const int DefaultSleepMinute = 19 * 60;

    public static string ConfiguredWorkIqAccount
        => Environment.GetEnvironmentVariable(
                WorkIqAccountEnvironmentVariable)
            ?.Trim()
            ?? string.Empty;

    public static int GetMonitoringBatchSize(int consecutiveFailures)
        => consecutiveFailures switch
        {
            <= 0 => MonitoringBatchSize,
            1 => 10,
            2 => 5,
            _ => 1
        };

    public static string SpeechModelDirectory
        => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Peggy",
            "models");
}
