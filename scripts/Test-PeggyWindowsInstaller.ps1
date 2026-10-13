[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$ArtifactDirectory = (
        Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'
    ),

    [switch]$KeepExtracted,

    [switch]$SkipPrerequisiteExecution
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
. (Join-Path $PSScriptRoot 'PeggyMsiNative.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion -Version $Version
$assetNames = $versionInfo.AssetNames
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$publishRoot = Join-Path $artifactRoot 'peggy-win-x64'
$zipPath = Join-Path $artifactRoot $assetNames.Zip
$msiPath = Join-Path $artifactRoot $assetNames.Msi
$bundlePath = Join-Path $artifactRoot $assetNames.Bundle
$checkerPath = Join-Path (
    Join-Path $artifactRoot 'build\prerequisite-check'
) 'peggy-prerequisite-check.exe'
$setupRoot = Join-Path $artifactRoot 'build\setup-bootstrapper'
foreach ($path in @($publishRoot, $zipPath, $msiPath, $bundlePath, $setupRoot)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Installer validation input '$path' does not exist."
    }
}

function Assert-Installer {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw "Installer validation failed: $Message"
    }
}

function Get-IconFingerprint {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$Associated,
        [int]$Width,
        [int]$Height
    )

    $icon = if ($Associated) {
        [Drawing.Icon]::ExtractAssociatedIcon($Path)
    } else {
        [Drawing.Icon]::new($Path, $Width, $Height)
    }
    if ($null -eq $icon) {
        throw "No icon could be loaded from '$Path'."
    }
    try {
        $bitmap = $icon.ToBitmap()
        try {
            $pixels = [byte[]]::new($bitmap.Width * $bitmap.Height * 4)
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                for ($x = 0; $x -lt $bitmap.Width; $x++) {
                    $color = $bitmap.GetPixel($x, $y)
                    $offset = (($y * $bitmap.Width) + $x) * 4
                    $pixels[$offset] = $color.A
                    $pixels[$offset + 1] = $color.R
                    $pixels[$offset + 2] = $color.G
                    $pixels[$offset + 3] = $color.B
                }
            }
            $hash = [Security.Cryptography.SHA256]::HashData($pixels)
            return [pscustomobject]@{
                Width = $bitmap.Width
                Height = $bitmap.Height
                Hash = [Convert]::ToHexString($hash)
            }
        } finally {
            $bitmap.Dispose()
        }
    } finally {
        $icon.Dispose()
    }
}

function Get-MsiRows {
    param(
        [Parameter(Mandatory)]$Database,
        [Parameter(Mandatory)][string]$Query
    )

    $view = [PeggyMsiNative]::OpenView($Database, $Query)
    try {
        [PeggyMsiNative]::ExecuteView($view)
        $rows = @()
        while ($true) {
            $record = [IntPtr]::Zero
            if (-not [PeggyMsiNative]::Fetch($view, [ref]$record)) {
                break
            }
            $values = @()
            try {
                $fieldCount = [PeggyMsiNative]::FieldCount($record)
                for ($index = 1; $index -le $fieldCount; $index++) {
                    $values += [PeggyMsiNative]::RecordString(
                        $record,
                        $index)
                }
                $rows += ,$values
            } finally {
                [PeggyMsiNative]::Close($record)
            }
        }
        return $rows
    } finally {
        [PeggyMsiNative]::Close($view)
    }
}

function Get-MsiLongName {
    param([Parameter(Mandatory)][string]$Value)

    $targetName = $Value.Split(':', 2)[0]
    if ($targetName.Contains('|')) {
        return $targetName.Split('|', 2)[1]
    }
    return $targetName
}

function Get-RelativeMsiDirectory {
    param(
        [Parameter(Mandatory)][string]$DirectoryId,
        [Parameter(Mandatory)][hashtable]$Directories
    )

    $segments = [Collections.Generic.List[string]]::new()
    $current = $DirectoryId
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    while ($current -ne 'INSTALLFOLDER') {
        if (-not $seen.Add($current) -or
            -not $Directories.ContainsKey($current)) {
            throw "MSI directory '$DirectoryId' is not beneath INSTALLFOLDER."
        }
        $entry = $Directories[$current]
        $name = Get-MsiLongName -Value $entry.Name
        if ($name -ne '.') {
            $segments.Insert(0, $name)
        }
        $current = $entry.Parent
    }
    return $segments -join '\'
}

$versionOrder = @(
    '1.2.3-alpha.1',
    '1.2.3-alpha.29',
    '1.2.3-beta.1',
    '1.2.3-beta.30',
    '1.2.3-rc.1',
    '1.2.3-rc.39',
    '1.2.3',
    '1.2.4-alpha.1',
    '1.3.0-alpha.1',
    '2.0.0-alpha.1'
) | ForEach-Object {
    ConvertTo-PeggyInstallerVersion -Version $_
}
for ($index = 1; $index -lt $versionOrder.Count; $index++) {
    Assert-Installer `
        ([Version]$versionOrder[$index].ProductVersion -gt
            [Version]$versionOrder[$index - 1].ProductVersion) `
        'Prerelease MSI version mapping is not strictly ordered.'
    Assert-Installer `
        ([Version]$versionOrder[$index].FileVersion -gt
            [Version]$versionOrder[$index - 1].FileVersion) `
        'Release binary file versions are not strictly ordered.'
}
foreach ($orderedVersion in $versionOrder) {
    Assert-Installer `
        ($orderedVersion.FileVersion -eq "$($orderedVersion.ProductVersion).0") `
        'Binary file versions must follow the MSI version mapping.'
}
$repeatCode = (ConvertTo-PeggyInstallerVersion `
    -Version $versionInfo.SemanticVersion).ProductCode
Assert-Installer `
    ($repeatCode -eq $versionInfo.ProductCode) `
    'ProductCode generation is not deterministic.'
$invalidPrereleaseRejected = $false
try {
    ConvertTo-PeggyInstallerVersion -Version '1.2.3-preview' | Out-Null
} catch {
    $invalidPrereleaseRejected = $true
}
Assert-Installer `
    $invalidPrereleaseRejected `
    'An unordered prerelease version was accepted.'

foreach ($name in $assetNames.PSObject.Properties.Value) {
    Assert-Installer `
        (Test-Path -LiteralPath (Join-Path $artifactRoot $name) -PathType Leaf) `
        "Build artifact '$name' is missing."
}
foreach ($script in @(
    @{ Source = 'install.ps1'; Asset = $assetNames.InstallScript },
    @{ Source = 'uninstall.ps1'; Asset = $assetNames.UninstallScript }
)) {
    $sourceHash = (Get-FileHash `
        -LiteralPath (Join-Path $repoRoot $script.Source) -Algorithm SHA256).Hash
    foreach ($path in @(
        (Join-Path $artifactRoot $script.Asset),
        (Join-Path $publishRoot $script.Source)
    )) {
        Assert-Installer `
            ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sourceHash) `
            "Staged script '$path' differs from the canonical source."
    }
    Assert-Installer `
        (-not (Test-Path -LiteralPath (Join-Path $publishRoot $script.Asset))) `
        'Installed scripts must retain their canonical unversioned names.'
}

$binaryNamePattern = '^(Peggy\..*|peggy-.*)\.(dll|exe)$'
$peggyBinaries = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
    Where-Object { $_.Name -match $binaryNamePattern })
foreach ($requiredBinary in @(
        'peggy-tray.exe', 'peggy-tray.dll', 'Peggy.Core.dll',
        'Peggy.Mcp\peggy-mcp.exe', 'Peggy.Mcp\peggy-mcp.dll',
        'Peggy.Mcp\Peggy.Core.dll',
        'Peggy.Speech.Host\peggy-speech-host.exe',
        'Peggy.Speech.Host\peggy-speech-host.dll',
        'Peggy.Speech.Host\Peggy.Core.dll'
    )) {
    Assert-Installer `
        (Test-Path -LiteralPath (Join-Path $publishRoot $requiredBinary) -PathType Leaf) `
        "Release binary '$requiredBinary' is missing."
}
$helperBinaries = @(
    Get-Item -LiteralPath $checkerPath
    Get-Item -LiteralPath (Join-Path $artifactRoot 'build\uninstall-cleanup\peggy-uninstall-cleanup.exe')
)
foreach ($binary in @($peggyBinaries) + @($helperBinaries)) {
    Assert-Installer `
        ($binary.VersionInfo.FileVersion -eq $versionInfo.FileVersion) `
        "Release binary '$($binary.FullName)' does not have FileVersion $($versionInfo.FileVersion)."
}

$database = [PeggyMsiNative]::OpenDatabase([string]$msiPath, 0)
try {
    $summary = [PeggyMsiNative]::OpenSummary($database, 0)
    try {
        Assert-Installer `
            ([PeggyMsiNative]::SummaryProperty($summary, 7) -eq 'x64;1033' -and
                [int][PeggyMsiNative]::SummaryProperty($summary, 14) -eq 500) `
            'MSI summary is not Windows x64 with InstallerVersion 500.'
    } finally {
        [PeggyMsiNative]::Close($summary)
    }
    $properties = @{}
    foreach ($row in Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Property`,`Value` FROM `Property`') {
        $properties[$row[0]] = $row[1]
    }
    Assert-Installer `
        ($properties['ProductName'] -eq 'Peggy') `
        'ProductName is not Peggy.'
    Assert-Installer `
        ($properties['Manufacturer'] -eq 'Didier Gbenou') `
        'Manufacturer is not deterministic.'
    Assert-Installer `
        ($properties['ProductVersion'] -eq $versionInfo.ProductVersion) `
        "ProductVersion is not $($versionInfo.ProductVersion)."
    Assert-Installer `
        ($properties['ProductCode'] -eq $versionInfo.ProductCode) `
        'ProductCode is not deterministic for the semantic version.'
    Assert-Installer `
        ($properties['UpgradeCode'] -eq
            '{D661EFB2-155E-51B2-9951-C35E621D9E11}') `
        'UpgradeCode changed.'
    Assert-Installer `
        (-not $properties.ContainsKey('ALLUSERS')) `
        'ALLUSERS must be absent for a per-user package.'
    Assert-Installer `
        ($properties['ARPNOMODIFY'] -eq '1' -and
            $properties['ARPNOREPAIR'] -eq '1' -and
            $properties['ARPPRODUCTICON'] -eq 'Peggy.exe' -and
            -not [string]::IsNullOrWhiteSpace($properties['ARPHELPLINK'])) `
        'ARP metadata is incomplete.'
    Assert-Installer `
        (-not $properties.ContainsKey('MSIRESTARTMANAGERCONTROL')) `
        'Restart Manager must remain enabled.'
    Assert-Installer `
        (-not $properties.ContainsKey('PEGGY_BUNDLE') -and
            -not $properties.ContainsKey('PEGGY_LAUNCH')) `
        'The MSI must not default to a launch opt-in or bootstrapper context.'

    $directories = @{}
    foreach ($row in Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`') {
        $directories[$row[0]] = @{
            Parent = $row[1]
            Name = $row[2]
        }
    }
    Assert-Installer `
        ($directories['INSTALLFOLDER'].Parent -eq 'LocalProgramsFolder' -and
            $directories['INSTALLFOLDER'].Name -eq 'Peggy' -and
            $directories['LocalProgramsFolder'].Parent -eq
                'LocalAppDataFolder' -and
            $directories['LocalProgramsFolder'].Name -eq 'Programs') `
        'INSTALLFOLDER is not LocalAppDataFolder\Programs\Peggy.'

    $icons = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `Name` FROM `Icon`')
    Assert-Installer `
        ($icons.Count -eq 1 -and $icons[0][0] -eq 'Peggy.exe') `
        'The MSI does not contain Peggy''s product icon.'

    $shortcuts = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `Directory_`,`Name`,`Target`,`WkDir`,`Icon_`,`IconIndex` FROM `Shortcut`')
    Assert-Installer `
        ($shortcuts.Count -eq 1 -and
            $shortcuts[0][0] -eq 'ProgramMenuFolder' -and
            (Get-MsiLongName $shortcuts[0][1]) -eq 'Peggy' -and
            $shortcuts[0][2] -eq '[INSTALLFOLDER]peggy-tray.exe' -and
            $shortcuts[0][3] -eq 'INSTALLFOLDER' -and
            $shortcuts[0][4] -eq 'Peggy.exe' -and
            [int]$shortcuts[0][5] -eq 0) `
        'The current-user Start menu shortcut is incorrect.'

    $upgradeRows = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Attributes`,`ActionProperty` FROM `Upgrade`')
    Assert-Installer `
        ($upgradeRows.Count -eq 2 -and
            @($upgradeRows | Where-Object {
                $_[0] -eq '{D661EFB2-155E-51B2-9951-C35E621D9E11}' -and
                $_[4] -eq 'WIX_DOWNGRADE_DETECTED'
            }).Count -eq 1) `
        'Major-upgrade or downgrade detection is missing.'

    $sequence = @{}
    foreach ($row in Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`') {
        $sequence[$row[0]] = @{
            Condition = $row[1]
            Number = [int]$row[2]
        }
    }
    Assert-Installer `
        ($sequence['RemoveExistingProducts'].Number -gt
            $sequence['InstallInitialize'].Number -and
            $sequence['RemoveExistingProducts'].Number -lt
                $sequence['ProcessComponents'].Number) `
        'RemoveExistingProducts must replace the old payload inside the transaction before installing new components.'
    Assert-Installer `
        ($sequence['CheckPeggyPrerequisites'].Condition -eq
            'NOT Installed AND NOT ACTION~="ADMIN"' -and
            $sequence['CheckPeggyPrerequisites'].Number -lt
                $sequence['InstallInitialize'].Number) `
        'The MSI prerequisite check is not safely conditioned before installation.'
    $userDataCondition = 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE'
    $launchCondition = (
        'UILevel = 5 AND NOT PEGGY_BUNDLE AND NOT Installed AND NOT REMOVE AND ' +
        'NOT REINSTALL AND NOT UPGRADINGPRODUCTCODE AND NOT ACTION~="ADMIN"'
    )
    Assert-Installer `
        ($sequence['LaunchPeggyAfterInstall'].Condition -eq $launchCondition -and
            $sequence['LaunchPeggyAfterInstall'].Number -gt
                $sequence['InstallFinalize'].Number) `
        'Auto-launch must follow commit and exclude quiet/passive, Burn, maintenance, uninstall and admin images.'
    Assert-Installer `
        ($sequence['RemovePeggyUserData'].Condition -eq
            $userDataCondition -and
            $sequence['RemovePeggyUserData'].Number -gt
                $sequence['InstallFinalize'].Number) `
        'User-data cleanup is not safely limited to a committed true uninstall.'
    $startupCondition = (
        'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND ' +
        'PEGGY_RUN_VALUE = EXPECTED_PEGGY_RUN_VALUE'
    )
    Assert-Installer `
        ($sequence['RemoveOwnedPeggyStartupEntry'].Condition -eq
            $startupCondition -and
            $sequence['RemoveOwnedPeggyStartupEntry'].Number -lt
                $sequence['RemoveFiles'].Number) `
        'Startup cleanup is not safely conditioned before file removal.'
    Assert-Installer `
        ($sequence['RollbackOwnedPeggyStartupEntry'].Condition -eq
            $startupCondition -and
            $sequence['RollbackOwnedPeggyStartupEntry'].Number -lt
                $sequence['RemoveOwnedPeggyStartupEntry'].Number) `
        'Startup cleanup does not have a safely ordered rollback action.'

    $customActions = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`')
    $startupAction = @($customActions | Where-Object {
        $_[0] -eq 'RemoveOwnedPeggyStartupEntry'
    })
    $startupRollback = @($customActions | Where-Object {
        $_[0] -eq 'RollbackOwnedPeggyStartupEntry'
    })
    $prerequisiteAction = @($customActions | Where-Object {
        $_[0] -eq 'CheckPeggyPrerequisites'
    })
    Assert-Installer `
        ($prerequisiteAction.Count -eq 1 -and
            [int]$prerequisiteAction[0][1] -eq 2 -and
            $prerequisiteAction[0][3] -eq '--msi') `
        'The MSI does not execute its embedded prerequisite checker.'
    $userDataAction = @($customActions | Where-Object {
        $_[0] -eq 'RemovePeggyUserData'
    })
    $launchAction = @($customActions | Where-Object {
        $_[0] -eq 'LaunchPeggyAfterInstall'
    })
    Assert-Installer `
        ($launchAction.Count -eq 1 -and
            [int]$launchAction[0][1] -eq 66 -and
            $launchAction[0][2] -eq 'PeggyPrerequisiteChecker' -and
            $launchAction[0][3] -eq
                '--launch-msi --display [UILevel] --target "[INSTALLFOLDER]peggy-tray.exe"') `
        'Post-commit launch must be non-fatal, immediate, impersonated and use the identity-checking helper.'
    Assert-Installer `
        ($userDataAction.Count -eq 1 -and
            (([int]$userDataAction[0][1] -band 64) -ne 0) -and
            (([int]$userDataAction[0][1] -band 512) -eq 0) -and
            (([int]$userDataAction[0][1] -band 1024) -eq 0) -and
            (([int]$userDataAction[0][1] -band 2048) -eq 0) -and
            $userDataAction[0][3] -eq '--msi') `
        'The non-fatal post-commit current-user cleanup action is missing from the MSI.'
    $binaries = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `Name` FROM `Binary`')
    Assert-Installer `
        (@($binaries | Where-Object {
            $_[0] -eq 'PeggyPrerequisiteChecker'
        }).Count -eq 1) `
        'The MSI does not embed the Peggy prerequisite checker.'
    Assert-Installer `
        (@($binaries | Where-Object {
            $_[0] -eq 'PeggyUninstallCleanup'
        }).Count -eq 1) `
        'The MSI does not embed the user-data cleanup executable.'
    Assert-Installer `
        ($startupAction.Count -eq 1 -and
            (([int]$startupAction[0][1] -band 1024) -ne 0) -and
            (([int]$startupAction[0][1] -band 2048) -eq 0) -and
            $startupAction[0][3] -match
                'Remove-PeggyStartupEntry\.ps1') `
        'The deferred, impersonated startup cleanup action is missing.'
    Assert-Installer `
        ($startupRollback.Count -eq 1 -and
            (([int]$startupRollback[0][1] -band 256) -ne 0) -and
            (([int]$startupRollback[0][1] -band 2048) -eq 0) -and
            $startupRollback[0][3] -match
                'Remove-PeggyStartupEntry\.ps1.+-Restore') `
        'The startup cleanup rollback action is missing.'

    $componentDirectories = @{}
    foreach ($row in Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Component`,`Directory_` FROM `Component`') {
        $componentDirectories[$row[0]] = $row[1]
    }
    $componentAttributes = @(
        Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Attributes` FROM `Component`' |
        ForEach-Object { [int]$_[0] }
    )
    Assert-Installer `
        (@($componentAttributes | Where-Object {
            ($_ -band 256) -eq 0
        }).Count -eq 0) `
        'One or more MSI components are not marked 64-bit.'
    $msiFiles = @()
    foreach ($row in Get-MsiRows `
            -Database $database `
            -Query 'SELECT `Component_`,`FileName`,`Version` FROM `File`') {
        $directory = Get-RelativeMsiDirectory `
            -DirectoryId $componentDirectories[$row[0]] `
            -Directories $directories
        $name = Get-MsiLongName -Value $row[1]
        if ($name -match $binaryNamePattern) {
            Assert-Installer `
                ($row[2] -eq $versionInfo.FileVersion) `
                "MSI binary '$name' does not have FileVersion $($versionInfo.FileVersion)."
        }
        $msiFiles += if ([string]::IsNullOrWhiteSpace($directory)) {
            $name
        } else {
            Join-Path $directory $name
        }
    }
    $sourceFiles = @(
        Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
            ForEach-Object {
                [IO.Path]::GetRelativePath($publishRoot, $_.FullName)
            }
    )
    $fileDifference = @(
        Compare-Object `
            ($sourceFiles | Sort-Object) `
            ($msiFiles | Sort-Object) `
            -CaseSensitive
    )
    Assert-Installer `
        ($fileDifference.Count -eq 0) `
        'MSI File table does not exactly match the ZIP publish payload.'
    Assert-Installer `
        (@($msiFiles | Where-Object {
            $_ -match '(^|\\)(state\.dat|models)(\\|$)'
        }).Count -eq 0) `
        'Persistent state or model data was included in the MSI payload.'

    $removeFolders = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `DirProperty` FROM `RemoveFile`')
    Assert-Installer `
        (@($removeFolders | Where-Object {
            $_[0] -in @('LocalAppDataFolder', 'TARGETDIR')
        }).Count -eq 0) `
        'MSI contains an unsafe broad directory removal.'
} finally {
    [PeggyMsiNative]::Close($database)
}

$validationRoot = Join-Path $artifactRoot 'validation'
$zipExtractRoot = Join-Path $validationRoot 'zip'
$adminRoot = Join-Path $validationRoot 'admin-image'
$adminLog = Join-Path $validationRoot 'admin-image.log'
$bundleExtractRoot = Join-Path $validationRoot 'bundle-extract'
$bundleWorkRoot = Join-Path $validationRoot 'bundle-work'
$bundleBaRoot = Join-Path $validationRoot 'bundle-ba'
if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $adminRoot -Force | Out-Null

try {
    Expand-Archive `
        -LiteralPath $zipPath `
        -DestinationPath $zipExtractRoot
    $zipFiles = @(
        Get-ChildItem -LiteralPath $zipExtractRoot -File -Recurse
    )
    Assert-Installer `
        ($zipFiles.Count -eq $sourceFiles.Count) `
        'ZIP file count does not match the publish payload.'
    foreach ($sourceFile in Get-ChildItem `
            -LiteralPath $publishRoot `
            -File `
            -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath(
            $publishRoot,
            $sourceFile.FullName)
        $zipFile = Join-Path $zipExtractRoot $relativePath
        Assert-Installer `
            (Test-Path -LiteralPath $zipFile -PathType Leaf) `
            "ZIP is missing '$relativePath'."
        Assert-Installer `
            ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).
                Hash -eq
                (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash) `
            "ZIP changed '$relativePath'."
    }

    $msiArguments = @(
        '/a',
        ('"' + $msiPath + '"'),
        ('TARGETDIR="' + $adminRoot + '"'),
        '/qn',
        '/norestart',
        '/l*v',
        ('"' + $adminLog + '"')
    )
    $msiProcess = Start-Process `
        -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') `
        -ArgumentList $msiArguments `
        -Wait `
        -PassThru
    Assert-Installer `
        ($msiProcess.ExitCode -eq 0) `
        "MSI administrative image failed with exit $($msiProcess.ExitCode)."

    $extractedTray = @(
        Get-ChildItem `
            -LiteralPath $adminRoot `
            -Filter 'peggy-tray.exe' `
            -File `
            -Recurse
    )
    Assert-Installer `
        ($extractedTray.Count -eq 1) `
        'Administrative image did not contain exactly one Peggy payload root.'
    $extractedPayloadRoot = $extractedTray[0].DirectoryName
    foreach ($sourceFile in Get-ChildItem `
            -LiteralPath $publishRoot `
            -File `
            -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath(
            $publishRoot,
            $sourceFile.FullName)
        $extractedPath = Join-Path $extractedPayloadRoot $relativePath
        Assert-Installer `
            (Test-Path -LiteralPath $extractedPath -PathType Leaf) `
            "Administrative image is missing '$relativePath'."
        Assert-Installer `
            ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).
                Hash -eq
                (Get-FileHash -LiteralPath $extractedPath -Algorithm SHA256).
                Hash) `
            "Administrative image changed '$relativePath'."
    }

    $publishedIcon = Join-Path $publishRoot 'Assets\Peggy.ico'
    $publishedTray = Join-Path $publishRoot 'peggy-tray.exe'
    $trayIcon = Get-IconFingerprint `
        -Path $publishedTray `
        -Associated
    $expectedTrayIcon = Get-IconFingerprint `
        -Path $publishedIcon `
        -Width $trayIcon.Width `
        -Height $trayIcon.Height
    Assert-Installer `
        ($trayIcon.Hash -eq $expectedTrayIcon.Hash) `
        'The published Peggy executable does not contain Peggy''s icon.'

    $nugetRoot = if (
        [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)
    ) {
        Join-Path $HOME '.nuget\packages'
    } else {
        $env:NUGET_PACKAGES
    }
    $wixCommand = Join-Path (
        Join-Path $nugetRoot 'wixtoolset.sdk\6.0.2'
    ) 'tools\net6.0\wix.dll'
    Assert-Installer `
        (Test-Path -LiteralPath $wixCommand -PathType Leaf) `
        'The pinned WiX 6.0.2 inspection command was not restored.'
    New-Item -ItemType Directory -Path $bundleExtractRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $bundleWorkRoot -Force | Out-Null
    & dotnet $wixCommand burn extract `
        -intermediateFolder $bundleWorkRoot `
        $bundlePath `
        -o $bundleExtractRoot `
        -oba $bundleBaRoot
    Assert-Installer `
        ($LASTEXITCODE -eq 0) `
        'Pinned WiX could not extract the Burn bundle.'

    $embeddedPayloads = @(
        Get-ChildItem -LiteralPath $bundleExtractRoot -File -Recurse
    )
    $msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
    Assert-Installer `
        (@($embeddedPayloads | Where-Object {
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq
                $msiHash
        }).Count -eq 1) `
        'Burn does not embed the exact validated MSI.'
    [xml]$bundleManifest = Get-Content -LiteralPath (Join-Path $bundleBaRoot 'manifest.xml') -Raw
    $ns = [Xml.XmlNamespaceManager]::new($bundleManifest.NameTable)
    $ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/2008/Burn')
    $chain = $bundleManifest.SelectSingleNode('//w:Chain', $ns)
    $packages = @($chain.ChildNodes | Where-Object { $_.NodeType -eq 'Element' })
    Assert-Installer `
        ($packages.Count -eq 1 -and $packages[0].Id -eq 'PeggyMsi' -and
            $packages[0].LocalName -eq 'MsiPackage' -and
            $packages[0].PerMachine -eq 'no' -and $packages[0].Vital -eq 'yes') `
        'The unified setup chain must contain only the per-user Peggy MSI, with no separate prerequisite or launch UI packages.'
    Assert-Installer `
        ($null -ne $packages[0].SelectSingleNode('w:MsiProperty[@Id="PEGGY_BUNDLE" and @Value="1"]', $ns)) `
        'The single-window bootstrapper must suppress MSI auto-launch.'

    foreach ($required in @(
            'peggy-setup.exe', 'peggy-setup.dll', 'peggy-setup.deps.json',
            'peggy-setup.runtimeconfig.json', 'peggy-tray.dll', 'Peggy.Core.dll',
            'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
            'WixToolset.BootstrapperApplicationApi.dll', 'mbanative.dll',
            'scripts\Install-SpeechModels.ps1', 'THIRD-PARTY-NOTICES.txt'
        )) {
        Assert-Installer `
            (Test-Path -LiteralPath (Join-Path $setupRoot $required) -PathType Leaf) `
            "The self-contained setup build is missing '$required'."
    }
    $setupFiles = @(Get-ChildItem -LiteralPath $setupRoot -File -Recurse)
    foreach ($file in $setupFiles) {
        $relative = [IO.Path]::GetRelativePath($setupRoot, $file.FullName)
        $extracted = Join-Path $bundleBaRoot $relative
        Assert-Installer (Test-Path -LiteralPath $extracted -PathType Leaf) `
            "Burn's bootstrapper container is missing '$relative'."
        Assert-Installer `
            ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -eq
                (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash) `
            "Burn's bootstrapper container changed '$relative'."
        if ($file.Name -match $binaryNamePattern) {
            Assert-Installer ($file.VersionInfo.FileVersion -eq $versionInfo.FileVersion) `
                "Setup binary '$relative' does not have FileVersion $($versionInfo.FileVersion)."
        }
    }
    $runtimeConfig = Get-Content -LiteralPath (
        Join-Path $setupRoot 'peggy-setup.runtimeconfig.json') -Raw | ConvertFrom-Json
    Assert-Installer `
        (@($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object {
            $_.name -eq 'Microsoft.WindowsDesktop.App'
        }).Count -eq 1) `
        'The WPF setup host must include its runtime rather than require .NET on a new machine.'

    $bundleVersion = (Get-Item -LiteralPath $bundlePath).VersionInfo
    Assert-Installer `
        ($bundleVersion.ProductName -eq 'Peggy' -and
            $bundleVersion.CompanyName -eq 'Didier Gbenou' -and
            $bundleVersion.ProductVersion -eq $versionInfo.BundleVersion) `
        'Burn version or publisher metadata is incorrect.'
    $bundleIcon = Get-IconFingerprint `
        -Path $bundlePath `
        -Associated
    $expectedBundleIcon = Get-IconFingerprint `
        -Path $publishedIcon `
        -Width $bundleIcon.Width `
        -Height $bundleIcon.Height
    Assert-Installer `
        ($bundleIcon.Hash -eq $expectedBundleIcon.Hash) `
        'The Burn bootstrapper does not contain Peggy''s icon.'

    if (-not $SkipPrerequisiteExecution) {
        Assert-Installer `
            (Test-Path -LiteralPath $checkerPath -PathType Leaf) `
            'The prerequisite checker build output is unavailable.'
        $checkerProcess = Start-Process `
            -FilePath $checkerPath `
            -ArgumentList '--validate' `
            -Wait `
            -PassThru `
            -NoNewWindow
        Assert-Installer `
            ($checkerProcess.ExitCode -eq 0) `
            'The current machine did not pass Peggy prerequisite detection.'
    }

    foreach ($artifact in @(
            $assetNames.Zip,
            $assetNames.Msi,
            $assetNames.Bundle
        )) {
        $checksumName = if ($artifact -eq $assetNames.Zip) {
            $assetNames.ZipChecksum
        } else {
            "$artifact.sha256"
        }
        $checksumPath = Join-Path $artifactRoot $checksumName
        Assert-Installer `
            (Test-Path -LiteralPath $checksumPath -PathType Leaf) `
            "Checksum '$checksumName' is missing."
        $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        $expectedHash = (
            Get-FileHash `
                -LiteralPath (Join-Path $artifactRoot $artifact) `
                -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        Assert-Installer `
            ($checksumText -eq "$expectedHash  $artifact") `
            "Checksum '$checksumName' is invalid."
    }
} finally {
    if (-not $KeepExtracted -and
        (Test-Path -LiteralPath $validationRoot)) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force
    }
}

$signatureResults = foreach ($artifact in @($msiPath, $bundlePath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $artifact
    "$([IO.Path]::GetFileName($artifact))=$($signature.Status)"
}
Write-Output (
    "Peggy Windows installer validation passed: $($sourceFiles.Count) " +
    "payload files, MSI $($versionInfo.ProductVersion), " +
    "bundle $($versionInfo.BundleVersion), signatures " +
    ($signatureResults -join ', ') + '.'
)
