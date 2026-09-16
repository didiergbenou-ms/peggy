using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Peggy.PrerequisiteCheck;

internal static class InteractiveIdentity
{
    internal static bool CanShowUi => Environment.UserInteractive &&
        Process.GetCurrentProcess().SessionId != 0 && !WindowsIdentity.GetCurrent().IsSystem;

    internal static bool CanInstallDependencies
    {
        get
        {
            try
            {
                return CanShowUi && IsCurrentDesktopUser() &&
                    !IsElevated(Process.GetCurrentProcess());
            }
            catch { return false; }
        }
    }

    internal static bool IsCurrentDesktopUser()
    {
        var window = GetShellWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        using var shell = Process.GetProcessById((int)processId);
        if (shell.SessionId != Process.GetCurrentProcess().SessionId || IsElevated(shell)) return false;
        using var token = OpenToken(shell);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        using var current = WindowsIdentity.GetCurrent();
        return identity.User == current.User;
    }

    internal static bool IsElevated(Process process)
    {
        using var token = OpenToken(process);
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return elevated != 0;
    }

    private static SafeAccessTokenHandle OpenToken(Process process)
    {
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return token;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnLength);
}
