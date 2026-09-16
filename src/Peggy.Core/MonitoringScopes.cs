namespace Peggy.Core;

public static class MonitoringScopes
{
    public static bool IncludesSelectedChannels(TeamsMonitoringScope scope)
        => scope is TeamsMonitoringScope.AllActivity or
            TeamsMonitoringScope.SelectedTeamsAndChannels or
            TeamsMonitoringScope.ChatsAndSelectedChannels;

    public static bool IncludesChats(TeamsMonitoringScope scope)
        => scope is TeamsMonitoringScope.AllActivity or
            TeamsMonitoringScope.ChatsOnly or
            TeamsMonitoringScope.ChatsAndSelectedChannels;

    internal static OutlookMonitoringScope InferLegacyOutlookScope(
        PeggySettings settings)
    {
        var folders = settings.AdditionalOutlookFolders ?? [];
        if (folders.Any(IsInbox))
        {
            return OutlookMonitoringScope.InboxAndAllSubfolders;
        }

        return settings.IncludeActiveCasesFolder ||
            folders.Any(path => !string.IsNullOrWhiteSpace(path))
            ? OutlookMonitoringScope.InboxAndSelectedFolders
            : OutlookMonitoringScope.InboxOnly;
    }

    private static bool IsInbox(string path)
    {
        try
        {
            return string.Equals(
                OutlookFolderPath.Normalize(path),
                OutlookFolderPath.Inbox,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
