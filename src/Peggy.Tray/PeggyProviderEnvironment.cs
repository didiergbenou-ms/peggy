using System;
using System.Collections.Generic;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Tray;

internal static class PeggyProviderEnvironment
{
    public static Dictionary<string, string> Create(
        PeggySettings settings,
        string account,
        string? principalId = null)
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["PEGGY_OUTLOOK_FOLDERS"] = string.Join(
                ";",
                settings.AdditionalOutlookFolders),
            [PeggyDefaults.IncludeActiveCasesFolderEnvironmentVariable] =
                settings.IncludeActiveCasesFolder.ToString(),
            ["PEGGY_TEAMS_WATCHLIST"] = JsonSerializer.Serialize(
                settings.TeamsChannelWatchlist),
            ["PEGGY_TEAMS_TEAM_WATCHLIST"] = JsonSerializer.Serialize(
                settings.TeamsTeamWatchlist),
            ["PEGGY_TEAMS_SCOPE"] = settings.TeamsScope.ToString(),
            ["PEGGY_TEAMS_MONITOR_CHATS"] =
                settings.MonitorTeamsChatsInSelectedScope.ToString()
        };
        if (!string.IsNullOrWhiteSpace(account))
        {
            environment["PEGGY_WORKIQ_ACCOUNT"] = account;
        }
        if (!string.IsNullOrWhiteSpace(principalId))
        {
            environment[
                PeggyDefaults.WorkIqPrincipalIdEnvironmentVariable
            ] = principalId;
        }
        CopyOverride(environment, "PEGGY_MESSAGE_PROVIDER");
        CopyOverride(environment, "PEGGY_WORKIQ_PATH");
        return environment;
    }

    private static void CopyOverride(
        IDictionary<string, string> destination,
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            destination[name] = value;
        }
    }
}
