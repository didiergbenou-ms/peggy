#requires -Version 7.0
<#
.SYNOPSIS
Verifies a setup EXE checksum and extracts its version-checked MSI without executing it.
.OUTPUTS
The canonical MSI path. WiX diagnostics remain in the unique extraction directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ChecksumPath = "$BundlePath.sha256"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Bundle inspection requires Windows and Windows Installer.' }
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion -Version $Version
$bundle = Get-Item -LiteralPath $BundlePath
if ($bundle.PSIsContainer -or $bundle.Extension -ine '.exe') {
    throw 'The baseline must be a setup EXE file.'
}
$checksum = Get-Content -LiteralPath $ChecksumPath -Raw
$pattern = '\A([a-fA-F0-9]{64})[ \t]+\*?' + [regex]::Escape($bundle.Name) + '\r?\n?\z'
if ($checksum -cnotmatch $pattern) {
    throw "Malformed setup checksum or filename mismatch for '$($bundle.Name)'."
}
$expectedHash = $Matches[1]
if ((Get-FileHash -LiteralPath $bundle.FullName -Algorithm SHA256).Hash -ine $expectedHash) {
    throw "Setup checksum does not match '$($bundle.Name)'."
}

$nugetRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $HOME '.nuget\packages'
} else { $env:NUGET_PACKAGES }
$wix = Join-Path $nugetRoot 'wixtoolset.sdk\6.0.2\tools\net6.0\wix.dll'
if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) {
    throw 'The pinned WiX 6.0.2 inspection command was not restored.'
}
# Never reuse an extraction tree: an old MSI must not satisfy a failed extraction.
$root = (New-Item -ItemType Directory -Path (
    Join-Path $OutputDirectory "bundle-$([Guid]::NewGuid().ToString('N'))"
)).FullName
$payloads = Join-Path $root 'payloads'
$work = Join-Path $root 'work'
New-Item -ItemType Directory -Path $work | Out-Null
$log = Join-Path $root 'extract.log'
& dotnet $wix burn extract -intermediateFolder $work $bundle.FullName `
    -o $payloads -oba (Join-Path $root 'ba') *> $log
if ($LASTEXITCODE -ne 0) { throw "WiX bundle extraction failed; see '$log'." }
$msis = @(Get-ChildItem -LiteralPath $payloads -Recurse -File -Force -Filter '*.msi')
if ($msis.Count -ne 1 -or $msis[0].Name -cne 'peggy-win-x64.msi') {
    throw 'The setup bundle must contain exactly one MSI named peggy-win-x64.msi.'
}

$installer = $database = $view = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($msis[0].FullName, 0)
    $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
    [void]$view.Execute()
    $properties = @{}
    while ($record = $view.Fetch()) {
        try {
            $name = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
            $properties[$name] = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(2))
        } finally {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }
    }
    $expected = @{
        ProductName = 'Peggy'
        ProductCode = $versionInfo.ProductCode
        ProductVersion = $versionInfo.ProductVersion
        UpgradeCode = '{D661EFB2-155E-51B2-9951-C35E621D9E11}'
    }
    foreach ($name in $expected.Keys) {
        if ($properties[$name] -ine $expected[$name]) {
            throw "Embedded MSI $name does not match Peggy $($versionInfo.Tag)."
        }
    }
} finally {
    try {
        if ($null -ne $view) { [void]$view.Close() }
    } finally {
        foreach ($object in @($view, $database, $installer)) {
            if ($null -ne $object) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($object)
            }
        }
    }
}

return (Resolve-Path -LiteralPath $msis[0].FullName).ProviderPath
