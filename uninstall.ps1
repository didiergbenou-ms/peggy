[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$InstallDirectory,
    [switch]$PurgeData,
    [switch]$ForceRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-SafeDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Purpose
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Purpose cannot be empty."
    }

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($fullPath) -or $fullPath -eq $root) {
        throw "$Purpose cannot be a filesystem root."
    }

    $protectedPaths = @(
        $env:USERPROFILE,
        $env:LOCALAPPDATA,
        $env:APPDATA,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)}
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($protectedPath in $protectedPaths) {
        $resolvedProtectedPath = (
            [IO.Path]::GetFullPath($protectedPath).TrimEnd('\', '/')
        )
        if ($fullPath -ieq $resolvedProtectedPath) {
            throw "$Purpose cannot be protected directory '$fullPath'."
        }
    }

    return $fullPath
}

function Test-SamePath {
    param(
        [string]$Left,
        [string]$Right
    )

    if ([string]::IsNullOrWhiteSpace($Left) -or
        [string]::IsNullOrWhiteSpace($Right)) {
        return $false
    }

    try {
        return (
            [IO.Path]::GetFullPath($Left).TrimEnd('\', '/') -ieq
            [IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
        )
    } catch {
        return $false
    }
}

function Test-PathInsideDirectory {
    param(
        [string]$Path,
        [Parameter(Mandatory)][string]$Directory
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path)
        $directoryPrefix = (
            [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        )
        return $fullPath.StartsWith(
            $directoryPrefix,
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Get-StartupExecutable {
    param([string]$Command)

    if ([string]::IsNullOrWhiteSpace($Command)) {
        return $null
    }

    $match = [Text.RegularExpressions.Regex]::Match(
        $Command,
        '^\s*(?:"([^"]+)"|([^\s]+))'
    )
    if (-not $match.Success) {
        return $null
    }
    if (-not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
        return $match.Groups[1].Value
    }
    return $match.Groups[2].Value
}

function Get-PeggyProcesses {
    param([Parameter(Mandatory)][string]$Directory)

    $installedProcesses = @(
        Get-Process | Where-Object {
            try {
                Test-PathInsideDirectory -Path $_.Path -Directory $Directory
            } catch {
                $false
            }
        }
    )
    if ($installedProcesses.Count -eq 0) {
        return @()
    }

    $processSnapshot = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    $depthById = @{}
    foreach ($process in $installedProcesses) {
        $depthById[[int]$process.Id] = 0
    }

    do {
        $addedProcess = $false
        foreach ($process in $processSnapshot) {
            $processId = [int]$process.ProcessId
            $parentId = [int]$process.ParentProcessId
            if (-not $depthById.ContainsKey($processId) -and
                $depthById.ContainsKey($parentId)) {
                $depthById[$processId] = $depthById[$parentId] + 1
                $addedProcess = $true
            }
        }
    } while ($addedProcess)

    $managedHelperNames = @(
        'copilot.exe',
        'peggy-mcp.exe',
        'peggy-speech-host.exe',
        'workiq.exe'
    )
    $installedIds = @($installedProcesses | ForEach-Object { [int]$_.Id })
    $results = @(
        foreach ($process in $installedProcesses) {
            [pscustomobject]@{
                Id = [int]$process.Id
                Name = $process.ProcessName
                Path = $process.Path
                Depth = [int]$depthById[[int]$process.Id]
                Process = $process
            }
        }
    )
    foreach ($process in $processSnapshot) {
        $processId = [int]$process.ProcessId
        if ($installedIds -contains $processId -or
            -not $depthById.ContainsKey($processId) -or
            $managedHelperNames -notcontains $process.Name) {
            continue
        }

        $liveProcess = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $liveProcess) {
            $results += [pscustomobject]@{
                Id = $processId
                Name = $process.Name
                Path = $process.ExecutablePath
                Depth = [int]$depthById[$processId]
                Process = $liveProcess
            }
        }
    }

    return @($results | Sort-Object Depth -Descending)
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Peggy supports Windows only.'
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $scriptExecutable = Join-Path $PSScriptRoot 'peggy-tray.exe'
    $InstallDirectory = if (Test-Path `
            -LiteralPath $scriptExecutable `
            -PathType Leaf) {
        $PSScriptRoot
    } else {
        Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData
            )
        ) 'Programs\Peggy'
    }
}
$InstallDirectory = Resolve-SafeDirectory `
    -Path $InstallDirectory `
    -Purpose 'Peggy installation directory'
$installedExecutable = Join-Path $InstallDirectory 'peggy-tray.exe'
$dataDirectory = Resolve-SafeDirectory `
    -Path (
        Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData
            )
        ) 'Peggy'
    ) `
    -Purpose 'Peggy data directory'
$actionsSkipped = $false
$targetsFound = 0

if ((Test-Path -LiteralPath $InstallDirectory) -and
    -not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
    throw "Peggy installation path '$InstallDirectory' is not a directory."
}
if ((Test-Path -LiteralPath $InstallDirectory -PathType Container) -and
    -not (Test-Path -LiteralPath $installedExecutable -PathType Leaf) -and
    -not $ForceRemoval) {
    throw "Directory '$InstallDirectory' does not contain peggy-tray.exe. " +
        'Use -ForceRemoval only if this is a partial Peggy installation.'
}

$peggyProcesses = @(Get-PeggyProcesses -Directory $InstallDirectory)
foreach ($processInfo in $peggyProcesses) {
    $targetsFound++
    $target = "$($processInfo.Name) process $($processInfo.Id)"
    if ($PSCmdlet.ShouldProcess($target, 'Stop')) {
        Write-Step "Stopping $target"
        if (-not $processInfo.Process.HasExited) {
            Stop-Process `
                -Id $processInfo.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
        if (-not $processInfo.Process.WaitForExit(10000)) {
            throw "$target did not stop."
        }
    } else {
        $actionsSkipped = $true
    }
}

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runKey = Get-Item -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
if ($null -ne $runKey -and $runKey.GetValueNames() -contains 'Peggy') {
    $startupCommand = [string]$runKey.GetValue('Peggy')
    $startupExecutable = Get-StartupExecutable -Command $startupCommand
    if (Test-SamePath -Left $startupExecutable -Right $installedExecutable) {
        $targetsFound++
        if ($PSCmdlet.ShouldProcess(
                "$runKeyPath\Peggy",
                'Remove startup entry')) {
            Write-Step 'Removing startup entry'
            Remove-ItemProperty `
                -LiteralPath $runKeyPath `
                -Name 'Peggy' `
                -Force
        } else {
            $actionsSkipped = $true
        }
    } else {
        Write-Warning (
            'The Peggy startup entry targets another executable and was preserved.'
        )
    }
}

$programsDirectory = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Programs
)
$shortcutPath = Join-Path $programsDirectory 'Peggy.lnk'
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutTarget = $shell.CreateShortcut($shortcutPath).TargetPath
    if (Test-SamePath -Left $shortcutTarget -Right $installedExecutable) {
        $targetsFound++
        if ($PSCmdlet.ShouldProcess($shortcutPath, 'Remove Start menu shortcut')) {
            Write-Step 'Removing Start menu shortcut'
            Remove-Item -LiteralPath $shortcutPath -Force
        } else {
            $actionsSkipped = $true
        }
    } else {
        Write-Warning (
            'The Peggy Start menu shortcut targets another executable and was ' +
            'preserved.'
        )
    }
}

if (Test-Path -LiteralPath $InstallDirectory -PathType Container) {
    $targetsFound++
    if ($PSCmdlet.ShouldProcess(
            $InstallDirectory,
            'Remove Peggy installation directory')) {
        Write-Step 'Removing Peggy application files'
        $currentLocation = Get-Location
        if ($currentLocation.Provider.Name -eq 'FileSystem' -and
            ((Test-SamePath `
                    -Left $currentLocation.Path `
                    -Right $InstallDirectory) -or
                (Test-PathInsideDirectory `
                    -Path $currentLocation.Path `
                    -Directory $InstallDirectory))) {
            Set-Location -LiteralPath ([IO.Path]::GetTempPath())
        }
        Remove-Item -LiteralPath $InstallDirectory -Recurse -Force
    } else {
        $actionsSkipped = $true
    }
}

if ($PurgeData) {
    $accountVariable = 'PEGGY_WORKIQ_ACCOUNT'
    $savedAccount = [Environment]::GetEnvironmentVariable(
        $accountVariable,
        [EnvironmentVariableTarget]::User
    )
    if (-not [string]::IsNullOrWhiteSpace($savedAccount)) {
        $targetsFound++
        if ($PSCmdlet.ShouldProcess(
                "Current user environment variable $accountVariable",
                'Remove Peggy account preference')) {
            Write-Step 'Removing Peggy account preference'
            [Environment]::SetEnvironmentVariable(
                $accountVariable,
                $null,
                [EnvironmentVariableTarget]::User
            )
        } else {
            $actionsSkipped = $true
        }
    }

    if (Test-Path -LiteralPath $dataDirectory -PathType Container) {
        $targetsFound++
        if ($PSCmdlet.ShouldProcess(
                $dataDirectory,
                'Remove Peggy settings and speech models')) {
            Write-Step 'Removing Peggy settings and speech models'
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        } else {
            $actionsSkipped = $true
        }
    }

    $temporaryFiles = @(
        $logPath = Join-Path ([IO.Path]::GetTempPath()) 'peggy.log'
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            Get-Item -LiteralPath $logPath
        }
        Get-ChildItem `
            -LiteralPath ([IO.Path]::GetTempPath()) `
            -Filter 'peggy-*.json' `
            -File `
            -ErrorAction Stop
    )
    foreach ($temporaryFile in $temporaryFiles) {
        $targetsFound++
        if ($PSCmdlet.ShouldProcess(
                $temporaryFile.FullName,
                'Remove temporary Peggy file')) {
            Remove-Item -LiteralPath $temporaryFile.FullName -Force
        } else {
            $actionsSkipped = $true
        }
    }
}

Write-Host ''
if ($WhatIfPreference) {
    Write-Host 'Peggy uninstall preview completed; no changes were made.' `
        -ForegroundColor Green
} elseif ($actionsSkipped) {
    Write-Warning 'Peggy uninstall completed with one or more skipped actions.'
} elseif ($targetsFound -eq 0) {
    Write-Warning (
        "No Peggy installation was found at '$InstallDirectory'; no changes " +
        'were made.'
    )
} else {
    Write-Host 'Peggy uninstall completed.' -ForegroundColor Green
}
if (-not $PurgeData -and
    (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
    Write-Host "Settings and speech models were preserved at '$dataDirectory'."
    Write-Host 'Run uninstall.ps1 -PurgeData to remove them.'
}
Write-Host 'Shared prerequisites such as Copilot CLI and WorkIQ were not removed.'
