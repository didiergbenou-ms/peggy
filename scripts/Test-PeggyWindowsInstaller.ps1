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

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion -Version $Version
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$publishRoot = Join-Path $artifactRoot 'peggy-win-x64'
$zipPath = Join-Path $artifactRoot 'peggy-win-x64.zip'
$msiPath = Join-Path $artifactRoot 'peggy-win-x64.msi'
$bundlePath = Join-Path $artifactRoot 'peggy-win-x64-setup.exe'
$checkerPath = Join-Path (
    Join-Path $artifactRoot 'build\prerequisite-check'
) 'peggy-prerequisite-check.exe'
foreach ($path in @($publishRoot, $zipPath, $msiPath, $bundlePath)) {
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

function Get-MsiRows {
    param(
        [Parameter(Mandatory)]$Database,
        [Parameter(Mandatory)][string]$Query
    )

    $view = $Database.GetType().InvokeMember(
        'OpenView',
        'InvokeMethod',
        $null,
        $Database,
        @($Query))
    try {
        $view.GetType().InvokeMember(
            'Execute',
            'InvokeMethod',
            $null,
            $view,
            $null) | Out-Null
        $rows = @()
        while ($record = $view.GetType().InvokeMember(
                'Fetch',
                'InvokeMethod',
                $null,
                $view,
                $null)) {
            $fieldCount = $record.GetType().InvokeMember(
                'FieldCount',
                'GetProperty',
                $null,
                $record,
                $null)
            $values = @()
            for ($index = 1; $index -le $fieldCount; $index++) {
                $values += [string]$record.GetType().InvokeMember(
                    'StringData',
                    'GetProperty',
                    $null,
                    $record,
                    @($index))
            }
            $rows += ,$values
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
                $record) | Out-Null
        }
        return $rows
    } finally {
        try {
            $view.GetType().InvokeMember(
                'Close',
                'InvokeMethod',
                $null,
                $view,
                $null) | Out-Null
        } finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
                $view) | Out-Null
        }
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
    '1.2.3'
) | ForEach-Object {
    [Version](ConvertTo-PeggyInstallerVersion -Version $_).ProductVersion
}
for ($index = 1; $index -lt $versionOrder.Count; $index++) {
    Assert-Installer `
        ($versionOrder[$index] -gt $versionOrder[$index - 1]) `
        'Prerelease MSI version mapping is not strictly ordered.'
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

$installer = New-Object -ComObject WindowsInstaller.Installer
$summary = $installer.SummaryInformation([string]$msiPath, 0)
try {
    Assert-Installer `
        ($summary.Property(7) -eq 'x64;1033' -and
            [int]$summary.Property(14) -eq 500) `
        'MSI summary is not Windows x64 with InstallerVersion 500.'
} finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
        $summary) | Out-Null
}
$database = $installer.OpenDatabase([string]$msiPath, 0)
try {
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
            -not [string]::IsNullOrWhiteSpace($properties['ARPHELPLINK'])) `
        'ARP metadata is incomplete.'
    Assert-Installer `
        (-not $properties.ContainsKey('MSIRESTARTMANAGERCONTROL')) `
        'Restart Manager must remain enabled.'

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

    $shortcuts = @(Get-MsiRows `
        -Database $database `
        -Query 'SELECT `Directory_`,`Name`,`Target`,`WkDir` FROM `Shortcut`')
    Assert-Installer `
        ($shortcuts.Count -eq 1 -and
            $shortcuts[0][0] -eq 'ProgramMenuFolder' -and
            (Get-MsiLongName $shortcuts[0][1]) -eq 'Peggy' -and
            $shortcuts[0][2] -eq '[INSTALLFOLDER]peggy-tray.exe' -and
            $shortcuts[0][3] -eq 'INSTALLFOLDER') `
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
            $sequence['InstallExecute'].Number -and
            $sequence['RemoveExistingProducts'].Number -lt
                $sequence['InstallFinalize'].Number) `
        'RemoveExistingProducts is not transactionally scheduled after InstallExecute.'
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
            -Query 'SELECT `Component_`,`FileName` FROM `File`') {
        $directory = Get-RelativeMsiDirectory `
            -DirectoryId $componentDirectories[$row[0]] `
            -Directories $directories
        $name = Get-MsiLongName -Value $row[1]
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
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
        $database) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject(
        $installer) | Out-Null
}

$validationRoot = Join-Path $artifactRoot 'validation'
$zipExtractRoot = Join-Path $validationRoot 'zip'
$adminRoot = Join-Path $validationRoot 'admin-image'
$adminLog = Join-Path $validationRoot 'admin-image.log'
$bundleExtractRoot = Join-Path $validationRoot 'bundle-extract'
$bundleWorkRoot = Join-Path $validationRoot 'bundle-work'
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
        -o $bundleExtractRoot
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
    Assert-Installer `
        (@($embeddedPayloads | Where-Object {
            $_.VersionInfo.ProductName -eq 'peggy-prerequisite-check'
        }).Count -eq 1) `
        'Burn does not embed the Peggy prerequisite checker.'

    $bundleVersion = (Get-Item -LiteralPath $bundlePath).VersionInfo
    Assert-Installer `
        ($bundleVersion.ProductName -eq 'Peggy' -and
            $bundleVersion.CompanyName -eq 'Didier Gbenou' -and
            $bundleVersion.ProductVersion -eq $versionInfo.BundleVersion) `
        'Burn version or publisher metadata is incorrect.'

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
            'peggy-win-x64.zip',
            'peggy-win-x64.msi',
            'peggy-win-x64-setup.exe'
        )) {
        $checksumName = if ($artifact -eq 'peggy-win-x64.zip') {
            'peggy-win-x64.sha256'
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
