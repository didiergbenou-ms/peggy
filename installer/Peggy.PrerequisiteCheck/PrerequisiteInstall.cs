using System.Diagnostics;

namespace Peggy.PrerequisiteCheck;

internal static class PrerequisiteInstall
{
    internal static string OfficialUrl(string id) => id switch
    {
        "node" or "npm" => "https://nodejs.org/en/download",
        "powershell" => "https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows",
        "copilot" => "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    internal static string ActionName(PrerequisiteResult result) =>
        result.State == PrerequisiteState.Ready || result.Id == "npm" ||
        result.State == PrerequisiteState.Error ? "Repair" :
        result.Executable is null ? "Install" : "Update";

    internal static ProcessStartInfo CreateStartInfo(
        string winget, PrerequisiteResult result, bool explicitConsent,
        bool interactive, bool copilotOverride)
    {
        if (!interactive || !explicitConsent)
        {
            throw new InvalidOperationException("Dependency changes require a full-UI user action.");
        }
        if (result.Id == "copilot" && copilotOverride)
        {
            throw new InvalidOperationException(
                "Update the executable selected by PEGGY_COPILOT_PATH, or remove that override and retry.");
        }
        var package = result.Id switch
        {
            "node" or "npm" => "OpenJS.NodeJS.LTS",
            "powershell" => "Microsoft.PowerShell",
            "copilot" => "GitHub.Copilot",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        var action = ActionName(result);
        var info = new ProcessStartInfo(winget)
        {
            // Keep WinGet's terminal and vendor UI visible for progress and agreement prompts.
            // Do not supply acceptance flags, credentials, runas, or silent switches.
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal
        };
        foreach (var argument in new[]
        {
            action == "Repair" ? "repair" : action == "Update" ? "upgrade" : "install",
            "--id", package, "--exact", "--source", "winget", "--interactive"
        })
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static async Task<int> RunAsync(
        ProcessStartInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("WinGet did not start.");
        // Never kill a dependency installer mid-transaction. Cancellation while installing
        // belongs to the vendor's visible installer; setup waits for it to finish.
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
