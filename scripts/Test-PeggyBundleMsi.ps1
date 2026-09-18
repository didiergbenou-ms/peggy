#requires -Version 7.0
<#
.SYNOPSIS
Tests checksum rejection and non-executing extraction against a real setup/MSI pair.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$BundlePath,
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$extract = Join-Path $PSScriptRoot 'Expand-PeggyBundleMsi.ps1'
$bundle = Get-Item -LiteralPath $BundlePath
$hash = (Get-FileHash -LiteralPath $bundle.FullName -Algorithm SHA256).Hash
$root = (New-Item -ItemType Directory -Path (
    Join-Path $OutputDirectory "test-$([Guid]::NewGuid().ToString('N'))"
)).FullName

function Assert-Bundle {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Bundle MSI regression: $Message" }
}

function Assert-RejectedChecksum {
    param([string]$Name, [string]$Content, [string]$ErrorPattern, [string]$InputBundle = $bundle.FullName)
    $checksum = Join-Path $root "$Name.sha256"
    $output = Join-Path $root "$Name-output"
    Set-Content -LiteralPath $checksum -Value $Content -NoNewline
    $failure = $null
    $result = @()
    try {
        $result = @(& $extract -Version $Version -BundlePath $InputBundle `
            -ChecksumPath $checksum -OutputDirectory $output)
    } catch { $failure = $_.Exception.Message }
    Assert-Bundle ($failure -match $ErrorPattern) "$Name did not fail for the expected checksum error: $failure"
    Assert-Bundle ($result.Count -eq 0) "$Name returned an MSI path."
    Assert-Bundle (-not (Test-Path -LiteralPath $output)) "$Name extracted before verifying the checksum."
}

$passed = $false
try {
    Assert-RejectedChecksum 'wrong-filename' "$hash  another-setup.exe`n" 'filename mismatch'
    Assert-RejectedChecksum 'wrong-hash' "$('0' * 64)  $($bundle.Name)`n" 'checksum does not match'
    Assert-RejectedChecksum 'invalid-hash' "$('x' * 64)  $($bundle.Name)`n" 'Malformed setup checksum'
    Assert-RejectedChecksum 'multiple-lines' "$hash  $($bundle.Name)`n$hash  $($bundle.Name)`n" 'Malformed setup checksum'
    Assert-RejectedChecksum 'path-filename' "$hash  .\$($bundle.Name)`n" 'filename mismatch'

    $corruptRoot = New-Item -ItemType Directory -Path (Join-Path $root 'corrupt')
    $corrupt = Join-Path $corruptRoot.FullName $bundle.Name
    Copy-Item -LiteralPath $bundle.FullName -Destination $corrupt
    $stream = [IO.File]::Open($corrupt, 'Open', 'ReadWrite')
    try {
        $firstByte = $stream.ReadByte()
        $stream.Position = 0
        $stream.WriteByte([byte]($firstByte -bxor 1))
    } finally { $stream.Dispose() }
    Assert-RejectedChecksum 'corrupt-bundle' "$hash  $($bundle.Name)`n" 'checksum does not match' $corrupt

    # A stale MSI in the supplied parent must never participate in discovery.
    $output = Join-Path $root 'positive'
    New-Item -ItemType Directory -Path $output | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'peggy-win-x64.msi') -Value 'stale'
    $result = @(& $extract -Version $Version -BundlePath $bundle.FullName -OutputDirectory $output)
    Assert-Bundle ($result.Count -eq 1 -and $result[0] -is [string]) 'Extraction must return only one path.'
    $msi = $result[0]
    Assert-Bundle ($msi -ceq (Resolve-Path -LiteralPath $msi).ProviderPath) 'The MSI path is not canonical.'
    Assert-Bundle ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash) 'The embedded MSI bytes changed.'

    $binaryChecksum = Join-Path $root 'binary.sha256'
    Set-Content -LiteralPath $binaryChecksum -NoNewline -Value "$($hash.ToLowerInvariant()) *$($bundle.Name)`r`n"
    $repeat = @(& $extract -Version $Version -BundlePath $bundle.FullName `
        -ChecksumPath $binaryChecksum -OutputDirectory $output)
    Assert-Bundle ($repeat.Count -eq 1 -and $repeat[0] -cne $msi) 'Extraction reused a prior path.'
    Assert-Bundle ((Get-FileHash -LiteralPath $repeat[0] -Algorithm SHA256).Hash -eq
        (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash) 'Repeated extraction changed the MSI.'

    $wrongVersion = if ($Version.TrimStart('v') -eq '0.0.0') { '0.0.1' } else { '0.0.0' }
    $failure = $null
    $result = @()
    try {
        $result = @(& $extract -Version $wrongVersion -BundlePath $bundle.FullName -OutputDirectory $output)
    } catch { $failure = $_.Exception.Message }
    Assert-Bundle ($failure -match 'Embedded MSI Product(Code|Version) does not match') `
        "The wrong semantic version was not rejected: $failure"
    Assert-Bundle ($result.Count -eq 0) 'A mismatched MSI identity returned a path.'
    $passed = $true
    Write-Host 'Bundle MSI regressions passed: checksum/filename/corruption rejection, exact MSI bytes, fresh paths, and version identity.'
} finally {
    if ($passed) {
        Remove-Item -LiteralPath $root -Recurse -Force
    } else {
        Write-Warning "Bundle MSI regression evidence retained at '$root'."
    }
}
