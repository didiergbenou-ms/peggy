[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CheckerAssembly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$assembly = [Reflection.Assembly]::LoadFrom(
    (Resolve-Path -LiteralPath $CheckerAssembly).Path)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$script:assertions = 0

function Invoke-Helper {
    param([string]$Type, [string]$Method, [object[]]$Arguments)
    $methodInfo = $assembly.GetType("Peggy.PrerequisiteCheck.$Type").GetMethod($Method, $flags)
    if ($null -eq $methodInfo) { throw "Missing helper method: $Type.$Method" }
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($null -ne $Arguments[$index]) {
            $Arguments[$index] = $Arguments[$index].PSObject.BaseObject
        }
    }
    return $methodInfo.Invoke($null, $Arguments)
}

function Assert-Experience {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Installer experience test failed: $Message" }
    $script:assertions++
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Experience $rejected $Message
}

function Assert-Cancelled {
    param([scriptblock]$Action, [string]$Message)
    $cancelled = $false
    try { & $Action | Out-Null } catch {
        $cancelled = $_.Exception.GetBaseException() -is [OperationCanceledException]
    }
    Assert-Experience $cancelled $Message
}

foreach ($display in @('0', '1', '2', '3', '4', '5', 'invalid')) {
    $interactive = Invoke-Helper SetupMode IsInteractive @(
        ,[string[]]@('--bundle', '--display', $display))
    Assert-Experience ($interactive -eq ($display -eq '4')) "Burn display $display."
}
foreach ($arguments in @(
    ,@('--msi'),
    ,@('--msi', '--display', '4'),
    ,@('--validate'),
    ,@('--bundle'),
    ,@('--bundle', '--display', '4', '--quiet')
)) {
    Assert-Experience (-not (Invoke-Helper SetupMode IsInteractive @(,[string[]]$arguments))) `
        'Only the exact full-UI bundle invocation may show prerequisite actions.'
}
foreach ($display in @('2', '3', '4', '5')) {
    Assert-Experience ((Invoke-Helper InstallerLaunch IsAllowed @(
        ,[string[]]@('--launch-msi', '--display', $display, '--target', 'peggy-tray.exe')
    )) -eq ($display -eq '5')) "MSI launch display $display."
    foreach ($action in @('2', '4', '6', '7', '8')) {
        Assert-Experience ((Invoke-Helper InstallerLaunch IsAllowed @(
            ,[string[]]@('--launch-bundle', '--display', $display, '--action', $action,
                '--target', 'peggy-tray.exe')
        )) -eq ($display -eq '4' -and $action -eq '6')) `
            "Bundle launch display $display/action $action."
    }
}

$ready = Invoke-Helper PrerequisiteEngine EvaluateVersion @(
    'node', 'Node.js', 'C:\node.exe', 'v22.17.0', [Version]'18.0')
$old = Invoke-Helper PrerequisiteEngine EvaluateVersion @(
    'node', 'Node.js', 'C:\node.exe', 'v16.0.0', [Version]'18.0')
$broken = Invoke-Helper PrerequisiteEngine EvaluateVersion @(
    'node', 'Node.js', 'C:\node.exe', 'not a version', [Version]'18.0')
Assert-Experience ($ready.State.ToString() -eq 'Ready') 'Supported Node version.'
Assert-Experience ($old.State.ToString() -eq 'Missing') 'Old Node requires update.'
Assert-Experience ($broken.State.ToString() -eq 'Error') 'Malformed version is an error.'
foreach ($version in @('1.0.80', '1.0.81', '1.1.0')) {
    $result = Invoke-Helper PrerequisiteEngine EvaluateVersion @(
        'copilot', 'Copilot', 'C:\copilot.exe', "GitHub Copilot CLI $version",
        [Version]'1.0.81')
    Assert-Experience (($result.State.ToString() -eq 'Ready') -eq
        ([Version]$version -ge [Version]'1.0.81')) "Copilot version $version."
}

$missing = Invoke-Helper PrerequisiteEngine CheckAsync @(
    'node', 'Node.js', [string[]]@('C:\peggy-test-nonexistent\node.exe'),
    [string[]]@('--version'), [Version]'18.0', [Threading.CancellationToken]::None)
$missing = $missing.GetAwaiter().GetResult()
$install = Invoke-Helper PrerequisiteInstall CreateStartInfo @(
    'winget.exe', $missing, $true, $true, $false)
$update = Invoke-Helper PrerequisiteInstall CreateStartInfo @(
    'winget.exe', $old, $true, $true, $false)
$repair = Invoke-Helper PrerequisiteInstall CreateStartInfo @(
    'winget.exe', $ready, $true, $true, $false)
Assert-Experience ($install.ArgumentList[0] -eq 'install') 'Missing Node offers install.'
Assert-Experience ($update.ArgumentList[0] -eq 'upgrade') 'Old Node offers upgrade.'
Assert-Experience ($repair.ArgumentList[0] -eq 'repair') 'Installed Node offers repair.'
foreach ($info in @($install, $update, $repair)) {
    $arguments = $info.ArgumentList -join ' '
    Assert-Experience ($arguments -match '--id OpenJS.NodeJS.LTS --exact --source winget --interactive') `
        'Node action uses the official exact package and visible vendor UI.'
    Assert-Experience ($arguments -notmatch 'accept-|silent|disable-interactivity|override') `
        'No silent action or agreement acceptance.'
    Assert-Experience ($info.UseShellExecute -and -not $info.CreateNoWindow -and
        [string]::IsNullOrEmpty($info.Verb)) 'WinGet uses current identity with visible prompts.'
}
foreach ($consent in @($false, $true)) {
    Assert-Rejected {
        Invoke-Helper PrerequisiteInstall CreateStartInfo @(
            'winget.exe', $missing, $consent, $false, $false)
    } 'Unattended mode must reject dependency installation.'
}
Assert-Rejected {
    Invoke-Helper PrerequisiteInstall CreateStartInfo @(
        'winget.exe', $missing, $false, $true, $false)
} 'Missing explicit consent must reject dependency installation.'
$copilot = Invoke-Helper PrerequisiteEngine EvaluateVersion @(
    'copilot', 'Copilot', 'C:\copilot.exe', '1.0.80', [Version]'1.0.81')
Assert-Rejected {
    Invoke-Helper PrerequisiteInstall CreateStartInfo @(
        'winget.exe', $copilot, $true, $true, $true)
} 'An explicit Copilot override cannot be fixed by installing a different copy.'

$scratch = Join-Path $repoRoot ('artifacts\installer-experience-' + [Guid]::NewGuid().ToString('N'))
$oldOverride = $env:PEGGY_COPILOT_PATH
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    $fake = Join-Path $scratch 'copilot.exe'
    Set-Content -LiteralPath $fake -Value 'test fixture; never execute'
    $env:PEGGY_COPILOT_PATH = '"' + $fake + '"'
    $resolution = Invoke-Helper CopilotPrerequisite ResolveCandidates @($scratch)
    Assert-Experience ($null -eq $resolution.Error -and $resolution.Candidates.Count -eq 1 -and
        $resolution.Candidates[0] -eq $fake) 'The configured override must take exclusive precedence.'
    $env:PEGGY_COPILOT_PATH = Join-Path $scratch 'missing.exe'
    $resolution = Invoke-Helper CopilotPrerequisite ResolveCandidates @($scratch)
    Assert-Experience ($resolution.Candidates.Count -eq 0 -and $null -ne $resolution.Error) `
        'Missing override must not fall back to another executable.'
    $env:PEGGY_COPILOT_PATH = $null
    $resolution = Invoke-Helper CopilotPrerequisite ResolveCandidates @($scratch)
    Assert-Experience ($resolution.Candidates[0] -eq 'copilot.exe' -and
        $resolution.Candidates.Count -gt 1) 'Normal detection includes PATH and WinGet links.'

    $npm = Join-Path $scratch 'npm.cmd'
    Set-Content -LiteralPath $npm -Value "@echo off`r`necho 10.8.2" -Encoding ascii
    $result = Invoke-Helper PrerequisiteEngine CheckAsync @(
        'npm', 'npm', [string[]]@($npm), [string[]]@('--version'), [Version]'0.0',
        [Threading.CancellationToken]::None)
    $result = $result.GetAwaiter().GetResult()
    Assert-Experience ($result.State.ToString() -eq 'Ready' -and
        $result.Version -eq [Version]'10.8.2') 'npm.cmd is executed, not merely found.'
    $info = Invoke-Helper PrerequisiteInstall CreateStartInfo @(
        'winget.exe', $result, $true, $true, $false)
    Assert-Experience (($info.ArgumentList -join ' ') -match '^repair --id OpenJS.NodeJS.LTS') `
        'npm repair uses the Node.js package.'
    Set-Content -LiteralPath $npm -Value "@echo off`r`nexit /b 1" -Encoding ascii
    $result = Invoke-Helper PrerequisiteEngine CheckAsync @(
        'npm', 'npm', [string[]]@($npm), [string[]]@('--version'), [Version]'0.0',
        [Threading.CancellationToken]::None)
    Assert-Experience ($result.GetAwaiter().GetResult().State.ToString() -eq 'Error') `
        'A broken npm shim blocks installation.'

    $cancel = [Threading.CancellationTokenSource]::new()
    try {
        $cancel.Cancel()
        $neverInstall = [Diagnostics.ProcessStartInfo]::new(
            (Join-Path $scratch 'never-execute-a-real-installer.exe'))
        Assert-Cancelled {
            $task = Invoke-Helper PrerequisiteInstall RunAsync @($neverInstall, $cancel.Token)
            $task.GetAwaiter().GetResult()
        } 'Cancellation before consented execution must not start WinGet.'
        Assert-Cancelled {
            $task = Invoke-Helper PrerequisiteEngine CheckAllAsync @($cancel.Token, $null)
            $task.GetAwaiter().GetResult()
        } 'Cancelled checks do not execute processes.'
    } finally { $cancel.Dispose() }
    Set-Content -LiteralPath $npm -Value "@echo off`r`nping -n 30 127.0.0.1 >nul`r`necho 10.8.2" -Encoding ascii
    $cancel = [Threading.CancellationTokenSource]::new()
    try {
        $cancel.CancelAfter(400)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Assert-Cancelled {
            $task = Invoke-Helper PrerequisiteEngine CheckAsync @(
                'npm', 'npm', [string[]]@($npm), [string[]]@('--version'), [Version]'0.0', $cancel.Token)
            $task.GetAwaiter().GetResult()
        } 'Cancellation stops an in-flight version probe.'
        Assert-Experience ($watch.Elapsed.TotalSeconds -lt 10) 'Cancelled probes do not wait for their timeout.'
    } finally { $cancel.Dispose() }
} finally {
    $env:PEGGY_COPILOT_PATH = $oldOverride
    Remove-Item -LiteralPath $scratch -Recurse -Force
}

$merged = Invoke-Helper PrerequisiteEngine MergePaths @(
    ,[string[]]@('C:\Machine;C:\Shared', 'C:\User;C:\shared', 'C:\Inherited'))
Assert-Experience ($merged -eq 'C:\Machine;C:\Shared;C:\User;C:\Inherited') `
    'Environment refresh preserves paths while removing duplicates.'

# Construct and close the real WPF window on an STA without showing it
# or running Loaded. This catches missing theme resources without real installs.
if (-not ('PeggyInstallerUiProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public static class PeggyInstallerUiProbe {
    public static void Run(Assembly assembly) {
        Exception failure = null;
        var thread = new Thread(() => {
            try {
                var type = assembly.GetType("Peggy.PrerequisiteCheck.PrerequisiteWindow", true);
                var window = Activator.CreateInstance(type, true);
                type.GetMethod("Close").Invoke(window, null);
            } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Prerequisite UI failed to load.", failure);
    }
}
'@
}
[PeggyInstallerUiProbe]::Run($assembly)
Assert-Experience $true 'WPF prerequisite window and Peggy theme load.'
Write-Output "Peggy installer experience: $script:assertions synthetic assertions passed; no dependency installs or app launches."
