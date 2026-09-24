using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Peggy.Tray;

namespace Peggy.SetupBootstrapper;

internal sealed class WindowsSetupShutdownPlatform : ISetupShutdownPlatform, IDisposable
{
    internal static string CanonicalExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Peggy", "peggy-tray.exe");
    private readonly Dictionary<int, SafeProcessHandle> _handles = [];
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly int _session = Process.GetCurrentProcess().SessionId;

    public IReadOnlyList<SetupProcessIdentity> FindRunning()
    {
        var matches = new List<SetupProcessIdentity>();
        foreach (var process in Process.GetProcessesByName("peggy-tray"))
        {
            using (process)
            {
                var handle = OpenProcess(0x00100000 | 0x1000, false, process.Id);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error == 87) continue; // Exited during enumeration.
                    throw SetupAppShutdown.Blocked("A Peggy process cannot be verified with your current Windows permissions. Close it from its own desktop.");
                }
                if (WaitForSingleObject(handle, 0) == 0) { handle.Dispose(); continue; }
                try
                {
                    var identity = ReadIdentity(process.Id, handle);
                    if (!string.Equals(identity.Path, CanonicalExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        handle.Dispose();
                        continue;
                    }
                    _handles.Add(process.Id, handle);
                    matches.Add(identity);
                }
                catch { handle.Dispose(); throw; }
            }
        }
        return matches;
    }

    public void Validate(SetupProcessIdentity process)
    {
        if (!_handles.TryGetValue(process.Pid, out var handle) || handle.IsInvalid ||
            WaitForSingleObject(handle, 0) != 258 ||
            !SetupProcessValidation.Matches(process, ReadIdentity(process.Pid, handle),
                CanonicalExecutable, _sid, _session))
            throw SetupAppShutdown.Blocked("Peggy's process, installation path or Windows owner changed. No shutdown request was sent.");
        if ((File.GetAttributes(CanonicalExecutable) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Path.GetDirectoryName(CanonicalExecutable)!) & FileAttributes.ReparsePoint) != 0)
            throw SetupAppShutdown.Blocked("Peggy's installation is redirected and cannot be verified safely. Quit Peggy manually.");
    }

    private static SetupProcessIdentity ReadIdentity(int pid, SafeProcessHandle handle)
    {
        var path = new StringBuilder(32768);
        var capacity = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref capacity) ||
            !GetProcessTimes(handle, out var start, out _, out _, out _) ||
            !ProcessIdToSessionId(pid, out var session))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(handle, 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) || elevated != 0)
                throw SetupAppShutdown.Blocked("Peggy is elevated or its Windows identity cannot be verified. Quit it from its own tray menu; setup will not bypass permissions.");
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var executable = Path.GetFullPath(path.ToString());
            return new(pid, start, executable, identity.User!.Value, session,
                FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "");
        }
    }

    public Task<bool> RequestModernAsync(SetupProcessIdentity process, CancellationToken ct) =>
        SetupShutdownPipe.RequestAsync(SetupShutdownPipe.Name(process.Pid, process.StartFileTime),
            process.Pid, () => Validate(process), ct);

    public Task RequestLegacyAsync(SetupProcessIdentity process, CancellationToken ct) =>
        RestartManagerShutdown.RequestAsync(process, () => Validate(process), ct);

    public async Task WaitForExitAsync(SetupProcessIdentity process, CancellationToken ct)
    {
        var handle = _handles[process.Pid];
        while (WaitForSingleObject(handle, 0) == 258)
            await Task.Delay(100, ct);
        if (WaitForSingleObject(handle, 0) != 0)
            throw SetupAppShutdown.Blocked("Windows could not confirm Peggy has exited.");
    }

    public bool FilesUnlocked()
    {
        var directory = Path.GetDirectoryName(CanonicalExecutable)!;
        if (!Directory.Exists(directory)) return true;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".exe" or ".dll"))
            {
                using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        foreach (var handle in _handles.Values) handle.Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long start, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int value, int length, out int returned);
}

internal static class SetupProcessValidation
{
    internal static bool Matches(SetupProcessIdentity expected, SetupProcessIdentity actual,
        string canonicalPath, string currentSid, int currentSession) =>
        expected.Pid == actual.Pid && expected.StartFileTime == actual.StartFileTime &&
        string.Equals(actual.Path, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase) &&
        actual.Sid == currentSid && expected.Sid == actual.Sid &&
        actual.Session == currentSession && expected.Session == actual.Session &&
        expected.Version == actual.Version;
}
