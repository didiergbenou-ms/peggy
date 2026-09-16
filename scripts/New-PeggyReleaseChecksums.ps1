[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactDirectory,

    [switch]$RequireBootstrapper
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$artifacts = @(
    [pscustomobject]@{
        Name = 'peggy-win-x64.zip'
        ChecksumName = 'peggy-win-x64.sha256'
        Required = $true
    },
    [pscustomobject]@{
        Name = 'peggy-win-x64.msi'
        ChecksumName = 'peggy-win-x64.msi.sha256'
        Required = $true
    },
    [pscustomobject]@{
        Name = 'peggy-win-x64-setup.exe'
        ChecksumName = 'peggy-win-x64-setup.exe.sha256'
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
