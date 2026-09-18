[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$MsiPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$IconPath = (
        Join-Path (
            Split-Path -Parent $PSScriptRoot
        ) 'src\Peggy.Tray\Assets\Peggy.ico'
    ),

    [string]$Configuration = 'Release',

    [switch]$SkipChecksum
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion -Version $Version
$assetNames = $versionInfo.AssetNames
$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
$resolvedIcon = (Resolve-Path -LiteralPath $IconPath).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
foreach ($artifactName in @(
        $assetNames.Bundle,
        $assetNames.BundleChecksum
    )) {
    Remove-Item `
        -LiteralPath (Join-Path $outputRoot $artifactName) `
        -Force `
        -ErrorAction SilentlyContinue
}

$buildRoot = Join-Path $outputRoot 'build'
$checkerOutput = Join-Path $buildRoot 'prerequisite-check'
$bundleOutput = Join-Path $buildRoot 'bundle'
$bundleIntermediate = Join-Path $buildRoot 'obj-bundle'
foreach ($path in @($checkerOutput, $bundleOutput, $bundleIntermediate)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$checkerProject = Join-Path (
    Join-Path $repoRoot 'installer\Peggy.PrerequisiteCheck'
) 'Peggy.PrerequisiteCheck.csproj'
& dotnet publish $checkerProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:Version=$($versionInfo.SemanticVersion) `
    -o $checkerOutput
if ($LASTEXITCODE -ne 0) {
    throw "Peggy prerequisite checker publish failed (exit $LASTEXITCODE)."
}
$checkerPath = Join-Path $checkerOutput 'peggy-prerequisite-check.exe'
if (-not (Test-Path -LiteralPath $checkerPath -PathType Leaf)) {
    throw 'The Peggy prerequisite checker was not produced.'
}

$bundleProject = Join-Path $repoRoot 'installer\Peggy.Bundle.wixproj'
$bundleArguments = @(
    'build',
    $bundleProject,
    '-c', $Configuration,
    '--nologo',
    "-p:BundleVersion=$($versionInfo.BundleVersion)",
    "-p:ProductCode=$($versionInfo.ProductCode)",
    "-p:SemanticVersion=$($versionInfo.SemanticVersion)",
    "-p:MsiPath=$resolvedMsi",
    "-p:PrerequisiteCheckerPath=$checkerPath",
    "-p:IconPath=$resolvedIcon",
    "-p:BaseIntermediateOutputPath=$bundleIntermediate\",
    '-o', $bundleOutput
)
& dotnet @bundleArguments
if ($LASTEXITCODE -ne 0) {
    throw "Peggy bootstrapper build failed (exit $LASTEXITCODE)."
}

$builtBundle = Get-ChildItem `
    -LiteralPath $bundleOutput `
    -Filter 'peggy-win-x64-setup.exe' `
    -File `
    -Recurse |
    Select-Object -First 1
if ($null -eq $builtBundle) {
    throw 'The Peggy bootstrapper was not produced.'
}
$finalBundle = Join-Path $outputRoot $assetNames.Bundle
Copy-Item -LiteralPath $builtBundle.FullName -Destination $finalBundle -Force

if (-not $SkipChecksum) {
    & (Join-Path $PSScriptRoot 'New-PeggyReleaseChecksums.ps1') `
        -Version $versionInfo.SemanticVersion `
        -ArtifactDirectory $outputRoot `
        -RequireBootstrapper
}

Write-Output $finalBundle
