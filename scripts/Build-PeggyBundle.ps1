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
$setupOutput = Join-Path $buildRoot 'setup-bootstrapper'
$bundleOutput = Join-Path $buildRoot 'bundle'
$bundleIntermediate = Join-Path $buildRoot 'obj-bundle'
foreach ($path in @($setupOutput, $bundleOutput, $bundleIntermediate)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$setupProject = Join-Path (
    Join-Path $repoRoot 'installer\Peggy.SetupBootstrapper'
) 'Peggy.SetupBootstrapper.csproj'
& dotnet publish $setupProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:Version=$($versionInfo.SemanticVersion) `
    -p:FileVersion=$($versionInfo.FileVersion) `
    -p:PeggyIncludeRuntimeHelpers=false `
    -o $setupOutput
if ($LASTEXITCODE -ne 0) {
    throw "Peggy unified setup publish failed (exit $LASTEXITCODE)."
}
$setupPath = Join-Path $setupOutput 'peggy-setup.exe'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw 'The Peggy unified setup executable was not produced.'
}
$setupScripts = Join-Path $setupOutput 'scripts'
New-Item -ItemType Directory -Path $setupScripts -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-SpeechModels.ps1') `
    -Destination $setupScripts -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') `
    -Destination $setupOutput -Force
$setupSource = Join-Path $buildRoot 'Peggy.Setup.generated.wxs'
& (Join-Path $PSScriptRoot 'New-PeggyBootstrapperPayload.ps1') `
    -PayloadDirectory $setupOutput -OutputPath $setupSource

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
    "-p:BootstrapperPath=$setupPath",
    "-p:BootstrapperPayloadsPath=$setupSource",
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
