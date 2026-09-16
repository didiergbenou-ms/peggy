namespace Peggy.Core;

public static class PeggyDefaults
{
    public const string WorkIqAccountEnvironmentVariable =
        "PEGGY_WORKIQ_ACCOUNT";
    public const string WorkIqPrincipalIdEnvironmentVariable =
        "PEGGY_WORKIQ_PRINCIPAL_ID";
    public const string IncludeActiveCasesFolderEnvironmentVariable =
        "PEGGY_INCLUDE_ACTIVE_CASES_FOLDER";
    public const string OutlookScopeEnvironmentVariable = "PEGGY_OUTLOOK_SCOPE";
    public const string TeamsModeEnvironmentVariable = "PEGGY_TEAMS_MODE";
    public const string CopilotModel = "gpt-5.4";
    public const string CopilotContextTierDefault = "default";
    public const string CopilotContextTierLong = "long_context";
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

    public static bool TrySynchronizeWorkIqAccount(
        string account,
        out Exception? failure)
    {
        var normalized = WorkIqSetupService.NormalizeAccount(account);
        Environment.SetEnvironmentVariable(
            WorkIqAccountEnvironmentVariable,
            normalized,
            EnvironmentVariableTarget.Process);
        try
        {
            Environment.SetEnvironmentVariable(
                WorkIqAccountEnvironmentVariable,
                normalized,
                EnvironmentVariableTarget.User);
            failure = null;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            PlatformNotSupportedException or
            System.Security.SecurityException or
            UnauthorizedAccessException)
        {
            failure = ex;
            return false;
        }
    }

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

    public static string ResolveSpeechModelDirectory(
        string? configuredDirectory = null)
    {
        if (TryResolveSpeechModelDirectory(
                configuredDirectory,
                out var directory))
        {
            return directory;
        }
        throw new ArgumentException(
            "The configured speech model directory is not a valid path.",
            nameof(configuredDirectory));
    }

    public static bool TryResolveSpeechModelDirectory(
        string? configuredDirectory,
        out string directory)
    {
        var environment = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_MODELS");
        var selected = !string.IsNullOrWhiteSpace(environment)
            ? environment
            : configuredDirectory;
        try
        {
            directory = string.IsNullOrWhiteSpace(selected)
                ? SpeechModelDirectory
                : Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(
                        selected.Trim().Trim('"')));
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            directory = string.Empty;
            return false;
        }
    }
}
