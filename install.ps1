[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$InstallDirectory = (
        Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData
            )
        ) 'Programs\Peggy'
    ),
    [string]$Repository = 'didiergbenou-ms/peggy',
    [string]$PackagePath,
    [string]$ChecksumPath,
    [switch]$InstallPrerequisites,
    [switch]$InstallSpeechModels,
    [switch]$SkipSpeechModels,
    [switch]$NoShortcut,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$archiveName = 'peggy-win-x64.zip'
$checksumName = 'peggy-win-x64.sha256'
$minimumCopilotVersion = [Version]'1.0.81'
$temporaryDirectory = Join-Path (
    [IO.Path]::GetTempPath()
) ('peggy-install-' + [Guid]::NewGuid().ToString('N'))

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Refresh-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable(
        'Path',
        [EnvironmentVariableTarget]::Machine
    )
    $userPath = [Environment]::GetEnvironmentVariable(
        'Path',
        [EnvironmentVariableTarget]::User
    )
    $pathEntries = @(
        foreach ($pathValue in @($env:Path, $machinePath, $userPath)) {
            if ([string]::IsNullOrWhiteSpace($pathValue)) {
                continue
            }
            foreach ($entry in $pathValue.Split(';')) {
                $trimmed = $entry.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                    $trimmed
                }
            }
        }
    ) | Select-Object -Unique
    $env:Path = $pathEntries -join ';'
}

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string[]]$Name)

    foreach ($candidate in $Name) {
        if ($null -ne (Get-Command $candidate -ErrorAction SilentlyContinue)) {
            return $true
        }
    }
    return $false
}

function Resolve-CommandPath {
    param([Parameter(Mandatory)][string[]]$Name)

    foreach ($candidate in $Name) {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $command -and
            -not [string]::IsNullOrWhiteSpace($command.Source)) {
            return $command.Source
        }
    }
    return $null
}

function Get-ExecutableVersion {
    param(
        [Parameter(Mandatory)][string[]]$Command,
        [Parameter(Mandatory)][string[]]$Argument
    )

    $commandPath = Resolve-CommandPath -Name $Command
    if ([string]::IsNullOrWhiteSpace($commandPath)) {
        return $null
    }

    try {
        $output = (& $commandPath @Argument 2>$null | Out-String).Trim()
    } catch {
        return $null
    }
    $match = [Text.RegularExpressions.Regex]::Match(
        $output,
        '\d+(?:\.\d+){1,3}'
    )
    if (-not $match.Success) {
        return $null
    }

    try {
        return [Version]$match.Value
    } catch {
        return $null
    }
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$DisplayName,
        [switch]$Upgrade
    )

    if (-not (Test-CommandAvailable -Name @('winget.exe', 'winget'))) {
        throw "WinGet is required to install $DisplayName automatically."
    }

    $verb = if ($Upgrade) { 'upgrade' } else { 'install' }
    $action = if ($Upgrade) { 'Upgrading' } else { 'Installing' }
    Write-Step "$action $DisplayName"
    & winget $verb `
        --id $Id `
        --exact `
        --silent `
        --accept-package-agreements `
        --accept-source-agreements `
        --disable-interactivity
    if ($LASTEXITCODE -ne 0 -and $Upgrade) {
        & winget install `
            --id $Id `
            --exact `
            --silent `
            --force `
            --accept-package-agreements `
            --accept-source-agreements `
            --disable-interactivity
    }
    if ($LASTEXITCODE -ne 0) {
        throw "WinGet could not install $DisplayName (exit $LASTEXITCODE)."
    }
    Refresh-ProcessPath
}

function Ensure-Prerequisite {
    param(
        [Parameter(Mandatory)][string[]]$Command,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$DisplayName
    )

    if (Test-CommandAvailable -Name $Command) {
        return
    }
    if (-not $InstallPrerequisites) {
        throw "$DisplayName is required. Install it first or rerun with " +
            '-InstallPrerequisites.'
    }

    Install-WingetPackage -Id $PackageId -DisplayName $DisplayName
    if (-not (Test-CommandAvailable -Name $Command)) {
        throw "$DisplayName was installed but is not available on PATH. " +
            'Open a new terminal and run this installer again.'
    }
}

function Ensure-MinimumPrerequisite {
    param(
        [Parameter(Mandatory)][string[]]$Command,
        [Parameter(Mandatory)][string[]]$VersionArgument,
        [Parameter(Mandatory)][Version]$MinimumVersion,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $installedVersion = Get-ExecutableVersion `
        -Command $Command `
        -Argument $VersionArgument
    if ($null -ne $installedVersion -and
        $installedVersion -ge $MinimumVersion) {
        return
    }

    $installedDescription = if ($null -eq $installedVersion) {
        'not installed or unreadable'
    } else {
        "version $installedVersion"
    }
    if (-not $InstallPrerequisites) {
        throw "$DisplayName $MinimumVersion or later is required " +
            "($installedDescription). Install or upgrade it first, or rerun " +
            'with -InstallPrerequisites.'
    }

    Install-WingetPackage `
        -Id $PackageId `
        -DisplayName $DisplayName `
        -Upgrade:($null -ne $installedVersion)
    $installedVersion = Get-ExecutableVersion `
        -Command $Command `
        -Argument $VersionArgument
    if ($null -eq $installedVersion -or
        $installedVersion -lt $MinimumVersion) {
        throw "$DisplayName was installed but version $MinimumVersion or " +
            'later is not available. Open a new terminal and retry.'
    }
}

function Ensure-CopilotPrerequisite {
    param([Parameter(Mandatory)][Version]$MinimumVersion)

    $command = @('copilot.exe', 'copilot')
    $versionArgument = @('--version')
    $installedVersion = Get-ExecutableVersion `
        -Command $command `
        -Argument $versionArgument
    if ($null -ne $installedVersion -and
        $installedVersion -ge $MinimumVersion) {
        return
    }

    $installedDescription = if ($null -eq $installedVersion) {
        'not installed or unreadable'
    } else {
        "version $installedVersion"
    }
    if (-not $InstallPrerequisites) {
        throw "GitHub Copilot CLI $MinimumVersion or later is required " +
            "($installedDescription). Peggy monitoring depends on " +
            "'--usage-output-file'. Install or upgrade Copilot CLI first, " +
            'or rerun with -InstallPrerequisites.'
    }

    if ($null -eq $installedVersion) {
        Ensure-Prerequisite `
            -Command $command `
            -PackageId 'GitHub.Copilot' `
            -DisplayName 'GitHub Copilot CLI'
        $installedVersion = Get-ExecutableVersion `
            -Command $command `
            -Argument $versionArgument
    }

    if ($null -eq $installedVersion -or
        $installedVersion -lt $MinimumVersion) {
        $commandPath = Resolve-CommandPath -Name $command
        if ([string]::IsNullOrWhiteSpace($commandPath)) {
            throw 'GitHub Copilot CLI was installed but is unavailable on PATH.'
        }

        Write-Step "Updating GitHub Copilot CLI to $MinimumVersion or later"
        & $commandPath update stable
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub Copilot CLI update failed (exit $LASTEXITCODE)."
        }
        Refresh-ProcessPath
        $installedVersion = Get-ExecutableVersion `
            -Command $command `
            -Argument $versionArgument
    }

    if ($null -eq $installedVersion -or
        $installedVersion -lt $MinimumVersion) {
        throw "GitHub Copilot CLI $MinimumVersion or later is required. " +
            'Install the latest stable release and run this installer again.'
    }
}

function Assert-InstallDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($fullPath) -or $fullPath -eq $root) {
        throw 'Peggy cannot be installed at a filesystem root.'
    }

    $protectedPaths = @(
        $env:USERPROFILE,
        $env:LOCALAPPDATA,
        $env:APPDATA,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)}
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($protectedPath in $protectedPaths) {
        if ($fullPath -ieq (
                [IO.Path]::GetFullPath($protectedPath).TrimEnd('\', '/')
            )) {
            throw "Peggy cannot replace protected directory '$fullPath'."
        }
    }
    return $fullPath
}

function Get-GitHubHeaders {
    param([string]$Accept = 'application/vnd.github+json')

    $headers = @{
        Accept = $Accept
        'User-Agent' = 'Peggy-Installer'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $token = $env:GH_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $env:GITHUB_TOKEN
    }
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $headers.Authorization = "Bearer $token"
    }
    return $headers
}

function Save-GitHubReleasePackage {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Checksum
    )

    $escapedRepository = $Repository.Trim('/')
    $releaseUrl = if ($Version -eq 'latest') {
        "https://api.github.com/repos/$escapedRepository/releases/latest"
    } else {
        $encodedVersion = [Uri]::EscapeDataString($Version)
        "https://api.github.com/repos/$escapedRepository/releases/tags/" +
            $encodedVersion
    }

    try {
        $headers = Get-GitHubHeaders
        $release = Invoke-RestMethod `
            -Uri $releaseUrl `
            -Headers $headers `
            -Method Get
        $archiveAsset = @($release.assets) |
            Where-Object { $_.name -eq $archiveName } |
            Select-Object -First 1
        $checksumAsset = @($release.assets) |
            Where-Object { $_.name -eq $checksumName } |
            Select-Object -First 1
        if ($null -eq $archiveAsset -or $null -eq $checksumAsset) {
            throw "Release '$($release.tag_name)' does not contain the " +
                'expected Windows package and checksum.'
        }

        $hasToken = $headers.ContainsKey('Authorization')
        $archiveUrl = if ($hasToken) {
            $archiveAsset.url
        } else {
            $archiveAsset.browser_download_url
        }
        $checksumUrl = if ($hasToken) {
            $checksumAsset.url
        } else {
            $checksumAsset.browser_download_url
        }
        $downloadHeaders = if ($hasToken) {
            Get-GitHubHeaders -Accept 'application/octet-stream'
        } else {
            @{
                'User-Agent' = 'Peggy-Installer'
            }
        }

        Invoke-WebRequest `
            -Uri $archiveUrl `
            -Headers $downloadHeaders `
            -OutFile $Archive `
            -UseBasicParsing
        Invoke-WebRequest `
            -Uri $checksumUrl `
            -Headers $downloadHeaders `
            -OutFile $Checksum `
            -UseBasicParsing
        return $release.tag_name
    } catch {
        $apiFailure = $_
        if (-not (Test-CommandAvailable -Name @('gh.exe', 'gh'))) {
            throw "Could not download $Repository release '$Version'. " +
                'For a private repository, set GH_TOKEN or install and ' +
                "authenticate GitHub CLI. GitHub API error: $apiFailure"
        }

        Write-Step 'Trying authenticated GitHub CLI release download'
        $arguments = @('release', 'download')
        if ($Version -ne 'latest') {
            $arguments += $Version
        }
        $arguments += @(
            '--repo', $Repository,
            '--pattern', 'peggy-win-x64*',
            '--dir', $Destination,
            '--clobber'
        )
        & gh @arguments
        if ($LASTEXITCODE -ne 0 -or
            -not (Test-Path -LiteralPath $Archive) -or
            -not (Test-Path -LiteralPath $Checksum)) {
            throw "Could not download private release '$Version' from " +
                "$Repository. Run 'gh auth login' with repository access or " +
                'set GH_TOKEN, then retry.'
        }
        return $Version
    }
}

function Assert-PackageHash {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Checksum
    )

    $checksumText = Get-Content -LiteralPath $Checksum -Raw
    $match = [Text.RegularExpressions.Regex]::Match(
        $checksumText,
        '(?i)\b[0-9a-f]{64}\b'
    )
    if (-not $match.Success) {
        throw "Checksum file '$Checksum' does not contain a SHA-256 digest."
    }

    $expected = $match.Value.ToUpperInvariant()
    $actual = (
        Get-FileHash -LiteralPath $Archive -Algorithm SHA256
    ).Hash.ToUpperInvariant()
    if ($actual -ne $expected) {
        throw 'Peggy package checksum verification failed.'
    }
}

function Stop-InstalledPeggy {
    param([Parameter(Mandatory)][string]$Executable)

    if (-not (Test-Path -LiteralPath $Executable)) {
        return
    }
    $expectedPath = [IO.Path]::GetFullPath($Executable)
    $installRoot = (
        [IO.Path]::GetDirectoryName($expectedPath).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    )
    $processes = Get-Process | Where-Object {
        try {
            -not [string]::IsNullOrWhiteSpace($_.Path) -and
                [IO.Path]::GetFullPath($_.Path) -ieq $expectedPath
        } catch {
            $false
        }
    }
    foreach ($process in $processes) {
        $managedHelperNames = @(
            'copilot.exe',
            'peggy-mcp.exe',
            'peggy-speech-host.exe',
            'workiq.exe'
        )
        $managedHelperIds = @(
            Get-CimInstance Win32_Process `
                -Filter "ParentProcessId = $($process.Id)" `
                -ErrorAction Stop |
                Where-Object {
                    $managedHelperNames -contains $_.Name
                } |
                Select-Object -ExpandProperty ProcessId
        )

        Write-Step "Stopping Peggy process $($process.Id)"
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        if (-not $process.WaitForExit(10000)) {
            throw "Peggy process $($process.Id) did not stop."
        }

        foreach ($helperId in $managedHelperIds) {
            $helper = Get-Process `
                -Id $helperId `
                -ErrorAction SilentlyContinue
            if ($null -ne $helper) {
                Write-Step "Stopping Peggy helper process $helperId"
                Stop-Process -Id $helperId -Force -ErrorAction Stop
                if (-not $helper.WaitForExit(10000)) {
                    throw "Peggy helper process $helperId did not stop."
                }
            }
        }
    }

    $installedHelpers = @(
        Get-Process | Where-Object {
            try {
                -not [string]::IsNullOrWhiteSpace($_.Path) -and
                    [IO.Path]::GetFullPath($_.Path).StartsWith(
                        $installRoot,
                        [StringComparison]::OrdinalIgnoreCase)
            } catch {
                $false
            }
        }
    )
    foreach ($helper in $installedHelpers) {
        Write-Step "Stopping Peggy helper process $($helper.Id)"
        Stop-Process -Id $helper.Id -Force -ErrorAction Stop
        if (-not $helper.WaitForExit(10000)) {
            throw "Peggy helper process $($helper.Id) did not stop."
        }
    }
    $remainingHelpers = @(
        Get-Process | Where-Object {
            try {
                -not [string]::IsNullOrWhiteSpace($_.Path) -and
                    [IO.Path]::GetFullPath($_.Path).StartsWith(
                        $installRoot,
                        [StringComparison]::OrdinalIgnoreCase)
            } catch {
                $false
            }
        }
    )
    if ($remainingHelpers.Count -gt 0) {
        throw 'Peggy helper processes are still using the install directory.'
    }
}

function New-PeggyShortcut {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $programs = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Programs
    )
    $shortcutPath = Join-Path $programs 'Peggy.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $Executable
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Description = 'Peggy Microsoft 365 assistant'
    $shortcut.Save()
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Peggy supports Windows only.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Peggy requires 64-bit Windows.'
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Invalid GitHub repository name '$Repository'."
}
if ($InstallSpeechModels -and $SkipSpeechModels) {
    throw 'InstallSpeechModels and SkipSpeechModels cannot be used together.'
}
$shouldInstallSpeechModels = -not $SkipSpeechModels

$InstallDirectory = Assert-InstallDirectory -Path $InstallDirectory
Refresh-ProcessPath

Ensure-MinimumPrerequisite `
    -Command @('pwsh.exe', 'pwsh') `
    -VersionArgument @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-Command',
        '$PSVersionTable.PSVersion.ToString()'
    ) `
    -MinimumVersion ([Version]'7.0') `
    -PackageId 'Microsoft.PowerShell' `
    -DisplayName 'PowerShell 7'
Ensure-MinimumPrerequisite `
    -Command @('node.exe', 'node') `
    -VersionArgument @('--version') `
    -MinimumVersion ([Version]'18.0') `
    -PackageId 'OpenJS.NodeJS.LTS' `
    -DisplayName 'Node.js LTS'
if (-not (Test-CommandAvailable -Name @('npm.cmd', 'npm'))) {
    throw 'Node.js is installed but npm is unavailable. Repair the Node.js ' +
        'installation and run this installer again.'
}
Ensure-CopilotPrerequisite -MinimumVersion $minimumCopilotVersion

New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
$stagingDirectory = $null
$backupDirectory = $null
try {
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $resolvedArchive = Join-Path $temporaryDirectory $archiveName
        $resolvedChecksum = Join-Path $temporaryDirectory $checksumName
        Write-Step "Downloading Peggy release $Version"
        $installedVersion = Save-GitHubReleasePackage `
            -Destination $temporaryDirectory `
            -Archive $resolvedArchive `
            -Checksum $resolvedChecksum
    } else {
        $resolvedArchive = (
            Resolve-Path -LiteralPath $PackagePath
        ).Path
        if ([string]::IsNullOrWhiteSpace($ChecksumPath)) {
            $ChecksumPath = Join-Path (
                Split-Path -Parent $resolvedArchive
            ) $checksumName
        }
        $resolvedChecksum = (
            Resolve-Path -LiteralPath $ChecksumPath
        ).Path
        $installedVersion = 'local package'
    }

    Write-Step 'Verifying package checksum'
    Assert-PackageHash `
        -Archive $resolvedArchive `
        -Checksum $resolvedChecksum

    $parentDirectory = Split-Path -Parent $InstallDirectory
    New-Item -ItemType Directory -Path $parentDirectory -Force | Out-Null
    $stagingDirectory = Join-Path $parentDirectory (
        '.peggy-stage-' + [Guid]::NewGuid().ToString('N')
    )
    Expand-Archive `
        -LiteralPath $resolvedArchive `
        -DestinationPath $stagingDirectory

    $payloadDirectory = $stagingDirectory
    $stagedExecutable = Join-Path $payloadDirectory 'peggy-tray.exe'
    if (-not (Test-Path -LiteralPath $stagedExecutable)) {
        $children = @(Get-ChildItem -LiteralPath $stagingDirectory -Directory)
        if ($children.Count -eq 1 -and
            (Test-Path -LiteralPath (
                Join-Path $children[0].FullName 'peggy-tray.exe'
            ))) {
            $payloadDirectory = $children[0].FullName
            $stagedExecutable = Join-Path $payloadDirectory 'peggy-tray.exe'
        }
    }

    $requiredFiles = @(
        'peggy-tray.exe',
        'Peggy.Mcp\peggy-mcp.exe',
        'Peggy.Speech.Host\peggy-speech-host.exe',
        'scripts\Install-SpeechModels.ps1',
        'scripts\Collect-PeggyDiagnostics.ps1'
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (
                Join-Path $payloadDirectory $requiredFile
            ))) {
            throw "Peggy package is missing '$requiredFile'."
        }
    }

    if ($shouldInstallSpeechModels) {
        $speechInstaller = Join-Path (
            Join-Path $payloadDirectory 'scripts'
        ) 'Install-SpeechModels.ps1'
        Write-Step 'Installing local voice models (about 803 MiB on first install)'
        & $speechInstaller
    }

    $installedExecutable = Join-Path $InstallDirectory 'peggy-tray.exe'
    Stop-InstalledPeggy -Executable $installedExecutable

    $hadExistingInstall = Test-Path -LiteralPath $InstallDirectory
    if ($hadExistingInstall) {
        $backupDirectory = Join-Path $parentDirectory (
            '.peggy-backup-' + [Guid]::NewGuid().ToString('N')
        )
        Move-Item `
            -LiteralPath $InstallDirectory `
            -Destination $backupDirectory
    }

    try {
        if ($payloadDirectory -ieq $stagingDirectory) {
            Move-Item `
                -LiteralPath $stagingDirectory `
                -Destination $InstallDirectory
            $stagingDirectory = $null
        } else {
            Move-Item `
                -LiteralPath $payloadDirectory `
                -Destination $InstallDirectory
        }
    } catch {
        if ($hadExistingInstall -and
            -not (Test-Path -LiteralPath $InstallDirectory) -and
            (Test-Path -LiteralPath $backupDirectory)) {
            Move-Item `
                -LiteralPath $backupDirectory `
                -Destination $InstallDirectory
            $backupDirectory = $null
        }
        throw
    }

    if ($null -ne $backupDirectory -and
        (Test-Path -LiteralPath $backupDirectory)) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
        $backupDirectory = $null
    }

    $installedExecutable = Join-Path $InstallDirectory 'peggy-tray.exe'
    if (-not $NoShortcut) {
        Write-Step 'Creating Start menu shortcut'
        New-PeggyShortcut `
            -Executable $installedExecutable `
            -WorkingDirectory $InstallDirectory
    }

    Write-Host ''
    Write-Host "Peggy $installedVersion installed at:" -ForegroundColor Green
    Write-Host "  $InstallDirectory"
    if (-not $NoLaunch) {
        Write-Step 'Starting Peggy'
        Start-Process -FilePath $installedExecutable
    } else {
        Write-Host "Start Peggy with: `"$installedExecutable`""
    }
    Write-Host ''
    Write-Host 'If Copilot CLI is not authenticated, run: copilot login'
    Write-Host 'Peggy handles WorkIQ installation and Microsoft 365 sign-in on first launch.'
} finally {
    if ($null -ne $stagingDirectory -and
        (Test-Path -LiteralPath $stagingDirectory)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
