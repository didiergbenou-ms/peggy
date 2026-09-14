[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::DesktopDirectory
        )
    ),
    [switch]$RunConnectivityTests,
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string]$EmailTo = 'didiergbenou@microsoft.com',
    [switch]$NoEmail,
    [switch]$SendWithoutPrompt,
    [ValidateRange(100, 10000)]
    [int]$LogTailLines = 2000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Protect-Text {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }
    $protected = $Text
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $protected = $protected -replace (
            [Text.RegularExpressions.Regex]::Escape($env:USERPROFILE)
        ), '%USERPROFILE%'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
        $protected = $protected -replace (
            [Text.RegularExpressions.Regex]::Escape($env:USERNAME)
        ), '%USERNAME%'
    }
    $protected = $protected -replace (
        '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    ), '[email]'
    $protected = $protected -replace (
        '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-' +
        '[0-9a-f]{4}-[0-9a-f]{12}\b'
    ), '[guid]'
    $protected = $protected -replace 'https?://\S+', '[url]'
    $protected = $protected -replace (
        '\b[A-Za-z0-9_-]{64,}={0,2}\b'
    ), '[opaque-id]'
    return $protected
}

function Write-TextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowEmptyString()][string]$Content
    )

    [IO.File]::WriteAllText(
        $Path,
        $Content,
        (New-Object Text.UTF8Encoding($false))
    )
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    Write-TextFile `
        -Path $Path `
        -Content ($Value | ConvertTo-Json -Depth 8)
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($stream)
        ).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Resolve-CommandPath {
    param([Parameter(Mandatory)][string[]]$Names)

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
                return $command.Source
            }
            if (-not [string]::IsNullOrWhiteSpace($command.Path)) {
                return $command.Path
            }
        }
    }
    return $null
}

function Resolve-DiagnosticDataPath {
    param(
        [Parameter(Mandatory)][string]$DefaultPath,
        [Parameter(Mandatory)][string]$OverrideVariable,
        [Parameter(Mandatory)]
        [ValidateSet('Leaf', 'Container')]
        [string]$ExpectedType
    )

    $configuredPath = [Environment]::GetEnvironmentVariable(
        $OverrideVariable
    )
    $path = $DefaultPath
    $displayPath = Protect-Text $DefaultPath
    if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
        $displayPath = Protect-Text $configuredPath
        try {
            $path = [IO.Path]::GetFullPath(
                [Environment]::ExpandEnvironmentVariables(
                    $configuredPath.Trim().Trim('"')
                )
            )
            $displayPath = Protect-Text $path
        }
        catch {
            return [pscustomobject]@{
                Path = $null
                DisplayPath = $displayPath
                Error = "$OverrideVariable contains an invalid path."
            }
        }
    }

    try {
        if ((Test-Path -LiteralPath $path) -and
            -not (Test-Path -LiteralPath $path -PathType $ExpectedType)) {
            $expectedDescription = if ($ExpectedType -eq 'Leaf') {
                'file'
            } else {
                'directory'
            }
            return [pscustomobject]@{
                Path = $path
                DisplayPath = $displayPath
                Error = (
                    "$OverrideVariable does not identify a " +
                    "$expectedDescription."
                )
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Path = $null
            DisplayPath = $displayPath
            Error = "$OverrideVariable contains an invalid path."
        }
    }

    return [pscustomobject]@{
        Path = $path
        DisplayPath = $displayPath
        Error = $null
    }
}

function Invoke-DiagnosticCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Candidates,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$OverrideVariable,
        [string[]]$FallbackPaths = @()
    )

    $configuredPath = if ([string]::IsNullOrWhiteSpace(
            $OverrideVariable
        )) {
        $null
    } else {
        [Environment]::GetEnvironmentVariable($OverrideVariable)
    }
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        $path = Resolve-CommandPath -Names $Candidates
        if ([string]::IsNullOrWhiteSpace($path)) {
            foreach ($fallbackPath in $FallbackPaths) {
                try {
                    $candidate = [IO.Path]::GetFullPath(
                        [Environment]::ExpandEnvironmentVariables(
                            $fallbackPath
                        )
                    )
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                        $path = $candidate
                        break
                    }
                }
                catch {
                    continue
                }
            }
        }
    } else {
        try {
            $path = [IO.Path]::GetFullPath(
                [Environment]::ExpandEnvironmentVariables(
                    $configuredPath.Trim().Trim('"')
                )
            )
        }
        catch {
            return [pscustomobject]@{
                Name = $Name
                Found = $false
                Path = Protect-Text $configuredPath
                ExitCode = $null
                Output = "$OverrideVariable contains an invalid path."
            }
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return [pscustomobject]@{
                Name = $Name
                Found = $false
                Path = Protect-Text $path
                ExitCode = $null
                Output = "$OverrideVariable does not identify a file."
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        return [pscustomobject]@{
            Name = $Name
            Found = $false
            Path = $null
            ExitCode = $null
            Output = 'Command not found.'
        }
    }

    try {
        $output = (& $path @Arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            Name = $Name
            Found = $true
            Path = Protect-Text $path
            ExitCode = $exitCode
            Output = Protect-Text $output
        }
    }
    catch {
        return [pscustomobject]@{
            Name = $Name
            Found = $true
            Path = Protect-Text $path
            ExitCode = -1
            Output = Protect-Text $_.Exception.Message
        }
    }
}

function Send-DiagnosticEmail {
    param(
        [Parameter(Mandatory)][string]$AttachmentPath,
        [Parameter(Mandatory)][string]$Recipient,
        [Parameter(Mandatory)][string]$CollectedAt,
        [switch]$WithoutPrompt
    )

    if (-not $WithoutPrompt) {
        Write-Host ''
        Write-Host 'The ZIP contains redacted device and Peggy diagnostics.'
        Write-Host "Recipient: $Recipient"
        $confirmation = Read-Host (
            "Type SEND to email the diagnostic ZIP, or press Enter to keep " +
            'it locally'
        )
        if ($confirmation -cne 'SEND') {
            Write-Host 'Diagnostic email was not sent.'
            return $false
        }
    }

    $outlook = $null
    $mail = $null
    $attachments = $null
    $attachment = $null
    try {
        $outlook = New-Object -ComObject Outlook.Application
        $mail = $outlook.CreateItem(0)
        $mail.To = $Recipient
        $mail.Subject = "Peggy diagnostics $CollectedAt"
        $mail.Body = @(
            'Hello,'
            ''
            "Attached is a Peggy diagnostic bundle generated at $CollectedAt."
            ''
            'The collector excludes customer messages, tokens, encrypted ' +
                'state contents, and Copilot or WorkIQ session data.'
        ) -join [Environment]::NewLine
        $attachments = $mail.Attachments
        $attachment = $attachments.Add(
            (Resolve-Path -LiteralPath $AttachmentPath).Path
        )
        $mail.Send()
        Write-Host "Diagnostic ZIP emailed to $Recipient."
        return $true
    }
    catch {
        throw (
            "The diagnostic ZIP was created, but classic Outlook could not " +
            "email it to $Recipient. The ZIP remains at '$AttachmentPath'. " +
            "Error: $($_.Exception.Message)"
        )
    }
    finally {
        foreach ($comObject in @(
                $attachment,
                $attachments,
                $mail,
                $outlook
            )) {
            if ($null -ne $comObject -and
                [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
                    $comObject
                )
            }
        }
    }
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = (Get-Location).Path
}
$OutputDirectory = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($OutputDirectory)
)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$staging = Join-Path (
    [IO.Path]::GetTempPath()
) "Peggy-Diagnostics-$timestamp-$PID"
$zipPath = Join-Path $OutputDirectory "Peggy-Diagnostics-$timestamp.zip"
New-Item -ItemType Directory -Path $staging | Out-Null

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\Peggy'
$defaultStateRoot = Join-Path $env:LOCALAPPDATA 'Peggy'
$stateResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path $defaultStateRoot 'state.dat') `
    -OverrideVariable 'PEGGY_STATE_PATH' `
    -ExpectedType Leaf
$statePath = $stateResolution.Path
$modelsResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path $defaultStateRoot 'models') `
    -OverrideVariable 'PEGGY_SPEECH_MODELS' `
    -ExpectedType Container
$modelsRoot = $modelsResolution.Path
$manifestPath = if ($null -eq $modelsRoot) {
    $null
} else {
    Join-Path $modelsRoot 'speech-models.json'
}
$logResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path ([IO.Path]::GetTempPath()) 'peggy.log') `
    -OverrideVariable 'PEGGY_LOG_PATH' `
    -ExpectedType Leaf
$logPath = $logResolution.Path
$copilotFallbackPaths = @(
    Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\copilot.exe'
    Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\copilot.exe'
)
$workIqFallbackPaths = @(
    Join-Path (
        Join-Path $env:APPDATA 'npm'
    ) 'node_modules\@microsoft\workiq\bin\win-x64\workiq.exe'
)

try {
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    Write-JsonFile `
        -Path (Join-Path $staging 'system.json') `
        -Value ([ordered]@{
            CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            OsCaption = $os.Caption
            OsVersion = $os.Version
            BuildNumber = $os.BuildNumber
            OsArchitecture = $os.OSArchitecture
            ProcessArchitecture = (
                [Runtime.InteropServices.RuntimeInformation]::
                    ProcessArchitecture.ToString()
            )
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            PowerShellEdition = $PSVersionTable.PSEdition
            MemoryGiB = [Math]::Round(
                $computer.TotalPhysicalMemory / 1GB,
                1
            )
        })

    $installFiles = @()
    $coreHashes = @()
    if (Test-Path -LiteralPath $installRoot -PathType Container) {
        $allFiles = @(Get-ChildItem `
            -LiteralPath $installRoot `
            -Recurse `
            -File `
            -Force)
        $installFiles = foreach ($file in $allFiles) {
            [pscustomobject]@{
                RelativePath = $file.FullName.Substring(
                    $installRoot.Length + 1
                )
                Length = $file.Length
                ModifiedUtc = $file.LastWriteTimeUtc.ToString('O')
            }
        }
        $coreNames = @(
            'peggy-tray.exe',
            'peggy-tray.dll',
            'Peggy.Core.dll',
            'version.txt',
            'install.ps1',
            'Peggy.Mcp\peggy-mcp.exe',
            'Peggy.Speech.Host\peggy-speech-host.exe'
        )
        $coreHashes = foreach ($relativePath in $coreNames) {
            $file = Join-Path $installRoot $relativePath
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                [pscustomobject]@{
                    RelativePath = $relativePath
                    Length = (Get-Item -LiteralPath $file).Length
                    Sha256 = Get-Sha256 -Path $file
                }
            }
        }
    }
    $trayAssembly = Join-Path $installRoot 'peggy-tray.dll'
    $assemblyVersion = if (Test-Path -LiteralPath $trayAssembly) {
        [Reflection.AssemblyName]::GetAssemblyName(
            $trayAssembly
        ).Version.ToString()
    } else {
        $null
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'installation.json') `
        -Value ([ordered]@{
            InstallRoot = Protect-Text $installRoot
            Installed = Test-Path `
                -LiteralPath (Join-Path $installRoot 'peggy-tray.exe')
            Version = if (Test-Path (
                    Join-Path $installRoot 'version.txt'
                )) {
                (Get-Content `
                    -LiteralPath (Join-Path $installRoot 'version.txt') `
                    -Raw).Trim()
            } else {
                $null
            }
            AssemblyVersion = $assemblyVersion
            FileCount = $installFiles.Count
            Files = $installFiles
            CoreFileHashes = $coreHashes
        })

    $processNames = @(
        'peggy-tray.exe',
        'peggy-mcp.exe',
        'peggy-speech-host.exe',
        'copilot.exe',
        'workiq.exe'
    )
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in $processNames
    } | Select-Object `
        Name,
        ProcessId,
        ParentProcessId,
        @{Name = 'ExecutablePath'; Expression = {
            Protect-Text $_.ExecutablePath
        }})
    Write-JsonFile `
        -Path (Join-Path $staging 'processes.json') `
        -Value $processes

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runProperty = Get-ItemProperty `
        $runKey `
        -Name Peggy `
        -ErrorAction SilentlyContinue
    $runValue = if ($null -eq $runProperty) {
        $null
    } else {
        $runProperty.Peggy
    }
    $programs = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Programs
    )
    $installedApps = @()
    foreach ($root in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
        if (-not (Test-Path $root)) {
            continue
        }
        foreach ($key in Get-ChildItem $root) {
            $value = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $value -and
                $null -ne $value.PSObject.Properties['DisplayName'] -and
                $value.DisplayName -eq 'Peggy') {
                $displayVersion = $value.PSObject.Properties[
                    'DisplayVersion'
                ]
                $uninstallString = $value.PSObject.Properties[
                    'UninstallString'
                ]
                $installedApps += [pscustomobject]@{
                    RegistryRoot = $root
                    ProductCode = $key.PSChildName
                    DisplayVersion = if ($null -eq $displayVersion) {
                        $null
                    } else {
                        $displayVersion.Value
                    }
                    UninstallString = if ($null -eq $uninstallString) {
                        $null
                    } else {
                        Protect-Text $uninstallString.Value
                    }
                }
            }
        }
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'startup-and-registration.json') `
        -Value ([ordered]@{
            StartupValue = Protect-Text $runValue
            RootShortcutExists = Test-Path (
                Join-Path $programs 'Peggy.lnk'
            )
            FolderShortcutExists = Test-Path (
                Join-Path $programs 'Peggy\Peggy.lnk'
            )
            InstalledAppsEntries = $installedApps
        })

    $stateMetadata = [ordered]@{
        Exists = (
            $null -ne $statePath -and
            (Test-Path -LiteralPath $statePath -PathType Leaf)
        )
        Path = $stateResolution.DisplayPath
        ResolutionError = $stateResolution.Error
    }
    if ($stateMetadata.Exists) {
        $stateFile = Get-Item -LiteralPath $statePath
        $stateMetadata.Length = $stateFile.Length
        $stateMetadata.ModifiedUtc = $stateFile.LastWriteTimeUtc.ToString('O')
        $stateMetadata.Sha256 = Get-Sha256 -Path $statePath
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'state-metadata-only.json') `
        -Value $stateMetadata

    $modelFiles = @()
    if ($null -ne $modelsRoot -and
        (Test-Path -LiteralPath $modelsRoot -PathType Container)) {
        $modelFiles = @(Get-ChildItem `
            -LiteralPath $modelsRoot `
            -Recurse `
            -File `
            -Force | Select-Object `
                @{Name = 'RelativePath'; Expression = {
                    $_.FullName.Substring($modelsRoot.Length + 1)
                }},
                Length,
                @{Name = 'ModifiedUtc'; Expression = {
                    $_.LastWriteTimeUtc.ToString('O')
                }})
    }
    [long]$totalModelBytes = 0
    foreach ($modelFile in $modelFiles) {
        $totalModelBytes += $modelFile.Length
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'speech-models.json') `
        -Value ([ordered]@{
            RootPath = $modelsResolution.DisplayPath
            ResolutionError = $modelsResolution.Error
            ManifestExists = (
                $null -ne $manifestPath -and
                (Test-Path -LiteralPath $manifestPath -PathType Leaf)
            )
            FileCount = $modelFiles.Count
            TotalBytes = $totalModelBytes
            Files = $modelFiles
        })

    if ($null -ne $logPath -and
        (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        $logContent = (
            Get-Content `
                -LiteralPath $logPath `
                -Tail $LogTailLines `
                -ErrorAction Stop
        ) -join [Environment]::NewLine
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.txt') `
            -Content (Protect-Text $logContent)
    } else {
        $logDetail = if ([string]::IsNullOrWhiteSpace(
                $logResolution.Error
            )) {
            "Peggy log not found at $($logResolution.DisplayPath)."
        } else {
            $logResolution.Error
        }
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.txt') `
            -Content $logDetail
    }

    $scopeFiles = @(Get-ChildItem `
        -LiteralPath ([IO.Path]::GetTempPath()) `
        -File `
        -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -like 'peggy-*.json'
        } | Select-Object `
            Name,
            Length,
            @{Name = 'ModifiedUtc'; Expression = {
                $_.LastWriteTimeUtc.ToString('O')
            }})
    Write-JsonFile `
        -Path (Join-Path $staging 'temporary-scope-metadata-only.json') `
        -Value $scopeFiles

    $safeVariables = @(
        'PEGGY_MESSAGE_PROVIDER',
        'PEGGY_COPILOT_MODEL',
        'PEGGY_TEAMS_SCOPE',
        'PEGGY_TEAMS_MONITOR_CHATS',
        'PEGGY_LOG_PATH',
        'PEGGY_STATE_PATH',
        'PEGGY_COPILOT_PATH',
        'PEGGY_WORKIQ_PATH',
        'PEGGY_SPEECH_MODELS',
        'PEGGY_SPEECH_HOST_PATH',
        'PEGGY_SPEECH_MODEL_ROOT',
        'PEGGY_SPEECH_THREADS',
        'PEGGY_SPEECH_VOICE_ID',
        'PEGGY_SPEECH_SPEED',
        'PEGGY_SPEECH_INPUT_GAIN'
    )
    $sensitiveVariables = @(
        'PEGGY_WORKIQ_ACCOUNT',
        'COPILOT_GITHUB_TOKEN',
        'GH_TOKEN',
        'GITHUB_TOKEN'
    )
    $environmentStatus = @(
        foreach ($name in $safeVariables) {
            $value = [Environment]::GetEnvironmentVariable($name)
            [pscustomobject]@{
                Name = $name
                Present = -not [string]::IsNullOrWhiteSpace($value)
                Value = Protect-Text $value
            }
        }
        foreach ($name in $sensitiveVariables) {
            $value = [Environment]::GetEnvironmentVariable($name)
            [pscustomobject]@{
                Name = $name
                Present = -not [string]::IsNullOrWhiteSpace($value)
                Value = if ([string]::IsNullOrWhiteSpace($value)) {
                    $null
                } else {
                    '[redacted]'
                }
            }
        }
    )
    Write-JsonFile `
        -Path (Join-Path $staging 'environment-overrides.json') `
        -Value $environmentStatus

    $commands = @(
        Invoke-DiagnosticCommand `
            -Name 'PowerShell 7' `
            -Candidates @('pwsh.exe', 'pwsh') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'Node.js' `
            -Candidates @('node.exe', 'node') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'npm' `
            -Candidates @('npm.cmd', 'npm') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'GitHub Copilot CLI' `
            -Candidates @('copilot.exe', 'copilot') `
            -Arguments @('version') `
            -OverrideVariable 'PEGGY_COPILOT_PATH' `
            -FallbackPaths $copilotFallbackPaths
        Invoke-DiagnosticCommand `
            -Name 'Microsoft WorkIQ' `
            -Candidates @('workiq.exe', 'workiq.cmd', 'workiq') `
            -Arguments @('version') `
            -OverrideVariable 'PEGGY_WORKIQ_PATH' `
            -FallbackPaths $workIqFallbackPaths
        Invoke-DiagnosticCommand `
            -Name '.NET' `
            -Candidates @('dotnet.exe', 'dotnet') `
            -Arguments @('--info')
        Invoke-DiagnosticCommand `
            -Name 'curl' `
            -Candidates @('curl.exe') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'tar' `
            -Candidates @('tar.exe') `
            -Arguments @('--version')
    )
    Write-JsonFile `
        -Path (Join-Path $staging 'prerequisites.json') `
        -Value $commands

    $workIqConfig = Join-Path $env:USERPROFILE '.work-iq-cli\.workiq.json'
    $eulaMarkerPresent = $false
    if (Test-Path -LiteralPath $workIqConfig -PathType Leaf) {
        $configText = Get-Content -LiteralPath $workIqConfig -Raw
        $eulaMarkerPresent = $configText -match (
            '(?i)"?I-accept-EULA"?\s*:\s*true'
        )
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'workiq-config-metadata-only.json') `
        -Value ([ordered]@{
            ConfigExists = Test-Path -LiteralPath $workIqConfig
            EulaMarkerPresent = $eulaMarkerPresent
        })

    $eventResults = @()
    try {
        $eventResults = @(Get-WinEvent `
            -FilterHashtable @{
                LogName = 'Application'
                StartTime = (Get-Date).AddDays(-7)
            } `
            -MaxEvents 1000 `
            -ErrorAction Stop | Where-Object {
                $_.Message -match (
                    '(?i)\bPeggy\b|peggy-tray|peggy-mcp|' +
                    'peggy-speech-host'
                )
            } | Select-Object -First 100 | ForEach-Object {
                [pscustomobject]@{
                    TimeCreatedUtc = $_.TimeCreated.ToUniversalTime().
                        ToString('O')
                    ProviderName = $_.ProviderName
                    Id = $_.Id
                    Level = $_.LevelDisplayName
                    Message = Protect-Text $_.Message
                }
            })
    }
    catch {
        $eventResults = @(
            [pscustomobject]@{
                CollectionError = Protect-Text $_.Exception.Message
            }
        )
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'windows-application-events.json') `
        -Value $eventResults

    if ($RunConnectivityTests) {
        $previousCi = $env:CI
        try {
            $env:CI = 'true'
            $copilotTest = Invoke-DiagnosticCommand `
                -Name 'Copilot generic connectivity' `
                -Candidates @('copilot.exe', 'copilot') `
                -Arguments @(
                    '-p',
                    'Reply with OK only.',
                    '--silent',
                    '--disable-builtin-mcps',
                    '--no-custom-instructions',
                    '--no-remote',
                    '--no-remote-export'
                ) `
                -OverrideVariable 'PEGGY_COPILOT_PATH' `
                -FallbackPaths $copilotFallbackPaths
        }
        finally {
            $env:CI = $previousCi
        }
        $workIq = Invoke-DiagnosticCommand `
            -Name 'WorkIQ identity read' `
            -Candidates @('workiq.exe', 'workiq.cmd', 'workiq') `
            -Arguments @(
                'fetch',
                '--urls',
                '/me?$select=id',
                '--log-level',
                'Error'
            ) `
            -OverrideVariable 'PEGGY_WORKIQ_PATH' `
            -FallbackPaths $workIqFallbackPaths
        $workIqSucceeded = $workIq.ExitCode -eq 0 -and
            $workIq.Output -match '(?i)"id"'
        $connectivity = @(
            [pscustomobject]@{
                Name = $copilotTest.Name
                Found = $copilotTest.Found
                ExitCode = $copilotTest.ExitCode
                Succeeded = $copilotTest.ExitCode -eq 0 -and
                    $copilotTest.Output.Trim() -eq 'OK'
                Detail = $copilotTest.Output
            },
            [pscustomobject]@{
                Name = $workIq.Name
                Found = $workIq.Found
                ExitCode = $workIq.ExitCode
                Succeeded = $workIqSucceeded
                Detail = if ($workIqSucceeded) {
                    'Succeeded. Response content omitted.'
                } else {
                    $workIq.Output
                }
            }
        )
        Write-JsonFile `
            -Path (Join-Path $staging 'connectivity-tests.json') `
            -Value $connectivity
    } else {
        Write-TextFile `
            -Path (Join-Path $staging 'connectivity-tests.txt') `
            -Content (
                'Connectivity tests were not requested. Run the collector ' +
                'with -RunConnectivityTests to test Copilot and WorkIQ ' +
                'using generic, non-customer input.'
            )
    }

    Write-TextFile `
        -Path (Join-Path $staging 'README.txt') `
        -Content @"
Peggy diagnostic bundle

Collected:
- Operating system and PowerShell metadata
- Peggy version, installed file inventory, and selected file hashes
- Process, startup, shortcut, and Installed apps status
- Encrypted-state file metadata and hash only
- Speech-model inventory only
- Redacted Peggy lifecycle log
- Temporary Peggy scope-file metadata only
- Prerequisite versions
- Peggy-related Windows Application events
- Optional generic Copilot and WorkIQ connectivity results

Explicitly excluded:
- Peggy state.dat contents
- Customer messages, email bodies, Teams messages, files, or calendar data
- Copilot logs, session-state, prompts, responses, and command history
- WorkIQ tokens, cached authentication, account details, and config contents
- Environment token values
- Temporary Peggy focus/context/action file contents
- Speech model binaries

Review the ZIP before sharing it outside your support team.
"@

    Compress-Archive `
        -Path (Join-Path $staging '*') `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Host ''
Write-Host 'Peggy diagnostic ZIP created:'
Write-Host "  $zipPath"
Write-Host ''
Write-Host 'Review the ZIP before sharing it.'

if (-not $NoEmail) {
    [void](Send-DiagnosticEmail `
        -AttachmentPath $zipPath `
        -Recipient $EmailTo `
        -CollectedAt $timestamp `
        -WithoutPrompt:$SendWithoutPrompt)
}
