using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Peggy.Core;

[SupportedOSPlatform("windows")]
public sealed class EncryptedStateStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("Peggy.State.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedStateStore(string? path = null)
    {
        var configuredPath = Environment.GetEnvironmentVariable(
            "PEGGY_STATE_PATH");
        _path = path ??
            (!string.IsNullOrWhiteSpace(configuredPath)
                ? System.IO.Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(
                        configuredPath.Trim().Trim('"')))
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Peggy",
                    "state.dat"));
    }

    public string Path => _path;

    public async Task<PeggyStateDocument> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
            {
                return CreateDefault();
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = await File.ReadAllBytesAsync(_path, ct);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    "The assistant could not read its encrypted state.", ex);
            }

            byte[] plaintext;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "The assistant's encrypted state cannot be decrypted for this Windows user.",
                    ex);
            }

            try
            {
                var state = JsonSerializer.Deserialize<PeggyStateDocument>(
                    plaintext,
                    JsonOptions)
                    ?? throw new InvalidDataException("The assistant's encrypted state was empty.");
                NormalizeAndPrune(state, DateTimeOffset.UtcNow);
                return state;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    "The assistant's encrypted state has an unsupported format.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        PeggyStateDocument state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(ct);
        try
        {
            NormalizeAndPrune(state, DateTimeOffset.UtcNow);
            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException(
                    "The assistant's encrypted state path has no parent directory.");
            Directory.CreateDirectory(directory);

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plaintext,
                    Entropy,
                    DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            var temporaryPath = _path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, ct);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The assistant could not persist its encrypted state.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void NormalizeAndPrune(
        PeggyStateDocument state,
        DateTimeOffset nowUtc)
    {
        var configuredAccount = PeggyDefaults.ConfiguredWorkIqAccount;
        if (string.IsNullOrWhiteSpace(state.PinnedAccount))
        {
            state.PinnedAccount = configuredAccount;
        }
        else if (!string.IsNullOrWhiteSpace(configuredAccount) &&
                 !string.Equals(
                     state.PinnedAccount,
                     configuredAccount,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The assistant's encrypted state belongs to a different Microsoft 365 account.");
        }
        if (string.IsNullOrWhiteSpace(state.PinnedMailbox))
        {
            state.PinnedMailbox = state.PinnedAccount;
        }
        state.WorkIqEulaAcceptedVersion ??= string.Empty;

        state.Settings ??= new PeggySettings();
        state.Settings.CopilotModel =
            string.IsNullOrWhiteSpace(state.Settings.CopilotModel)
                ? PeggyDefaults.CopilotModel
                : state.Settings.CopilotModel.Trim();
        state.Settings.CopilotReasoningEffort =
            state.Settings.CopilotReasoningEffort?.Trim() ?? string.Empty;
        state.Settings.CopilotContextTier =
            state.Settings.CopilotContextTier?.Trim().ToLowerInvariant() ??
            PeggyDefaults.CopilotContextTierDefault;
        if (state.Settings.CopilotContextTier is not (
                PeggyDefaults.CopilotContextTierDefault or
                PeggyDefaults.CopilotContextTierLong))
        {
            state.Settings.CopilotContextTier =
                PeggyDefaults.CopilotContextTierDefault;
        }
        state.Settings.AdditionalOutlookFolders ??= [];
        if (state.Settings.AdditionalOutlookFolders.Any(path =>
                string.Equals(
                    TryNormalizeOutlookFolderPath(path),
                    PeggyDefaults.ActiveCasesFolder,
                    StringComparison.OrdinalIgnoreCase)))
        {
            state.Settings.IncludeActiveCasesFolder = true;
        }
        state.Settings.AdditionalOutlookFolders =
            state.Settings.AdditionalOutlookFolders
                .Select(TryNormalizeOutlookFolderPath)
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    !string.Equals(
                        path,
                        PeggyDefaults.ActiveCasesFolder,
                        StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (!Enum.IsDefined(state.Settings.TeamsScope))
        {
            state.Settings.TeamsScope = TeamsMonitoringScope.AllActivity;
        }
        state.Settings.TeamsTeamWatchlist ??= [];
        state.Settings.TeamsChannelWatchlist ??= [];
        NormalizeTeamsWatchlists(state.Settings);
        if (state.Settings.MonitoringWakeMinute is < 0 or
            >= MonitoringSchedule.MinutesPerDay)
        {
            state.Settings.MonitoringWakeMinute =
                PeggyDefaults.DefaultWakeMinute;
        }
        if (state.Settings.MonitoringSleepMinute is < 0 or
            >= MonitoringSchedule.MinutesPerDay ||
            state.Settings.MonitoringSleepMinute ==
            state.Settings.MonitoringWakeMinute)
        {
            state.Settings.MonitoringSleepMinute =
                PeggyDefaults.DefaultSleepMinute;
        }
        if (state.Settings.MonitoringSleepMinute ==
            state.Settings.MonitoringWakeMinute)
        {
            state.Settings.MonitoringWakeMinute =
                PeggyDefaults.DefaultWakeMinute;
            state.Settings.MonitoringSleepMinute =
                PeggyDefaults.DefaultSleepMinute;
        }
        if (state.Settings.SpeechTuningVersion < 3)
        {
            state.Settings.SpeechThreads = 8;
            state.Settings.SpeechVoiceId = 0;
            state.Settings.SpeechSpeed = 1.15F;
            state.Settings.SpeechTuningVersion = 3;
        }
        state.Settings.SpeechThreads = Math.Clamp(
            state.Settings.SpeechThreads,
            1,
            8);
        state.MonitoringCursor ??= new MonitoringCursorState();
        state.MonitoringCursor.OutlookScanStartedUtc ??=
            state.MonitoringCursor.InitialScanStartedUtc;
        state.MonitoringCursor.TeamsScanStartedUtc ??=
            state.MonitoringCursor.InitialScanStartedUtc;
        if (state.MonitoringCursor.Initialized)
        {
            state.MonitoringCursor.InitialOutlookScanComplete = true;
        }
        state.MonitoringCursor.TeamsBoundaryMessageIds ??= [];
        state.MonitoringCursor.TeamsSearchProcessedHitIds ??= [];
        state.MonitoringCursor.DirectChatBoundaryMessageIds ??= [];
        state.MonitoringCursor.DirectChatProcessedMessageIds ??= [];
        state.MonitoringCursor.TeamsSearchOffset = Math.Max(
            0,
            state.MonitoringCursor.TeamsSearchOffset);
        if (state.MonitoringCursor.TeamsSearchWindowEndUtc is null)
        {
            state.MonitoringCursor.TeamsSearchOffset = 0;
            state.MonitoringCursor.TeamsSearchProcessedHitIds.Clear();
        }
        state.MonitoringCursor.OutlookBoundaryMessageIds ??= [];
        state.MonitoringCursor.OutlookDeltaLinks ??=
            new Dictionary<string, string>(StringComparer.Ordinal);
        state.MonitoringCursor.ChannelWatermarksUtc ??=
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        state.MonitoringCursor.ChannelBoundaryMessageIds ??=
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        state.MonitoringCursor.ChannelDeltaLinks ??=
            new Dictionary<string, string>(StringComparer.Ordinal);
        state.MonitoringCursor.ChannelPageSizes ??=
            new Dictionary<string, int>(StringComparer.Ordinal);
        state.MonitoringCursor.ParticipatedChannelThreads ??= [];
        state.Alerts ??= [];
        state.FeedbackRules ??= [];
        state.SeenMessages ??= [];
        state.CreditHistory ??= [];
        state.AuditLog ??= [];

        var cutoff = nowUtc.AddDays(-PeggyDefaults.AuditRetentionDays);
        state.AuditLog.RemoveAll(entry => entry.TimestampUtc < cutoff);
        state.CreditHistory.RemoveAll(entry => entry.TimestampUtc < cutoff);
        state.SeenMessages.RemoveAll(entry => entry.SeenUtc < cutoff);
        state.FeedbackRules.RemoveAll(entry => entry.CreatedUtc < cutoff);
        state.MonitoringCursor.ParticipatedChannelThreads.RemoveAll(
            thread => thread.LastActivityUtc < cutoff);
        state.Alerts.RemoveAll(alert =>
            alert.Status != AlertStatus.Active &&
            alert.UpdatedUtc < cutoff);
    }

    private static string TryNormalizeOutlookFolderPath(string path)
    {
        try
        {
            return OutlookFolderPath.Normalize(path);
        }
        catch (ArgumentException)
        {
            return path?.Trim() ?? string.Empty;
        }
    }

    private static void NormalizeTeamsWatchlists(PeggySettings settings)
    {
        settings.TeamsTeamWatchlist = settings.TeamsTeamWatchlist
            .Where(item => !string.IsNullOrWhiteSpace(item.TeamId))
            .GroupBy(item => item.TeamId.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var item = group.First();
                return new TeamsTeamWatch
                {
                    TeamId = group.Key,
                    DisplayName = item.DisplayName?.Trim() ?? string.Empty,
                    ExcludedChannelIds = group
                        .SelectMany(value => value.ExcludedChannelIds ?? [])
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList()
                };
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TeamId, StringComparer.Ordinal)
            .ToList();

        var selectedTeamIds = settings.TeamsTeamWatchlist
            .Select(item => item.TeamId)
            .ToHashSet(StringComparer.Ordinal);
        settings.TeamsChannelWatchlist = settings.TeamsChannelWatchlist
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.TeamId) &&
                !string.IsNullOrWhiteSpace(item.ChannelId) &&
                !selectedTeamIds.Contains(item.TeamId.Trim()))
            .GroupBy(
                item => $"{item.TeamId.Trim()}\n{item.ChannelId.Trim()}",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var item = group.First();
                return new TeamsChannelWatch
                {
                    TeamId = item.TeamId.Trim(),
                    ChannelId = item.ChannelId.Trim(),
                    DisplayName = item.DisplayName?.Trim() ?? string.Empty
                };
            })
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TeamId, StringComparer.Ordinal)
            .ThenBy(item => item.ChannelId, StringComparer.Ordinal)
            .ToList();
    }

    private static PeggyStateDocument CreateDefault()
        => new()
        {
            PinnedAccount = PeggyDefaults.ConfiguredWorkIqAccount,
            CopilotSessionDate = DateOnly.FromDateTime(DateTime.Now),
            CopilotSessionId = Guid.NewGuid().ToString()
        };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
