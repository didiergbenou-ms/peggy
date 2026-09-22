namespace Peggy.Core;

public static class AlertDayPolicy
{
    public static bool IsPreviousDay(
        AlertRecord alert,
        DateTimeOffset nowUtc,
        TimeZoneInfo? timeZone = null)
        => IsPreviousDay(
            alert.FirstSeenUtc > alert.LastActivityUtc ? alert.FirstSeenUtc : alert.LastActivityUtc,
            nowUtc,
            timeZone);

    public static bool IsPreviousDay(
        DateTimeOffset activityUtc,
        DateTimeOffset nowUtc,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(activityUtc, zone).DateTime) <
               DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
    }

    public static int ClearPreviousDays(
        PeggyStateDocument state,
        DateTimeOffset nowUtc,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var cleared = 0;
        foreach (var alert in state.Alerts)
        {
            if (alert.Status is not (AlertStatus.Active or AlertStatus.Snoozed) ||
                !IsPreviousDay(alert, nowUtc, timeZone))
            {
                continue;
            }

            alert.Status = AlertStatus.Dismissed;
            alert.SnoozedUntilUtc = null;
            alert.UpdatedUtc = nowUtc;
            cleared++;
        }
        return cleared;
    }
}
