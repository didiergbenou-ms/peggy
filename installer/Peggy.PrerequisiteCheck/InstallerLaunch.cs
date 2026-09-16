using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Peggy.PrerequisiteCheck;

internal static class InstallerLaunch
{
    internal static bool IsLaunchCommand(string[] args) =>
        args.Length != 0 && args[0] is "--launch-msi" or "--launch-bundle";

    internal static bool IsAllowed(string[] args) =>
        args is ["--launch-msi", "--display", "5", "--target", _] ||
        args is ["--launch-bundle", "--display", "4", "--action", "6", "--target", _];

    internal static int Run(string[] args)
    {
        if (!IsAllowed(args))
        {
            Log("Launch skipped: not a full-UI installation.");
            return 0;
        }
        try
        {
            if (!InteractiveIdentity.CanShowUi || !InteractiveIdentity.IsCurrentDesktopUser())
            {
                throw new InvalidOperationException(
                    "No matching non-elevated interactive desktop; refusing service/other-user launch.");
            }
            var target = Path.GetFullPath(args[^1]);
            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Peggy", "peggy-tray.exe");
            if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            {
                throw new InvalidOperationException("The installed Peggy executable is unavailable at its per-user location.");
            }
            PrerequisiteEngine.RefreshEnvironment();
            if (!InteractiveIdentity.IsElevated(Process.GetCurrentProcess()))
            {
                using var process = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)!
                }) ?? throw new InvalidOperationException("Peggy did not start.");
                Log($"Started Peggy in the interactive user session (process {process.Id}).");
            }
            else
            {
                LaunchFromDesktop(target);
                Log("Requested Peggy launch through the verified non-elevated desktop shell.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Peggy is installed, but automatic launch failed: {ex.Message} " +
                "Open Peggy from the Start menu. Installation is not rolled back.");
            return 1;
        }
    }

    private static void LaunchFromDesktop(string target)
    {
        // Use the existing desktop's automation object, never a newly-created
        // Shell.Application that could inherit an elevated installer identity.
        object? windows = null;
        object? desktop = null;
        object? document = null;
        object? application = null;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!);
            dynamic collection = windows!;
            object location = 0;
            object root = 0;
            int window;
            desktop = collection.FindWindowSW(ref location, ref root, 8, out window, 1);
            if (desktop is null) throw new InvalidOperationException("The desktop shell is unavailable.");
            document = ((dynamic)desktop).Document;
            application = ((dynamic)document!).Application;
            ((dynamic)application!).ShellExecute(target, "", Path.GetDirectoryName(target), "open", 1);
        }
        finally
        {
            foreach (var item in new[] { application, document, desktop, windows })
            {
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
        }
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Peggy");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "installer-launch.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* Installer exit code remains available in the MSI/Burn log. */ }
    }
}
