using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Peggy.PrerequisiteCheck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AttachConsole(unchecked((uint)-1));
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            Console.Error.WriteLine("Peggy requires 64-bit Windows.");
            return 1;
        }

        if (InstallerLaunch.IsLaunchCommand(args))
        {
            return InstallerLaunch.Run(args);
        }
        if (SetupMode.IsInteractive(args))
        {
            if (!InteractiveIdentity.CanShowUi || !InteractiveIdentity.IsCurrentDesktopUser())
            {
                Console.Error.WriteLine(
                    "Run setup normally from the signed-in Windows desktop. " +
                    "Interactive prerequisite setup cannot use a service or another user's credentials.");
                return 1;
            }
            var app = new Application();
            var window = new PrerequisiteWindow();
            app.Run(window);
            return window.Accepted ? 0 : 1602;
        }
        if (!SetupMode.IsCheckCommand(args))
        {
            Console.Error.WriteLine("Use --msi, --validate, or --bundle --display <level>.");
            return 2;
        }

        try
        {
            PrerequisiteEngine.RefreshEnvironment();
            var results = PrerequisiteEngine.CheckAllAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            foreach (var result in results)
            {
                Console.WriteLine($"{result.Name}: {result.State}. {result.Detail}");
            }
            if (results.All(result => result.State == PrerequisiteState.Ready))
            {
                return 0;
            }
            Console.Error.WriteLine(
                "Prerequisites are not ready. Run setup.exe with full UI to review " +
                "Install/Update/Repair actions, or install prerequisites manually and retry. " +
                "Direct MSI and unattended setup never install dependencies or accept terms. " +
                "WorkIQ consent and account setup happen in Peggy on first run.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Peggy prerequisite check failed: {ex.Message}");
            return 1;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}

internal sealed record CopilotCandidateResolution(
    IReadOnlyList<string> Candidates,
    string? Error);

internal static class CopilotPrerequisite
{
    internal static CopilotCandidateResolution ResolveCandidates(
        string localAppData)
    {
        var configuredPath = Environment.GetEnvironmentVariable(
            "PEGGY_COPILOT_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string expanded;
            try
            {
                expanded = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(
                        configuredPath.Trim().Trim('"')));
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return new CopilotCandidateResolution(
                    [],
                    $"PEGGY_COPILOT_PATH is invalid: {ex.Message}");
            }

            return File.Exists(expanded)
                ? new CopilotCandidateResolution([expanded], null)
                : new CopilotCandidateResolution(
                    [],
                    "PEGGY_COPILOT_PATH does not identify copilot.exe.");
        }

        return new CopilotCandidateResolution(
            [
                "copilot.exe",
                "copilot",
                Path.Combine(
                    localAppData,
                    "Microsoft",
                    "WinGet",
                    "Links",
                    "copilot.exe"),
                Path.Combine(
                    localAppData,
                    "Microsoft",
                    "WindowsApps",
                    "copilot.exe")
            ],
            null);
    }
}
