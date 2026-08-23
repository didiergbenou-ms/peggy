using System.Globalization;

namespace Peggy.Core;

public enum MonitoringScheduleTransition
{
    None,
    Wake,
    Sleep
}

public readonly record struct MonitoringScheduleStatus(
    bool IsSleeping,
    DateTime? NextTransitionLocal,
    MonitoringScheduleTransition NextTransition);

public static class MonitoringSchedule
{
    public static bool CanRunAutomatic(
        PeggySettings settings,
        DateTime localNow)
        => !settings.MonitoringPaused &&
           !Evaluate(settings, localNow).IsSleeping;

    public static MonitoringScheduleStatus Evaluate(
        PeggySettings settings,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.MonitoringScheduleEnabled)
        {
            return new MonitoringScheduleStatus(
                false,
                null,
                MonitoringScheduleTransition.None);
        }

        Validate(
            settings.MonitoringWakeMinute,
            settings.MonitoringSleepMinute);
        var minute = (localNow.Hour * 60) + localNow.Minute;
        var isAwake = IsAwake(
            minute,
            settings.MonitoringWakeMinute,
            settings.MonitoringSleepMinute);
        var transitionMinute = isAwake
            ? settings.MonitoringSleepMinute
            : settings.MonitoringWakeMinute;
        var transition = localNow.Date.AddMinutes(transitionMinute);
        if (transition <= localNow)
        {
            transition = transition.AddDays(1);
        }

        return new MonitoringScheduleStatus(
            !isAwake,
            transition,
            isAwake
                ? MonitoringScheduleTransition.Sleep
                : MonitoringScheduleTransition.Wake);
    }

    public static bool IsAwake(
        int currentMinute,
        int wakeMinute,
        int sleepMinute)
    {
        Validate(wakeMinute, sleepMinute);
        if (currentMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(currentMinute));
        }

        return wakeMinute < sleepMinute
            ? currentMinute >= wakeMinute &&
              currentMinute < sleepMinute
            : currentMinute >= wakeMinute ||
              currentMinute < sleepMinute;
    }

    public static string FormatMinute(int minute)
    {
        if (minute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(minute));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minute / 60:00}:{minute % 60:00}");
    }

    public static void Validate(int wakeMinute, int sleepMinute)
    {
        if (wakeMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(wakeMinute));
        }
        if (sleepMinute is < 0 or >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(sleepMinute));
        }
        if (wakeMinute == sleepMinute)
        {
            throw new ArgumentException(
                "Wake and sleep times must be different.");
        }
    }

    public const int MinutesPerDay = 24 * 60;
}
