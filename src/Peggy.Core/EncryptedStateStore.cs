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
            state.PinnedAccount =
                PeggyDefaults.RequireConfiguredWorkIqAccount();
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

        state.Settings ??= new PeggySettings();
        state.Settings.AdditionalOutlookFolders ??= [];
        state.Settings.TeamsChannelWatchlist ??= [];
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
        if (state.MonitoringCursor.Initialized)
        {
            state.MonitoringCursor.InitialOutlookScanComplete = true;
        }
        state.MonitoringCursor.TeamsBoundaryMessageIds ??= [];
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

    private static PeggyStateDocument CreateDefault()
        => new()
        {
            PinnedAccount =
                PeggyDefaults.RequireConfiguredWorkIqAccount(),
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
