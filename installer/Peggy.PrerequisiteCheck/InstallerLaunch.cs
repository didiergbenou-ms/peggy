using System.Diagnostics;
using System.IO;

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
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(target)!
        };
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException(
                "The interactive desktop shell did not accept the Peggy launch.");
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
