[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ArtifactDirectory,

    [switch]$RequireBootstrapper
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$assetNames = (ConvertTo-PeggyInstallerVersion -Version $Version).AssetNames
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$artifacts = @(
    [pscustomobject]@{
        Name = $assetNames.Zip
        ChecksumName = $assetNames.ZipChecksum
        Required = $true
    },
    [pscustomobject]@{
        Name = $assetNames.Msi
        ChecksumName = $assetNames.MsiChecksum
        Required = $true
    },
    [pscustomobject]@{
        Name = $assetNames.Bundle
        ChecksumName = $assetNames.BundleChecksum
        Required = [bool]$RequireBootstrapper
    }
)

foreach ($artifact in $artifacts) {
    $path = Join-Path $artifactRoot $artifact.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if ($artifact.Required) {
            throw "Required release artifact '$($artifact.Name)' was not found."
        }
        continue
    }

    $hash = (
        Get-FileHash -LiteralPath $path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    Set-Content `
        -LiteralPath (Join-Path $artifactRoot $artifact.ChecksumName) `
        -Value "$hash  $($artifact.Name)" `
        -Encoding ascii
}
