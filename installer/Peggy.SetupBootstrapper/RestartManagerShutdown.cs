using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Peggy.SetupBootstrapper;

internal static class RestartManagerShutdown
{
    internal static async Task RequestAsync(SetupProcessIdentity process, Action validateProcess, CancellationToken ct)
    {
        validateProcess();
        ct.ThrowIfCancellationRequested();
        // Never register files/services: RM's rescan must not expand the set to
        // another process using shared DLLs. PID + creation time defeats PID reuse.
        Check(RmStartSession(out var session, 0, new StringBuilder(33)));
        try
        {
            var unique = new UniqueProcess
            {
                Pid = process.Pid,
                Start = new FILETIME
                {
                    dwLowDateTime = unchecked((int)process.StartFileTime),
                    dwHighDateTime = (int)(process.StartFileTime >> 32)
                }
            };
            Check(RmRegisterResources(session, 0, IntPtr.Zero, 1, [unique], 0, IntPtr.Zero));
            uint count = 1, reasons = 0;
            var affected = new ProcessInfo[1];
            Check(RmGetList(session, out _, ref count, affected, ref reasons));
            if (reasons != 0 || count != 1 || affected[0].Process.Pid != unique.Pid ||
                affected[0].Process.Start.dwLowDateTime != unique.Start.dwLowDateTime ||
                affected[0].Process.Start.dwHighDateTime != unique.Start.dwHighDateTime ||
                affected[0].Session != process.Session || !string.IsNullOrEmpty(affected[0].Service))
                throw SetupAppShutdown.Blocked("Windows could not restrict legacy shutdown to the verified Peggy process. Quit Peggy manually.");
            validateProcess();
            ct.ThrowIfCancellationRequested();
            using var cancellation = ct.Register(() => RmCancelCurrentTask(session));
            // Zero flags: never RmForceShutdown. Wait for native completion even
            // on cancellation, so no shutdown continues behind a subsequent Apply.
            var result = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return RmShutdown(session, 0, IntPtr.Zero);
            });
            ct.ThrowIfCancellationRequested();
            Check(result);
        }
        finally { RmEndSession(session); }
    }

    private static void Check(int code)
    {
        if (code != 0)
            throw SetupAppShutdown.Blocked($"Windows could not gracefully close the older Peggy (Restart Manager {code}). Choose Quit Peggy from its tray menu.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { internal int Pid; internal FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        internal UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Service;
        internal int Type;
        internal uint Status;
        internal int Session;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmRegisterResources(uint session, uint files, IntPtr filenames,
        uint applications, [In] UniqueProcess[] processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[] processes, ref uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmCancelCurrentTask(uint session);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
