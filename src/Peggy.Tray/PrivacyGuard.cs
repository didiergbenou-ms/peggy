using System;
using System.Runtime.InteropServices;

namespace Peggy.Tray;

public sealed class PrivacyGuard
{
    private bool _sessionLocked;

    public bool ShouldMask
    {
        get
        {
            if (_sessionLocked)
            {
                return true;
            }

            try
            {
                if (SHQueryUserNotificationState(out var state) != 0)
                {
                    return true;
                }

                return state is
                    QueryUserNotificationState.NotPresent or
                    QueryUserNotificationState.Busy or
                    QueryUserNotificationState.RunningDirect3DFullScreen or
                    QueryUserNotificationState.PresentationMode;
            }
            catch (DllNotFoundException)
            {
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return true;
            }
        }
    }

    public void SetSessionLocked(bool locked)
    {
        _sessionLocked = locked;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(
        out QueryUserNotificationState state);

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningDirect3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }
}
