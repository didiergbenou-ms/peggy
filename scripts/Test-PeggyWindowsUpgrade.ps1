#requires -Version 7.0
<#
.SYNOPSIS
Exercises real, isolated per-user MSI upgrade, rollback, and maintenance transactions.
.DESCRIPTION
Copies (never installs) the supplied production MSIs, namespaces their identities and
resources, and replaces executable custom actions with inert Type 51 markers. All
original sequence rows/conditions, Upgrade ranges, File rows, and cabinet bytes are
preserved. A conditional Type 19 failure tests transactional removal rollback.

This tests Windows Installer replacement and action conditions, not the production
prerequisite/cleanup helpers, authentication, or application startup. Restart Manager
is disabled; held-open-file/reboot behavior is deliberately outside this regression.
No Peggy executable is started and no existing process is stopped.

Successful runs remove fixture installations, registry state, and payload copies,
retaining logs and a JSON manifest. Failed runs retain isolated evidence and print
the fixture-only product codes for recovery. PrepareOnly performs every database
safety check without calling msiexec or writing registry sentinels.
.EXAMPLE
.\scripts\Test-PeggyWindowsUpgrade.ps1 -PreviousMsiPath .\baseline\peggy-win-x64.msi `
    -MsiPath .\artifacts\peggy-win-x64.msi -PayloadDirectory .\artifacts\peggy-win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousMsiPath,
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [string]$PreviousPayloadDirectory,
    [string]$OutputDirectory = (
        Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\upgrade-tests'
    ),
    [switch]$PrepareOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This regression requires Windows and Windows Installer.' }

if (-not ('PeggyUpgradeTest.PathSafety' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace PeggyUpgradeTest {
    public static class PathSafety {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint SizeHigh, SizeLow, ReparseTag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string path, out FindData data);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);
        public static bool IsCloudPlaceholder(string path) {
            FindData data;
            IntPtr handle = FindFirstFileW(path, out data);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                // CLOUD and CLOUD_1..F tags are storage placeholders, not name surrogates.
                return (data.ReparseTag & 0xffff0fffU) == 0x9000001aU;
            } finally { FindClose(handle); }
        }
    }
}
'@
}

function Assert-Upgrade {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Upgrade regression: $Message" }
}

function Release-Com {
    param($Object)
    if ($null -ne $Object) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Object)
    }
}

function Get-ComProperty {
    param($Object, [string]$Name, [object[]]$Arguments = @())
    return ,($Object.GetType().InvokeMember($Name, 'GetProperty', $null, $Object, $Arguments))
}

function Get-MsiRows {
    param($Database, [string]$Query)
    $view = $Database.OpenView($Query)
    try {
        [void]$view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = @()
                for ($index = 1; $index -le (Get-ComProperty $record FieldCount); $index++) {
                    $values += [string](Get-ComProperty $record StringData @($index))
                }
                ,$values
            } finally { Release-Com $record }
        }
    } finally {
        [void]$view.Close()
        Release-Com $view
    }
}

function Invoke-MsiSql {
    param($Database, [string]$Query)
    $view = $Database.OpenView($Query)
    try { [void]$view.Execute() } finally {
        [void]$view.Close()
        Release-Com $view
    }
}

function ConvertTo-SqlLiteral {
    param([string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Set-MsiValue {
    param($Database, [string]$Table, [string]$Column, [string]$Value,
        [string]$KeyColumn, [string]$Key)
    Invoke-MsiSql $Database (
        'UPDATE `{0}` SET `{1}` = {2} WHERE `{3}` = {4}' -f
        $Table, $Column, (ConvertTo-SqlLiteral $Value), $KeyColumn, (ConvertTo-SqlLiteral $Key)
    )
}

function Add-MsiProperty {
    param($Database, [string]$Name, [string]$Value)
    Invoke-MsiSql $Database (
        'INSERT INTO `Property` (`Property`, `Value`) VALUES ({0}, {1})' -f
        (ConvertTo-SqlLiteral $Name), (ConvertTo-SqlLiteral $Value)
    )
}

function Assert-NoReparsePath {
    param([string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            Assert-SafeItem $item
        }
        $current = Split-Path -Parent $current
    }
}

function Assert-SafeItem {
    param($Item)
    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Assert-Upgrade ([PeggyUpgradeTest.PathSafety]::IsCloudPlaceholder($Item.FullName)) `
            "Refusing filesystem redirection '$($Item.FullName)'."
    }
}

function Assert-UnderRoot {
    param([string]$Path, [string]$Root)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    Assert-Upgrade ([IO.Path]::GetFullPath($Path).StartsWith(
            $prefix, [StringComparison]::OrdinalIgnoreCase)) "Path escapes fixture root: '$Path'."
}

function Assert-SafeName {
    param([string]$Name)
    Assert-Upgrade (-not [string]::IsNullOrEmpty($Name) -and
        $Name -notin @('.', '..') -and
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -lt 0 -and
        $Name -notmatch '[\[\]]' -and $Name -eq $Name.TrimEnd(' ', '.')) `
        "Unexpected MSI path segment '$Name'."
}

function Get-MsiLongName {
    param([string]$Name)
    $parts = $Name.Split('|')
    foreach ($part in $parts) { Assert-SafeName $part }
    Assert-Upgrade ($parts.Count -le 2) "Invalid MSI filename '$Name'."
    return $parts[-1]
}

function Get-RelativeMsiDirectory {
    param([string]$Id, [hashtable]$Directories)
    $segments = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($Id -ne 'INSTALLFOLDER') {
        Assert-Upgrade ($seen.Add($Id) -and $Directories.ContainsKey($Id)) `
            "Directory '$Id' is not beneath INSTALLFOLDER."
        $entry = $Directories[$Id]
        $segments.Insert(0, (Get-MsiLongName $entry[2]))
        $Id = $entry[1]
    }
    return $segments -join '\'
}

function Get-TreeHashes {
    param([string]$Root)
    Assert-NoReparsePath $Root
    Assert-Upgrade (Test-Path -LiteralPath $Root -PathType Container) "Missing payload directory '$Root'."
    $hashes = @{}
    # Inspect each directory before descending, rather than following junctions.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            Assert-SafeItem $item
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            } else {
                $relative = [IO.Path]::GetRelativePath($Root, $item.FullName)
                $hashes[$relative] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            }
        }
    }
    return $hashes
}

function Assert-Hashes {
    param([hashtable]$Expected, [hashtable]$Actual, [string]$Label)
    $differences = @(
        foreach ($path in $Expected.Keys) {
            if (-not $Actual.ContainsKey($path)) { "missing: $path" }
            elseif ($Actual[$path] -ne $Expected[$path]) { "changed: $path" }
        }
        foreach ($path in $Actual.Keys) {
            if (-not $Expected.ContainsKey($path)) { "unexpected: $path" }
        }
    )
    Assert-Upgrade ($differences.Count -eq 0) "$Label payload mismatch: $($differences -join '; ')"
}

function Get-NamespacedGuid {
    param([string]$Kind, [string]$Original)
    $parsed = [guid]::Parse($Original)
    $bytes = [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes("$namespace/$Kind/$($parsed.ToString('D'))"))
    return ([guid]::new([byte[]]$bytes[0..15])).ToString('B').ToUpperInvariant()
}

function Get-TableFingerprint {
    param($Database, [string]$Table)
    $rows = @(Get-MsiRows $Database ('SELECT * FROM `{0}`' -f $Table))
    return ConvertTo-Json -InputObject $rows -Depth 8 -Compress
}

function Get-CabinetHashes {
    param($Database)
    $hashes = @{}
    foreach ($media in @(Get-MsiRows $Database 'SELECT * FROM `Media`')) {
        $name = $media[3].Substring(1)
        $view = $Database.OpenView(
            'SELECT `Data` FROM `_Streams` WHERE `Name` = ' + (ConvertTo-SqlLiteral $name))
        $record = $null
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            [void]$view.Execute()
            $record = $view.Fetch()
            Assert-Upgrade ($null -ne $record) "Missing embedded cabinet '$name'."
            do {
                # msiReadStreamBytes returns one byte per BSTR character; Latin1 is lossless.
                $chunk = [string]$record.GetType().InvokeMember(
                    'ReadStream', 'InvokeMethod', $null, $record, @(1, 1048576, 1))
                if ($chunk.Length) { $hash.AppendData([Text.Encoding]::Latin1.GetBytes($chunk)) }
            } while ($chunk.Length)
            $hashes[$name] = [Convert]::ToHexString($hash.GetHashAndReset())
        } finally {
            $hash.Dispose()
            Release-Com $record
            [void]$view.Close()
            Release-Com $view
        }
    }
    return $hashes
}

$sequenceTables = @(
    'InstallExecuteSequence', 'InstallUISequence', 'AdminExecuteSequence',
    'AdminUISequence', 'AdvtExecuteSequence'
)
$standardActions = @(
    'AppSearch', 'CostInitialize', 'FileCost', 'CostFinalize', 'ExecuteAction',
    'InstallValidate', 'InstallInitialize', 'InstallAdminPackage', 'InstallFiles',
    'InstallFinalize', 'CreateShortcuts', 'PublishFeatures', 'PublishProduct',
    'FindRelatedProducts', 'LaunchConditions', 'ValidateProductID', 'MigrateFeatureStates',
    'ProcessComponents', 'UnpublishFeatures', 'RemoveRegistryValues', 'RemoveShortcuts',
    'RemoveFiles', 'RemoveFolders', 'CreateFolders', 'WriteRegistryValues', 'RegisterUser',
    'RegisterProduct', 'InstallExecute', 'RemoveExistingProducts'
)
$actionTypes = @{
    SetARPINSTALLLOCATION = 51
    SetEXPECTED_PEGGY_RUN_VALUE = 51
    CheckPeggyPrerequisites = 2
    LaunchPeggyAfterInstall = 66
    RemovePeggyUserData = 66
    RemoveOwnedPeggyStartupEntry = 1058
    RollbackOwnedPeggyStartupEntry = 1314
}
$markerActions = @(
    'CheckPeggyPrerequisites', 'LaunchPeggyAfterInstall', 'RemovePeggyUserData',
    'RemoveOwnedPeggyStartupEntry', 'RollbackOwnedPeggyStartupEntry'
)
$preservedTables = @(
    'File', 'Media', 'MsiFileHash', 'Feature', 'FeatureComponents', 'LaunchCondition',
    'AppSearch', 'Signature'
) + $sequenceTables
$allowedTables = @(
    'Upgrade', 'LaunchCondition', 'Icon', 'Property', 'Binary', 'RegLocator', 'AppSearch',
    'Signature', 'CustomAction', 'Directory', 'Shortcut', 'Registry', 'Component',
    'Feature', 'FeatureComponents', 'RemoveFile', 'File', 'Media', 'MsiFileHash', '_Validation'
) + $sequenceTables
$originalProperties = @(
    'UpgradeCode', 'ARPCOMMENTS', 'ARPCONTACT', 'ARPHELPLINK', 'ARPPRODUCTICON',
    'ARPURLINFOABOUT', 'ARPNOMODIFY', 'ARPNOREPAIR', 'Manufacturer', 'ProductCode',
    'ProductLanguage', 'ProductName', 'ProductVersion', 'SecureCustomProperties'
)
$stableUpgradeCode = '{D661EFB2-155E-51B2-9951-C35E621D9E11}'

function Assert-MsiScope {
    param($Database, [switch]$Fixture)
    $summary = Get-ComProperty $Database SummaryInformation @(0)
    try {
        Assert-Upgrade ((Get-ComProperty $summary Property @(7)) -eq 'x64;1033' -and
            (Get-ComProperty $summary Property @(15)) -eq 10) `
            'Only the known compressed, non-elevating x64 per-user package is supported.'
    } finally { Release-Com $summary }
    $tables = @(Get-MsiRows $Database 'SELECT `Name` FROM `_Tables`')
    foreach ($row in $tables) {
        Assert-Upgrade ($row[0] -in $allowedTables) "Unsupported MSI table '$($row[0])'; refusing to execute."
    }
    Assert-Upgrade (@(Get-MsiRows $Database 'SELECT `Name` FROM `_Storages`').Count -eq 0) `
        'Embedded transforms are not supported.'
    $properties = @{}
    foreach ($row in @(Get-MsiRows $Database 'SELECT `Property`, `Value` FROM `Property`')) {
        $properties[$row[0]] = $row[1]
        $allowed = $originalProperties
        if ($Fixture) { $allowed += @('INSTALLFOLDER', 'FIXTURESHORTCUTS', 'MSIRESTARTMANAGERCONTROL') }
        Assert-Upgrade ($row[0] -in $allowed) "Unexpected MSI property '$($row[0])'."
    }
    $expectedUpgrade = if ($Fixture) { $fixtureUpgradeCode } else { $stableUpgradeCode }
    Assert-Upgrade ($properties.UpgradeCode -eq $expectedUpgrade) 'Unexpected product UpgradeCode.'
    Assert-Upgrade ($properties.ProductLanguage -eq '1033') 'Only the known English per-user MSI is supported.'
    Assert-Upgrade ($properties.SecureCustomProperties -eq
        'PEGGY_BUNDLE;WIX_DOWNGRADE_DETECTED;WIX_UPGRADE_DETECTED') 'Unexpected secure properties.'
    if ($Fixture) {
        Assert-Upgrade ($properties.INSTALLFOLDER -eq $installRoot -and
            $properties.FIXTURESHORTCUTS -eq $shortcutRoot -and
            $properties.MSIRESTARTMANAGERCONTROL -eq 'Disable') 'Fixture directory/RM isolation was lost.'
        Assert-Upgrade ($properties.ProductName -eq "Peggy Upgrade Test $namespace") 'Unexpected fixture product name.'
    } else {
        Assert-Upgrade ($properties.ProductName -eq 'Peggy') 'Input is not a Peggy MSI.'
    }
    $directories = @{}
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `Directory`')) { $directories[$row[0]] = $row }
    $knownDirectories = @('TARGETDIR', 'SystemFolder', 'LocalAppDataFolder',
        'LocalProgramsFolder', 'INSTALLFOLDER', 'ProgramMenuFolder')
    if ($Fixture) { $knownDirectories += 'FIXTURESHORTCUTS' }
    foreach ($id in $directories.Keys) {
        if ($id -notin $knownDirectories) {
            Get-RelativeMsiDirectory $id $directories | Out-Null
        }
    }
    Assert-Upgrade ($directories.INSTALLFOLDER[1] -eq $(if ($Fixture) { 'TARGETDIR' } else { 'LocalProgramsFolder' })) `
        'Unexpected installation directory ancestry.'
    if ($Fixture) {
        Assert-Upgrade ($directories.FIXTURESHORTCUTS[1] -eq 'TARGETDIR') 'Unsafe shortcut directory ancestry.'
    }
    $components = @{}
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `Component`')) {
        Assert-Upgrade ($row[3] -eq '260' -and $row[4] -eq '' -and
            [guid]::Parse($row[1]) -ne [guid]::Empty) "Unsupported component '$($row[0])'."
        if ($row[0] -eq 'PeggyStartMenuShortcut') {
            Assert-Upgrade ($row[2] -eq $(if ($Fixture) { 'FIXTURESHORTCUTS' } else { 'ProgramMenuFolder' })) `
                'Unexpected shortcut component destination.'
        } else { Get-RelativeMsiDirectory $row[2] $directories | Out-Null }
        $components[$row[0]] = $row
    }
    $paths = @{}
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `File`')) {
        Assert-Upgrade ($components.ContainsKey($row[1])) "Unknown file component '$($row[1])'."
        $relative = Get-RelativeMsiDirectory $components[$row[1]][2] $directories
        $name = Get-MsiLongName $row[2]
        $path = if ($relative) { "$relative\$name" } else { $name }
        Assert-Upgrade (-not $paths.ContainsKey($path)) "Duplicate payload destination '$path'."
        $paths[$path] = $true
    }
    Assert-Upgrade ($paths.Count -gt 0 -and $paths.ContainsKey('peggy-tray.exe')) 'Incomplete MSI payload.'
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `Registry`')) {
        $expectedKey = if ($Fixture) { "$registryKey\Installer" } else { 'Software\Peggy\Installer' }
        Assert-Upgrade ($row[1] -eq '1' -and
            $row[2] -in @($expectedKey, "$expectedKey\Components") -and
            $row[3] -match '^[A-Za-z0-9_]+$' -and $row[4] -eq '#1' -and
            $components.ContainsKey($row[5])) "Unsafe registry row '$($row[0])'."
    }
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `RemoveFile`')) {
        Assert-Upgrade ($row[2] -eq '' -and $row[4] -eq '2' -and
            $components.ContainsKey($row[1])) "Unsupported removal row '$($row[0])'."
        Get-RelativeMsiDirectory $row[3] $directories | Out-Null
    }
    $shortcuts = @(Get-MsiRows $Database 'SELECT * FROM `Shortcut`')
    Assert-Upgrade ($shortcuts.Count -eq 1) 'Unexpected shortcut count.'
    $shortcut = $shortcuts[0]
    Assert-Upgrade ($shortcut[0] -eq 'PeggyStartMenuShortcutLink' -and
        $shortcut[1] -eq $(if ($Fixture) { 'FIXTURESHORTCUTS' } else { 'ProgramMenuFolder' }) -and
        $shortcut[2] -eq 'Peggy' -and $shortcut[3] -eq 'PeggyStartMenuShortcut' -and
        $shortcut[4] -eq '[INSTALLFOLDER]peggy-tray.exe' -and $shortcut[5] -eq '' -and
        $shortcut[11] -eq 'INSTALLFOLDER') 'Unsupported shortcut behavior.'
    $locators = @(Get-MsiRows $Database 'SELECT * FROM `RegLocator`')
    Assert-Upgrade ($locators.Count -eq 1) 'Unexpected registry locator count.'
    $locator = $locators[0]
    Assert-Upgrade ($locator[0] -eq 'PeggyStartupRegistrySearch' -and $locator[1] -eq '1' -and
        $locator[2] -eq $(if ($Fixture) { "$registryKey\Startup" } else {
                'Software\Microsoft\Windows\CurrentVersion\Run' }) -and
        $locator[3] -eq 'Peggy' -and $locator[4] -eq '18') 'Unsafe registry search.'
    $search = @(Get-MsiRows $Database 'SELECT * FROM `AppSearch`')
    Assert-Upgrade ($search.Count -eq 1 -and $search[0][0] -eq 'PEGGY_RUN_VALUE' -and
        $search[0][1] -eq 'PeggyStartupRegistrySearch') 'Unsupported AppSearch.'
    Assert-Upgrade (@(Get-MsiRows $Database 'SELECT * FROM `Signature`').Count -eq 0) 'File searches are unsupported.'
    $actions = @(Get-MsiRows $Database 'SELECT * FROM `CustomAction`')
    Assert-Upgrade ($actions.Count -eq $actionTypes.Count) 'Unexpected custom action count.'
    foreach ($action in $actions) {
        $id = $action[0]
        Assert-Upgrade ($actionTypes.ContainsKey($id)) "Unknown custom action '$id'."
        Assert-Upgrade ($action[4] -eq '') "Unsupported extended custom-action flags for '$id'."
        if ($Fixture -and $id -in $markerActions) {
            Assert-Upgrade ($action[1] -eq '51' -and $action[2] -eq "PEGGY_TEST_$id" -and
                $action[3] -eq 'observed') "Executable custom action '$id' was not neutralized."
        } else {
            Assert-Upgrade ([int]$action[1] -eq $actionTypes[$id]) "Unexpected custom action type '$id'."
            if ($action[1] -eq '51') {
                $source = if ($id -eq 'SetARPINSTALLLOCATION') { 'ARPINSTALLLOCATION' } else { 'EXPECTED_PEGGY_RUN_VALUE' }
                $target = if ($id -eq 'SetARPINSTALLLOCATION') { '[INSTALLFOLDER]' } else {
                    '"[INSTALLFOLDER]peggy-tray.exe"' }
                Assert-Upgrade ($action[2] -eq $source -and $action[3] -eq $target) "Unsafe property action '$id'."
            }
        }
    }
    foreach ($table in $sequenceTables) {
        foreach ($row in @(Get-MsiRows $Database ('SELECT * FROM `{0}`' -f $table))) {
            Assert-Upgrade ($row[0] -in $standardActions -or
                ($table -eq 'InstallExecuteSequence' -and $actionTypes.ContainsKey($row[0]))) `
                "Unsupported action '$($row[0])' in $table."
            Assert-Upgrade ([int]$row[2] -gt 0) "Unexpected termination action '$($row[0])'."
        }
    }
    $upgrades = @(Get-MsiRows $Database 'SELECT * FROM `Upgrade`')
    Assert-Upgrade ($upgrades.Count -eq 2) 'Expected the two MajorUpgrade rows.'
    foreach ($row in $upgrades) {
        Assert-Upgrade ($row[0] -eq $expectedUpgrade -and
            $row[6] -in @('WIX_UPGRADE_DETECTED', 'WIX_DOWNGRADE_DETECTED')) 'Unsafe related-product search.'
    }
    $streams = @('Icon.Peggy.exe', 'Binary.PeggyPrerequisiteChecker', 'Binary.PeggyUninstallCleanup',
        ([string][char]5 + 'SummaryInformation'))
    foreach ($row in @(Get-MsiRows $Database 'SELECT * FROM `Media`')) {
        Assert-Upgrade ($row[3] -match '^#cab[0-9]+\.cab$' -and
            $row[4] -eq '' -and $row[5] -eq '') 'Only embedded release cabinets are supported.'
        $streams += $row[3].Substring(1)
    }
    foreach ($row in @(Get-MsiRows $Database 'SELECT `Name` FROM `_Streams`')) {
        Assert-Upgrade ($row[0] -in $streams) "Unexpected embedded stream '$($row[0])'."
    }
    return [pscustomobject]@{ Properties = $properties; Paths = $paths; Components = $components }
}

function New-MsiFixture {
    param([string]$Original, [string]$Destination)
    $originalHash = (Get-FileHash -LiteralPath $Original -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $Original -Destination $Destination
    $database = $installer.OpenDatabase($Destination, 1)
    try {
        $scope = Assert-MsiScope $database
        $snapshots = @{}
        foreach ($table in $preservedTables) { $snapshots[$table] = Get-TableFingerprint $database $table }
        $cabinets = Get-CabinetHashes $database
        $originalUpgrade = @(Get-MsiRows $database 'SELECT * FROM `Upgrade`')
        $productCode = Get-NamespacedGuid product $scope.Properties.ProductCode
        Set-MsiValue $database Property Value $productCode Property ProductCode
        Set-MsiValue $database Property Value $fixtureUpgradeCode Property UpgradeCode
        Set-MsiValue $database Property Value "Peggy Upgrade Test $namespace" Property ProductName
        # Windows Installer SQL cannot UPDATE a primary key.
        Invoke-MsiSql $database 'DELETE FROM `Upgrade`'
        foreach ($row in $originalUpgrade) {
            $values = @($fixtureUpgradeCode) + @($row[1..6])
            $literals = for ($index = 0; $index -lt $values.Count; $index++) {
                if ($index -eq 4) { $values[$index] }
                else { ConvertTo-SqlLiteral $values[$index] }
            }
            Invoke-MsiSql $database (
                'INSERT INTO `Upgrade` (`UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, ' +
                '`Attributes`, `Remove`, `ActionProperty`) VALUES (' + ($literals -join ', ') + ')')
        }
        foreach ($component in $scope.Components.Values) {
            Set-MsiValue $database Component ComponentId `
                (Get-NamespacedGuid component $component[1]) Component $component[0]
        }
        Set-MsiValue $database Directory Directory_Parent TARGETDIR Directory INSTALLFOLDER
        Set-MsiValue $database Directory DefaultDir install Directory INSTALLFOLDER
        Add-MsiProperty $database INSTALLFOLDER $installRoot
        Add-MsiProperty $database FIXTURESHORTCUTS $shortcutRoot
        Add-MsiProperty $database MSIRESTARTMANAGERCONTROL Disable
        Invoke-MsiSql $database (
            'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) ' +
            "VALUES ('FIXTURESHORTCUTS', 'TARGETDIR', 'shortcuts')")
        Set-MsiValue $database Component Directory_ FIXTURESHORTCUTS Component PeggyStartMenuShortcut
        Set-MsiValue $database Shortcut Directory_ FIXTURESHORTCUTS Shortcut PeggyStartMenuShortcutLink
        foreach ($row in @(Get-MsiRows $database 'SELECT * FROM `Registry`')) {
            Set-MsiValue $database Registry Key `
                ($row[2].Replace('Software\Peggy', $registryKey)) Registry $row[0]
        }
        Set-MsiValue $database RegLocator Key "$registryKey\Startup" Signature_ PeggyStartupRegistrySearch
        foreach ($action in $markerActions) {
            Invoke-MsiSql $database (
                'UPDATE `CustomAction` SET `Type` = 51, `Source` = {0}, `Target` = {1} WHERE `Action` = {2}' -f
                (ConvertTo-SqlLiteral "PEGGY_TEST_$action"), "'observed'", (ConvertTo-SqlLiteral $action))
        }
        $summary = Get-ComProperty $database SummaryInformation @(20)
        try {
            $packageCode = Get-NamespacedGuid package (Get-ComProperty $summary Property @(9))
            $summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @(9, $packageCode)) | Out-Null
            [void]$summary.Persist()
        } finally { Release-Com $summary }
        [void]$database.Commit()
    } finally { Release-Com $database }
    # Reopen the committed copy: validate the actual file that msiexec will consume.
    $database = $installer.OpenDatabase($Destination, 0)
    try {
        $fixtureScope = Assert-MsiScope $database -Fixture
        Assert-Upgrade ($fixtureScope.Properties.ProductCode -eq $productCode) 'Product identity mapping failed.'
        $summary = Get-ComProperty $database SummaryInformation @(0)
        try {
            Assert-Upgrade ((Get-ComProperty $summary Property @(9)) -eq $packageCode) 'Package identity mapping failed.'
        } finally { Release-Com $summary }
        foreach ($component in $fixtureScope.Components.Values) {
            Assert-Upgrade ($component[1] -eq (Get-NamespacedGuid component $scope.Components[$component[0]][1])) `
                "Component identity mapping failed for '$($component[0])'."
        }
        foreach ($table in $preservedTables) {
            Assert-Upgrade ((Get-TableFingerprint $database $table) -ceq $snapshots[$table]) `
                "Fixture changed production $table rows."
        }
        $mappedUpgrade = @(Get-MsiRows $database 'SELECT * FROM `Upgrade`')
        for ($index = 0; $index -lt $mappedUpgrade.Count; $index++) {
            $mappedUpgrade[$index][0] = $stableUpgradeCode
        }
        Assert-Upgrade ((ConvertTo-Json -InputObject $mappedUpgrade -Compress) -ceq
            (ConvertTo-Json -InputObject $originalUpgrade -Compress)) 'Upgrade ranges or conditions changed.'
        Assert-Hashes $cabinets (Get-CabinetHashes $database) 'Embedded cabinets'
    } finally { Release-Com $database }
    Assert-Upgrade ((Get-FileHash -LiteralPath $Original -Algorithm SHA256).Hash -eq $originalHash) `
        'Original MSI changed during fixture preparation.'
    return [pscustomobject]@{
        Path = $Destination; ProductCode = $productCode; PackageCode = $packageCode
        OriginalPath = $Original; OriginalHash = $originalHash
        OriginalProductCode = $scope.Properties.ProductCode
        Version = $scope.Properties.ProductVersion; Paths = $scope.Paths
        Hash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    }
}

function Add-RollbackProbe {
    param($Fixture)
    $database = $installer.OpenDatabase($Fixture.Path, 1)
    try {
        $originalActions = @(Get-MsiRows $database 'SELECT * FROM `CustomAction`')
        $originalSequence = @(Get-MsiRows $database 'SELECT * FROM `InstallExecuteSequence`')
        $sequence = @{}
        foreach ($row in $originalSequence) {
            $sequence[$row[0]] = [int]$row[2]
        }
        $earlyRemoval = $sequence.ContainsKey('RemoveExistingProducts') -and
            $sequence.RemoveExistingProducts -gt $sequence.InstallInitialize -and
            $sequence.RemoveExistingProducts -lt $sequence.ProcessComponents
        $lateRemoval = $sequence.ContainsKey('RemoveExistingProducts') -and
            $sequence.ContainsKey('InstallExecute') -and
            $sequence.RemoveExistingProducts -gt $sequence.InstallExecute -and
            $sequence.RemoveExistingProducts -lt $sequence.InstallFinalize
        # Accept the shipped late schedule as a negative control. Its stale-file bug
        # must fail installed-byte comparisons, not an early-schedule precondition.
        Assert-Upgrade (($earlyRemoval -or $lateRemoval) -and
            $sequence.InstallFiles -lt $sequence.InstallFinalize) `
            'Only the known transactional early/late removal schedules are supported.'
        # Fail immediately after file execution when the production schedule has no
        # InstallExecute. This probes removal/file rollback before product publishing.
        $addedFlush = -not $sequence.ContainsKey('InstallExecute')
        $failureSequence = if ($earlyRemoval -and $addedFlush) {
            $sequence.InstallFiles + 2
        } else { $sequence.InstallFinalize - 1 }
        Assert-Upgrade ($failureSequence -notin $sequence.Values -and
            $failureSequence -gt $sequence.RemoveExistingProducts) 'No free post-removal, pre-commit slot for the rollback probe.'
        # InstallFiles normally queues work. Flush that work before failing so rollback
        # covers replaced files as well as removal of the previous product.
        if ($addedFlush) {
            $flushSequence = $failureSequence - 1
            Assert-Upgrade ($flushSequence -gt $sequence.InstallFiles -and
                $flushSequence -notin $sequence.Values) 'No free rollback-only InstallExecute slot.'
            Invoke-MsiSql $database (
                'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) ' +
                "VALUES ('InstallExecute', 'PEGGY_TEST_FORCEFAIL = `"1`"', $flushSequence)")
        } else {
            Assert-Upgrade ($sequence.InstallExecute -gt $sequence.InstallFiles -and
                $sequence.InstallExecute -lt $failureSequence) 'InstallExecute does not precede the rollback probe.'
        }
        Invoke-MsiSql $database (
            'INSERT INTO `CustomAction` (`Action`, `Type`, `Target`) ' +
            "VALUES ('PeggyTestFailUpgrade', 19, 'PEGGY_TEST_EXPECTED_UPGRADE_FAILURE')")
        Invoke-MsiSql $database (
            'INSERT INTO `InstallExecuteSequence` (`Action`, `Condition`, `Sequence`) ' +
            "VALUES ('PeggyTestFailUpgrade', 'PEGGY_TEST_FORCEFAIL = `"1`" AND WIX_UPGRADE_DETECTED', $failureSequence)")
        [void]$database.Commit()
    } finally { Release-Com $database }
    $database = $installer.OpenDatabase($Fixture.Path, 0)
    try {
        $actions = @(Get-MsiRows $database 'SELECT * FROM `CustomAction`')
        $probe = @($actions | Where-Object { $_[0] -eq 'PeggyTestFailUpgrade' })
        Assert-Upgrade ($probe.Count -eq 1 -and $probe[0][1] -eq '19' -and
            $probe[0][2] -eq '' -and $probe[0][3] -eq 'PEGGY_TEST_EXPECTED_UPGRADE_FAILURE') `
            'Invalid failure-probe custom action.'
        $retained = @($actions | Where-Object { $_[0] -ne 'PeggyTestFailUpgrade' })
        Assert-Upgrade ((ConvertTo-Json -InputObject $retained -Compress) -ceq
            (ConvertTo-Json -InputObject $originalActions -Compress)) 'Probe modified an existing custom action.'
        $rows = @(Get-MsiRows $database 'SELECT * FROM `InstallExecuteSequence`')
        $probe = @($rows | Where-Object { $_[0] -eq 'PeggyTestFailUpgrade' })
        Assert-Upgrade ($probe.Count -eq 1 -and
            $probe[0][1] -eq 'PEGGY_TEST_FORCEFAIL = "1" AND WIX_UPGRADE_DETECTED' -and
            [int]$probe[0][2] -eq $failureSequence) 'Invalid failure-probe schedule.'
        if ($addedFlush) {
            $flush = @($rows | Where-Object { $_[0] -eq 'InstallExecute' })
            Assert-Upgrade ($flush.Count -eq 1 -and
                $flush[0][1] -eq 'PEGGY_TEST_FORCEFAIL = "1"' -and
                [int]$flush[0][2] -eq $flushSequence) 'Invalid rollback-only execution schedule.'
        }
        $retained = @($rows | Where-Object {
                $_[0] -ne 'PeggyTestFailUpgrade' -and (-not $addedFlush -or $_[0] -ne 'InstallExecute')
            })
        Assert-Upgrade ((ConvertTo-Json -InputObject $retained -Compress) -ceq
            (ConvertTo-Json -InputObject $originalSequence -Compress)) 'Probe modified a production schedule or condition.'
    } finally { Release-Com $database }
    $Fixture.Hash = (Get-FileHash -LiteralPath $Fixture.Path -Algorithm SHA256).Hash
}

function Get-RelatedFixtureProducts {
    $list = Get-ComProperty $installer RelatedProducts @($fixtureUpgradeCode)
    try {
        for ($index = 0; $index -lt (Get-ComProperty $list Count); $index++) {
            [string](Get-ComProperty $list Item @($index))
        }
    } finally { Release-Com $list }
}

function Assert-RegisteredProduct {
    param($Expected, $Absent)
    $related = @(Get-RelatedFixtureProducts)
    Assert-Upgrade ($related.Count -eq 1 -and $related[0] -eq $Expected.ProductCode) `
        "Expected exactly the registered fixture $($Expected.ProductCode); found: $($related -join ', ')."
    Assert-Upgrade ((Get-ComProperty $installer ProductState @($Expected.ProductCode)) -eq 5) `
        'Expected fixture is not fully installed.'
    Assert-Upgrade ((Get-ComProperty $installer ProductState @($Absent.ProductCode)) -eq -1) `
        "Unexpected registration remains for $($Absent.ProductCode)."
    Assert-Upgrade ((Get-ComProperty $installer ProductInfo @($Expected.ProductCode, 'VersionString')) -eq
        $Expected.Version) 'Registered version differs from the MSI.'
    $location = [string](Get-ComProperty $installer ProductInfo @($Expected.ProductCode, 'InstallLocation'))
    Assert-Upgrade ($location.TrimEnd('\') -eq $installRoot) 'Registered installation location is not isolated.'
}

function Invoke-FixtureMsi {
    param($Fixture, [string]$Label, [switch]$Uninstall, [switch]$ForceFailure)
    Assert-UnderRoot $Fixture.Path $runRoot
    Assert-NoReparsePath $runRoot
    foreach ($path in @($installRoot, $shortcutRoot, $dataRoot, $Fixture.Path)) {
        Assert-NoReparsePath $path
    }
    Assert-Upgrade ($Fixture.ProductCode -in @($previous.ProductCode, $candidate.ProductCode) -and
        $Fixture.ProductCode -notin @($previous.OriginalProductCode, $candidate.OriginalProductCode)) `
        'Refusing a non-fixture product identity.'
    Assert-Upgrade ((Get-FileHash -LiteralPath $Fixture.Path -Algorithm SHA256).Hash -eq $Fixture.Hash) `
        'Fixture changed since its safety validation.'
    $logPath = Join-Path $runRoot "$Label.log"
    $arguments = if ($Uninstall) { @('/x', $Fixture.ProductCode) } else { @('/i', "`"$($Fixture.Path)`"") }
    $arguments += @('/qn', '/norestart', 'REBOOT=ReallySuppress', 'MSIRESTARTMANAGERCONTROL=Disable',
        '/l*v', "`"$logPath`"")
    if ($ForceFailure) { $arguments += 'PEGGY_TEST_FORCEFAIL=1' }
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
        -ArgumentList $arguments -Wait -PassThru
    $expectedExit = if ($ForceFailure) { 1603 } else { 0 }
    Assert-Upgrade ($process.ExitCode -eq $expectedExit) `
        "$Label returned $($process.ExitCode), expected $expectedExit. See '$logPath'."
    return Get-Content -LiteralPath $logPath -Raw
}

function Assert-UpgradeLog {
    param([string]$Log, [switch]$Rollback)
    Assert-Upgrade ($Log -match ('WIX_UPGRADE_DETECTED.*' + [regex]::Escape($previous.ProductCode))) `
        'The previous fixture was not detected as a related product.'
    Assert-Upgrade ($Log -match 'Action ended .*RemoveExistingProducts\. Return value 1') `
        'RemoveExistingProducts did not complete.'
    foreach ($action in @('RemovePeggyUserData', 'RemoveOwnedPeggyStartupEntry', 'RollbackOwnedPeggyStartupEntry')) {
        Assert-Upgrade ($Log -match "Skipping action: $action \(condition is false\)") `
            "Upgrade did not skip $action."
        Assert-Upgrade ($Log -notmatch "PROPERTY CHANGE:.*PEGGY_TEST_$action") `
            "Upgrade ran the neutralized $action action."
    }
    Assert-Upgrade ($Log -notmatch 'PROPERTY CHANGE:.*PEGGY_TEST_LaunchPeggyAfterInstall') `
        'Quiet upgrade attempted to launch Peggy.'
    if ($Rollback) {
        $failure = [regex]::Match($Log, 'Action ended .*PeggyTestFailUpgrade\. Return value 3')
        Assert-Upgrade ($Log -match 'PEGGY_TEST_EXPECTED_UPGRADE_FAILURE' -and
            $failure.Success -and
            $Log -match 'Action ended .*InstallExecute\. Return value 1' -and
            $Log.Substring($failure.Index) -match 'Executing op: Header\([^\r\n]*ScriptType=2,') `
            'The forced failure did not reach the post-execution rollback boundary.'
    }
}

function Assert-Sentinels {
    Assert-Hashes $sentinelHashes (Get-TreeHashes $dataRoot) 'Settings/model sentinel'
    foreach ($name in $registrySentinels.Keys) {
        $path = "Registry::HKEY_CURRENT_USER\$registryKey\$name"
        $value = Get-ItemPropertyValue -LiteralPath $path -Name Peggy
        Assert-Upgrade ($value -ceq $registrySentinels[$name]) "$name sentinel changed."
    }
}

$namespace = [guid]::NewGuid().ToString('N')
$previousPath = (Resolve-Path -LiteralPath $PreviousMsiPath).Path
$candidatePath = (Resolve-Path -LiteralPath $MsiPath).Path
$payloadRoot = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
Assert-NoReparsePath $outputRoot
foreach ($realRoot in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Peggy'), (Join-Path $env:LOCALAPPDATA 'Peggy'))) {
    Assert-Upgrade (-not ($outputRoot + '\').StartsWith(
            $realRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) `
        'OutputDirectory must not be inside real Peggy installation or data.'
}
Assert-Upgrade ($outputRoot -notmatch '["\r\n]') 'Output path cannot be quoted safely for msiexec.'
$runRoot = Join-Path $outputRoot "peggy-upgrade-$namespace"
$installRoot = Join-Path $runRoot 'install'
$shortcutRoot = Join-Path $runRoot 'shortcuts'
$dataRoot = Join-Path $runRoot 'user-data'
$registryKey = "Software\PeggyUpgradeTests\$namespace"
$fixtureUpgradeCode = Get-NamespacedGuid upgrade $stableUpgradeCode
$manifestPath = Join-Path $runRoot 'manifest.json'
$installer = $null
$previous = $null
$candidate = $null
$manifest = [ordered]@{
    Namespace = $namespace; Status = 'preparing'; RunRoot = $runRoot
    InstallRoot = $installRoot; DataRoot = $dataRoot; RegistryKey = "HKCU\$registryKey"
    UpgradeCode = $fixtureUpgradeCode
}
[void](New-Item -ItemType Directory -Path $runRoot)
Write-Host "Isolated MSI regression artifacts: $runRoot"
try {
    $expectedHashes = Get-TreeHashes $payloadRoot
    $previousHashes = if ($PreviousPayloadDirectory) {
        Get-TreeHashes (Resolve-Path -LiteralPath $PreviousPayloadDirectory).Path
    } else { $null }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    Assert-Upgrade (@(Get-RelatedFixtureProducts).Count -eq 0) 'Generated fixture UpgradeCode is already registered.'
    $previous = New-MsiFixture $previousPath (Join-Path $runRoot 'previous-fixture.msi')
    $candidate = New-MsiFixture $candidatePath (Join-Path $runRoot 'candidate-fixture.msi')
    Assert-Upgrade ([version]$candidate.Version -gt [version]$previous.Version -and
        $candidate.ProductCode -ne $previous.ProductCode) 'Candidate must be a distinct, newer major upgrade, not maintenance.'
    $names = @{}
    foreach ($name in $expectedHashes.Keys) { $names[$name] = $true }
    Assert-Hashes $candidate.Paths $names 'Publish/MSI file list'
    foreach ($name in @($previous.Paths.Keys) + @($candidate.Paths.Keys)) {
        Assert-Upgrade ((Join-Path $installRoot $name).Length -lt 260) `
            'Fixture exceeds the Windows Installer path limit; use a shorter OutputDirectory.'
    }
    Add-RollbackProbe $candidate
    $manifest.Previous = $previous | Select-Object * -ExcludeProperty Paths
    $manifest.Candidate = $candidate | Select-Object * -ExcludeProperty Paths
    $manifest.Status = if ($PrepareOnly) { 'prepared-no-msiexec' } else { 'running' }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($PrepareOnly) {
        Write-Host "Safety validation passed; no msiexec or registry writes. Manifest: $manifestPath"
        return
    }
    $installLog = Invoke-FixtureMsi $previous '01-install-previous'
    Assert-RegisteredProduct $previous $candidate
    $installedPreviousHashes = Get-TreeHashes $installRoot
    $installedNames = @{}
    foreach ($name in $installedPreviousHashes.Keys) { $installedNames[$name] = $true }
    Assert-Hashes $previous.Paths $installedNames 'Previous MSI installed file list'
    if ($null -ne $previousHashes) {
        Assert-Hashes $previousHashes $installedPreviousHashes 'Previous publish'
    }
    Assert-Upgrade ($installLog -notmatch 'PROPERTY CHANGE:.*PEGGY_TEST_LaunchPeggyAfterInstall') `
        'Quiet baseline installation attempted to launch Peggy.'
    $installedPreviousHashes | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $runRoot 'previous-payload-hashes.json') -Encoding utf8
    [void](New-Item -ItemType Directory -Path (Join-Path $dataRoot 'models'))
    Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') `
        -Value ('{"testOnly":true,"sentinel":"' + $namespace + '"}') -Encoding utf8
    [IO.File]::WriteAllBytes((Join-Path $dataRoot 'models\test-model.bin'),
        [Text.Encoding]::UTF8.GetBytes("inert-model-$namespace"))
    $sentinelHashes = Get-TreeHashes $dataRoot
    $registrySentinels = @{
        Account = "inert-account-$namespace"
        Startup = '"' + (Join-Path $installRoot 'peggy-tray.exe') + '"'
    }
    foreach ($name in $registrySentinels.Keys) {
        $path = "Registry::HKEY_CURRENT_USER\$registryKey\$name"
        [void](New-Item -Path $path -Force)
        [void](New-ItemProperty -LiteralPath $path -Name Peggy -Value $registrySentinels[$name] -PropertyType String)
    }
    # Successful replacement comes first so the shipped beta4 -> beta5 negative
    # control reaches the stale-payload comparison independently of rollback behavior.
    $upgradeLog = Invoke-FixtureMsi $candidate '02-upgrade'
    Assert-UpgradeLog $upgradeLog
    Assert-RegisteredProduct $candidate $previous
    Assert-Hashes $expectedHashes (Get-TreeHashes $installRoot) 'Upgraded'
    Assert-Sentinels
    $maintenanceLog = Invoke-FixtureMsi $candidate '03-same-version-maintenance'
    Assert-Upgrade ($maintenanceLog -match 'Skipping action: LaunchPeggyAfterInstall \(condition is false\)' -and
        $maintenanceLog -notmatch 'PROPERTY CHANGE:.*PEGGY_TEST_LaunchPeggyAfterInstall') `
        'Same-version maintenance attempted to launch Peggy.'
    Assert-RegisteredProduct $candidate $previous
    Assert-Hashes $expectedHashes (Get-TreeHashes $installRoot) 'Maintenance'
    Assert-Sentinels
    $uninstallLog = Invoke-FixtureMsi $candidate '04-uninstall-candidate-for-rollback' -Uninstall
    Assert-Upgrade (@(Get-RelatedFixtureProducts).Count -eq 0) 'Fixture registration remains after uninstall.'
    Assert-Upgrade ($uninstallLog -match 'PROPERTY CHANGE:.*PEGGY_TEST_RemovePeggyUserData' -and
        $uninstallLog -match 'PROPERTY CHANGE:.*PEGGY_TEST_RemoveOwnedPeggyStartupEntry') `
        'Standalone uninstall did not exercise the cleanup/startup marker positive controls.'
    Assert-Sentinels
    if (Test-Path -LiteralPath $installRoot) {
        Assert-Upgrade ((Get-TreeHashes $installRoot).Count -eq 0) 'Fixture uninstall left payload files behind.'
    }
    Invoke-FixtureMsi $previous '05-reinstall-previous' | Out-Null
    Assert-RegisteredProduct $previous $candidate
    Assert-Hashes $installedPreviousHashes (Get-TreeHashes $installRoot) 'Rollback baseline'
    Assert-Sentinels
    $rollbackLog = Invoke-FixtureMsi $candidate '06-forced-upgrade-rollback' -ForceFailure
    Assert-UpgradeLog $rollbackLog -Rollback
    Assert-RegisteredProduct $previous $candidate
    Assert-Hashes $installedPreviousHashes (Get-TreeHashes $installRoot) 'Rollback'
    Assert-Sentinels
    Invoke-FixtureMsi $previous '07-uninstall-fixture' -Uninstall | Out-Null
    Assert-Upgrade (@(Get-RelatedFixtureProducts).Count -eq 0) 'Rollback fixture registration remains after uninstall.'
    Assert-Sentinels
    if (Test-Path -LiteralPath $installRoot) {
        Assert-Upgrade ((Get-TreeHashes $installRoot).Count -eq 0) 'Rollback fixture uninstall left payload files behind.'
    }
    $installerRegistryPath = "Registry::HKEY_CURRENT_USER\$registryKey\Installer"
    if (Test-Path -LiteralPath $installerRegistryPath) {
        $keys = @(Get-Item -LiteralPath $installerRegistryPath) +
            @(Get-ChildItem -LiteralPath $installerRegistryPath -Recurse)
        foreach ($key in $keys) {
            Assert-Upgrade ($key.Property.Count -eq 0) 'Fixture uninstall left installer registry values behind.'
        }
    }
    foreach ($fixture in @($previous, $candidate)) {
        Assert-Upgrade ((Get-FileHash -LiteralPath $fixture.OriginalPath -Algorithm SHA256).Hash -eq $fixture.OriginalHash) `
            'Original MSI was changed.'
    }
    Remove-Item -LiteralPath "Registry::HKEY_CURRENT_USER\$registryKey" -Recurse -Force
    foreach ($path in @($installRoot, $shortcutRoot, $dataRoot, $previous.Path, $candidate.Path)) {
        Assert-UnderRoot $path $runRoot
        Assert-NoReparsePath $path
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    $manifest.Status = 'passed-cleaned'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Host "PASS: real upgrade, exact $($expectedHashes.Count)-file payload, rollback, sentinels, and maintenance. Logs: $runRoot"
} catch {
    $failure = $_
    $manifest.Status = 'failed-artifacts-retained'
    $manifest.Error = $failure.Exception.Message
    if ($null -ne $previous) { $manifest.Previous = $previous | Select-Object * -ExcludeProperty Paths }
    if ($null -ne $candidate) { $manifest.Candidate = $candidate | Select-Object * -ExcludeProperty Paths }
    try { $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8 }
    catch { Write-Warning "Could not update manifest: $($_.Exception.Message)" }
    Write-Warning "Failure evidence retained at '$runRoot'; isolated registry: HKCU\$registryKey."
    foreach ($fixture in @($previous, $candidate)) {
        if ($null -ne $fixture) {
            Write-Warning "Fixture-only recovery, if registered: msiexec /x $($fixture.ProductCode) /qn /norestart"
        }
    }
    throw $failure
} finally {
    Release-Com $installer
}
