using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

public enum TroubleshootingCheckStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    NotReached
}

public sealed record TroubleshootingCheckResult(
    string Name,
    TroubleshootingCheckStatus Status,
    string Detail,
    MonitoringFailureDetails? Failure = null);

public sealed record TroubleshootingRecommendation(
    string Summary,
    bool ShowSignIn,
    bool ShowSettings,
    bool ShowRetry);

public sealed record MonitoringTroubleshootingReport(
    DateTimeOffset GeneratedUtc,
    MonitoringFailureDetails OriginalFailure,
    IReadOnlyList<TroubleshootingCheckResult> Checks,
    TroubleshootingRecommendation Recommendation)
{
    public string ToTechnicalText()
    {
        var text = new StringBuilder();
        text.AppendLine("Peggy monitoring troubleshooting");
        text.AppendLine($"Generated: {GeneratedUtc:O}");
        text.AppendLine();
        AppendFailure(text, "Original failure", OriginalFailure);
        text.AppendLine();
        text.AppendLine("Checks:");
        foreach (var check in Checks)
        {
            text.AppendLine(
                $"- {check.Name}: {check.Status} - {check.Detail}");
            if (check.Failure is not null)
            {
                AppendFailure(text, "  Failure", check.Failure);
            }
        }
        text.AppendLine();
        text.AppendLine($"Recommendation: {Recommendation.Summary}");
        return text.ToString().TrimEnd();
    }

    private static void AppendFailure(
        StringBuilder text,
        string label,
        MonitoringFailureDetails failure)
    {
        text.AppendLine($"{label}:");
        text.AppendLine($"  Time: {failure.OccurredUtc:O}");
        text.AppendLine($"  Stage: {failure.Stage}");
        text.AppendLine($"  Code: {failure.Code}");
        text.AppendLine($"  Type: {failure.ExceptionType}");
        text.AppendLine($"  Message: {failure.Message}");
        if (failure.StatusCode is not null)
        {
            text.AppendLine($"  Status: {failure.StatusCode}");
        }
        if (failure.RetryAfterSeconds is not null)
        {
            text.AppendLine(
                $"  Retry after: {failure.RetryAfterSeconds:0.##} seconds");
        }
        if (failure.Operation.Length > 0)
        {
            text.AppendLine($"  Operation: {failure.Operation}");
        }
        if (failure.RequestId.Length > 0)
        {
            text.AppendLine($"  Request ID: {failure.RequestId}");
        }
        if (failure.RetrievalReferenceUtc is { } referenceUtc)
        {
            text.AppendLine($"  Retrieval reference: {referenceUtc:O}");
        }
        if (failure.BatchLimit > 0)
        {
            text.AppendLine($"  Batch limit: {failure.BatchLimit}");
        }
        foreach (var item in failure.ExceptionChain)
        {
            text.AppendLine($"  Exception: {item}");
        }
    }
}

internal sealed class MonitoringTroubleshooter
{
    public static readonly IReadOnlyList<string> CheckNames =
    [
        "Microsoft 365 account and Inbox",
        "Configured Teams scope",
        "GitHub Copilot model access",
        "Exact monitoring retrieval replay"
    ];

    private readonly MonitoringFailureDetails _failure;
    private readonly PeggySettings _settings;
    private readonly MonitoringCursorState _cursor;
    private readonly string _account;
    private readonly string _principalId;
    private readonly int _failureCount;
    private readonly Func<IMessageProvider> _providerFactory;
    private readonly Func<
        CancellationToken,
        Task<IReadOnlyList<CopilotModelOption>>> _modelLoader;

    public MonitoringTroubleshooter(
        MonitoringFailureDetails failure,
        PeggySettings settings,
        MonitoringCursorState cursor,
        string account,
        int failureCount,
        Func<
            CancellationToken,
            Task<IReadOnlyList<CopilotModelOption>>> modelLoader,
        Func<IMessageProvider>? providerFactory = null,
        string? principalId = null)
    {
        _failure = failure;
        _settings = Clone(settings);
        _cursor = Clone(cursor);
        _account = account;
        _principalId = principalId?.Trim() ?? string.Empty;
        _failureCount = failureCount;
        _modelLoader = modelLoader;
        _providerFactory = providerFactory ??
            (() => MessageProviderFactory.Create(
                _settings,
                _account,
                _principalId));
    }

    public async Task<MonitoringTroubleshootingReport> RunAsync(
        IProgress<TroubleshootingCheckResult>? progress = null,
        CancellationToken ct = default)
    {
        var checks = new List<TroubleshootingCheckResult>();

        checks.Add(await RunCheckAsync(
            CheckNames[0],
            async token =>
            {
                if (string.IsNullOrWhiteSpace(_account))
                {
                    throw new InvalidOperationException(
                        "Peggy has no pinned Microsoft 365 account.");
                }
                var folders = await _providerFactory()
                    .ListOutlookChildFoldersAsync(null, token);
                return $"Pinned account and Inbox are accessible; " +
                       $"{folders.Count} top-level folders were returned.";
            },
            progress,
            ct));

        if (_settings.TeamsScope == TeamsMonitoringScope.Off)
        {
            var skipped = new TroubleshootingCheckResult(
                CheckNames[1],
                TroubleshootingCheckStatus.NotReached,
                "Teams monitoring is disabled in Settings.");
            progress?.Report(skipped);
            checks.Add(skipped);
        }
        else
        {
            checks.Add(await RunCheckAsync(
                CheckNames[1],
                CheckTeamsScopeAsync,
                progress,
                ct));
        }

        checks.Add(await RunCheckAsync(
            CheckNames[2],
            CheckCopilotModelAsync,
            progress,
            ct));

        checks.Add(await RunCheckAsync(
            CheckNames[3],
            ReplayRetrievalAsync,
            progress,
            ct));

        return new MonitoringTroubleshootingReport(
            DateTimeOffset.UtcNow,
            _failure,
            checks,
            CreateRecommendation(checks));
    }

    private async Task<string> CheckTeamsScopeAsync(CancellationToken ct)
    {
        var provider = _providerFactory();
        var teams = await provider.ListJoinedTeamsAsync(ct);
        if (_settings.TeamsScope !=
            TeamsMonitoringScope.SelectedTeamsAndChannels)
        {
            return $"{teams.Count} joined teams are accessible.";
        }

        var selectedTeamIds = _settings.TeamsTeamWatchlist
            .Select(item => item.TeamId)
            .Concat(_settings.TeamsChannelWatchlist.Select(item => item.TeamId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var missingTeams = selectedTeamIds
            .Where(id => teams.All(team => !string.Equals(
                team.TeamId,
                id,
                StringComparison.Ordinal)))
            .ToList();
        if (missingTeams.Count > 0)
        {
            throw new InvalidOperationException(
                $"{missingTeams.Count} selected Teams source(s) are no longer " +
                "available to the pinned account.");
        }

        foreach (var selectedTeamId in selectedTeamIds)
        {
            var team = teams.Single(item => string.Equals(
                item.TeamId,
                selectedTeamId,
                StringComparison.Ordinal));
            var channels = await provider.ListTeamChannelsAsync(
                team.TeamId,
                team.TeamName,
                ct);
            var missingChannels = _settings.TeamsChannelWatchlist
                .Where(item => string.Equals(
                    item.TeamId,
                    selectedTeamId,
                    StringComparison.Ordinal))
                .Count(item => channels.All(channel => !string.Equals(
                    channel.ChannelId,
                    item.ChannelId,
                    StringComparison.Ordinal)));
            if (missingChannels > 0)
            {
                throw new InvalidOperationException(
                    $"{missingChannels} selected channel(s) in " +
                    $"'{team.TeamName}' are no longer available.");
            }
        }

        return selectedTeamIds.Count == 0
            ? _settings.MonitorTeamsChatsInSelectedScope
                ? "Selected-only Teams monitoring has no team or channel " +
                  "roots; direct and group chat monitoring is enabled."
                : "Selected-only Teams monitoring has no selected sources."
            : $"{selectedTeamIds.Count} selected team source(s) are accessible.";
    }

    private async Task<string> CheckCopilotModelAsync(CancellationToken ct)
    {
        var models = await _modelLoader(ct);
        var model = models.FirstOrDefault(item => string.Equals(
            item.Id,
            _settings.CopilotModel,
            StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            throw new InvalidOperationException(
                $"Model '{_settings.CopilotModel}' is not available to the " +
                "signed-in GitHub Copilot account.");
        }
        if (_settings.CopilotReasoningEffort.Length > 0 &&
            !(model.SupportedReasoningEfforts ?? []).Contains(
                _settings.CopilotReasoningEffort,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support reasoning effort " +
                $"'{_settings.CopilotReasoningEffort}'.");
        }
        if (string.Equals(
                _settings.CopilotContextTier,
                PeggyDefaults.CopilotContextTierLong,
                StringComparison.OrdinalIgnoreCase) &&
            !model.SupportsLongContext)
        {
            throw new InvalidOperationException(
                $"Model '{model.Id}' does not support long context.");
        }
        return $"Model '{model.Id}' and its selected options are available.";
    }

    private async Task<string> ReplayRetrievalAsync(CancellationToken ct)
    {
        var batch = await _providerFactory().GetChangesAsync(
            Clone(_cursor),
            _failure.BatchLimit > 0
                ? _failure.BatchLimit
                : PeggyDefaults.GetMonitoringBatchSize(_failureCount),
            _failure.RetrievalReferenceUtc ??
                _failure.OccurredUtc,
            ct);
        var detail =
            $"Read-only replay succeeded with {batch.Messages.Count} item(s); " +
            $"hasMore={batch.HasMore}. Peggy state was not changed.";
        return string.IsNullOrWhiteSpace(batch.Warning)
            ? detail
            : $"{detail} Provider warning: {batch.Warning}";
    }

    private static async Task<TroubleshootingCheckResult> RunCheckAsync(
        string name,
        Func<CancellationToken, Task<string>> operation,
        IProgress<TroubleshootingCheckResult>? progress,
        CancellationToken ct)
    {
        progress?.Report(new TroubleshootingCheckResult(
            name,
            TroubleshootingCheckStatus.Running,
            "Running..."));
        try
        {
            var detail = await operation(ct);
            var result = new TroubleshootingCheckResult(
                name,
                TroubleshootingCheckStatus.Passed,
                detail);
            progress?.Report(result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = MonitoringFailureInspector.Create(
                ex,
                name,
                "diagnostic-check-failed",
                DateTimeOffset.UtcNow);
            var result = new TroubleshootingCheckResult(
                name,
                TroubleshootingCheckStatus.Failed,
                failure.Message,
                failure);
            progress?.Report(result);
            return result;
        }
    }

    private TroubleshootingRecommendation CreateRecommendation(
        IReadOnlyList<TroubleshootingCheckResult> checks)
    {
        var account = Find(checks, CheckNames[0]);
        var teams = Find(checks, CheckNames[1]);
        var copilot = Find(checks, CheckNames[2]);
        var replay = Find(checks, CheckNames[3]);
        var statusSource = checks.FirstOrDefault(check =>
            check.Status == TroubleshootingCheckStatus.Failed &&
            check.Failure?.StatusCode is not null);
        var statusCode = statusSource?.Failure?.StatusCode;
        if (statusCode is null &&
            replay.Status == TroubleshootingCheckStatus.Failed &&
            _failure.StatusCode is not null)
        {
            statusSource = replay;
            statusCode = _failure.StatusCode;
        }
        if (statusCode == 429)
        {
            var retryText = statusSource?.Failure?.RetryAfterSeconds is
                { } retryAfter
                ? $" Wait at least {retryAfter:0.##} seconds before retrying."
                : " Wait before retrying.";
            return new TroubleshootingRecommendation(
                "A required service is rate-limiting Peggy." + retryText,
                ShowSignIn: false,
                ShowSettings: false,
                ShowRetry: true);
        }
        if (statusCode is >= 500 and <= 599)
        {
            return new TroubleshootingRecommendation(
                "A required service is temporarily unavailable. Retry after " +
                "a short wait; create a diagnostic ZIP if it continues.",
                ShowSignIn: false,
                ShowSettings: false,
                ShowRetry: true);
        }
        if (statusCode is 401 or 403)
        {
            if (statusSource?.Name == CheckNames[2])
            {
                return new TroubleshootingRecommendation(
                    "GitHub Copilot denied access to the selected model or " +
                    "options. Choose an available configuration in Settings " +
                    "and verify organization policy.",
                    ShowSignIn: false,
                    ShowSettings: true,
                    ShowRetry: true);
            }
            return new TroubleshootingRecommendation(
                "Microsoft 365 denied a required read. Sign in again and " +
                "verify tenant consent.",
                ShowSignIn: true,
                ShowSettings: false,
                ShowRetry: false);
        }
        if (account.Status == TroubleshootingCheckStatus.Failed)
        {
            return new TroubleshootingRecommendation(
                "Microsoft 365 account or Inbox access failed. Sign in again, " +
                "then rerun the checks.",
                ShowSignIn: true,
                ShowSettings: false,
                ShowRetry: false);
        }
        if (copilot.Status == TroubleshootingCheckStatus.Failed)
        {
            return new TroubleshootingRecommendation(
                "The selected GitHub Copilot model is unavailable or blocked. " +
                "Choose an available model configuration in Settings, then " +
                "retry monitoring.",
                ShowSignIn: false,
                ShowSettings: true,
                ShowRetry: true);
        }
        if (teams.Status == TroubleshootingCheckStatus.Failed)
        {
            return new TroubleshootingRecommendation(
                "One or more configured Teams sources are unavailable. Update " +
                "the Teams selections in Settings, then retry monitoring.",
                ShowSignIn: false,
                ShowSettings: true,
                ShowRetry: true);
        }
        if (replay.Status == TroubleshootingCheckStatus.Passed)
        {
            return new TroubleshootingRecommendation(
                "The exact read-only retrieval now succeeds. The original " +
                "failure may have been transient; retry monitoring.",
                ShowSignIn: false,
                ShowSettings: false,
                ShowRetry: true);
        }
        return new TroubleshootingRecommendation(
            "Basic services pass, but the exact monitoring read still fails. " +
            "Copy the technical details or create a diagnostic ZIP.",
            ShowSignIn: false,
            ShowSettings: false,
            ShowRetry: true);
    }

    private static TroubleshootingCheckResult Find(
        IReadOnlyList<TroubleshootingCheckResult> checks,
        string name)
        => checks.Single(check => check.Name == name);

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(
               JsonSerializer.Serialize(value))
           ?? throw new InvalidOperationException(
               $"Could not clone {typeof(T).Name} for troubleshooting.");
}
