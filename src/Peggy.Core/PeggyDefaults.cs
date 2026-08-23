namespace Peggy.Core;

public static class PeggyDefaults
{
    public const string WorkIqAccountEnvironmentVariable =
        "PEGGY_WORKIQ_ACCOUNT";
    public const string CopilotModel = "gpt-5.4";
    public const string ActiveCasesFolder = "Inbox/Cases/Active";
    public const int AuditRetentionDays = 30;
    public const int StartupLookbackMinutes = 5;
    public const int MonitoringFailureThreshold = 3;
    public const int DefaultWakeMinute = 7 * 60;
    public const int DefaultSleepMinute = 19 * 60;

    public static string ConfiguredWorkIqAccount
        => Environment.GetEnvironmentVariable(
                WorkIqAccountEnvironmentVariable)
            ?.Trim()
            ?? string.Empty;

    public static string RequireConfiguredWorkIqAccount()
        => string.IsNullOrWhiteSpace(ConfiguredWorkIqAccount)
            ? throw new InvalidOperationException(
                $"Set {WorkIqAccountEnvironmentVariable} to the Microsoft " +
                "365 account Peggy should pin before first launch.")
            : ConfiguredWorkIqAccount;

    public static string SpeechModelDirectory
        => Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Peggy",
            "models");
}
