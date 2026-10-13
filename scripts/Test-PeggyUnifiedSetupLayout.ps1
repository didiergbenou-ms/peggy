#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The real bootstrapper layout smoke test requires Windows.' }
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
. (Join-Path $PSScriptRoot 'PeggyMsiNative.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion $Version
$bundle = (Resolve-Path -LiteralPath (
    Join-Path $ArtifactDirectory $versionInfo.AssetNames.Bundle)).Path
$checksum = (Get-Content -LiteralPath "$bundle.sha256" -Raw).Trim()
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
if ($checksum -ine "$hash  $($versionInfo.AssetNames.Bundle)") {
    throw 'The setup EXE does not match its release checksum.'
}
$root = (New-Item -ItemType Directory -Path (
    Join-Path $ArtifactDirectory "layout-smoke-$([Guid]::NewGuid().ToString('N'))"
)).FullName
$layout = Join-Path $root 'layout'
$log = Join-Path $root 'layout.log'

function Get-InstallationSnapshot {
    $codes = @($versionInfo.ProductCode) + @(
        [PeggyMsiNative]::RelatedProducts(
            '{D661EFB2-155E-51B2-9951-C35E621D9E11}')
    )
    $products = @(foreach ($code in $codes | Sort-Object -Unique) {
        $state = [PeggyMsiNative]::ProductState([string]$code)
        "$code=$state"
    })
    $registrations = @(foreach ($hive in @(
            'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'Registry::HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
        if (Test-Path -LiteralPath $hive) {
            foreach ($key in Get-ChildItem -LiteralPath $hive) {
                $values = Get-ItemProperty -LiteralPath $key.PSPath
                if ($null -ne $values.PSObject.Properties['DisplayName'] -and $values.DisplayName -eq 'Peggy') {
                    $snapshot = [ordered]@{ Key = $key.Name }
                    foreach ($name in @('DisplayName', 'DisplayVersion', 'UninstallString', 'BundleUpgradeCode')) {
                        $property = $values.PSObject.Properties[$name]
                        if ($null -ne $property) { $snapshot[$name] = $property.Value }
                    }
                    [pscustomobject]$snapshot
                }
            }
        }
    })
    $installedFiles = @(foreach ($name in @('version.txt', 'peggy-tray.exe', 'peggy-tray.dll', 'Peggy.Core.dll')) {
        $path = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs\Peggy') $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            "$name=$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
        }
    })
    return [ordered]@{
        Products = $products
        Registrations = @($registrations | Sort-Object Key)
        InstalledFiles = $installedFiles
    } | ConvertTo-Json -Depth 5 -Compress
}

$before = Get-InstallationSnapshot
$process = Start-Process -FilePath $bundle -ArgumentList @(
    '/layout', "`"$layout`"", '/quiet', '/norestart', '/log', "`"$log`""
) -PassThru
if (-not $process.WaitForExit(180000)) {
    throw "Layout did not finish within three minutes (process $($process.Id)); inspect '$log'."
}
$process.Refresh()
if ($process.ExitCode -ne 0) {
    throw "The packaged BA layout failed with exit $($process.ExitCode); inspect '$log'."
}
$after = Get-InstallationSnapshot
if ($before -cne $after) { throw 'Layout changed a Peggy registration or installed application file.' }
$copied = @(Get-ChildItem -LiteralPath $layout -File -Recurse -Filter '*.exe')
if ($copied.Count -ne 1 -or
    (Get-FileHash -LiteralPath $copied[0].FullName -Algorithm SHA256).Hash -ne $hash) {
    throw 'Layout did not produce exactly the original self-contained setup EXE.'
}
# Embedded MSI payloads remain inside the copied EXE; extraction is checked separately.
Remove-Item -LiteralPath $layout -Recurse -Force
Write-Output "Peggy real bootstrapper layout passed without changing installed files or registration. Log: $log"
