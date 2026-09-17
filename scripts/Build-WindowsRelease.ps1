[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$OutputDirectory = (
        Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'
    ),

    [string]$Configuration = 'Release',

    [switch]$SkipBundle,

    [switch]$SkipChecksums
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$versionInfo = ConvertTo-PeggyInstallerVersion -Version $Version
$iconPath = (
    Resolve-Path -LiteralPath (
        Join-Path $repoRoot 'src\Peggy.Tray\Assets\Peggy.ico'
    )
).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
foreach ($artifactName in @(
        'peggy-win-x64.zip',
        'peggy-win-x64.sha256',
        'peggy-win-x64.msi',
        'peggy-win-x64.msi.sha256',
        'peggy-win-x64-setup.exe',
        'peggy-win-x64-setup.exe.sha256'
    )) {
    Remove-Item `
        -LiteralPath (Join-Path $outputRoot $artifactName) `
        -Force `
        -ErrorAction SilentlyContinue
}

$publishDirectory = Join-Path $outputRoot 'peggy-win-x64'
$buildRoot = Join-Path $outputRoot 'build'
$checkerOutput = Join-Path $buildRoot 'prerequisite-check'
$cleanupOutput = Join-Path $buildRoot 'uninstall-cleanup'
$msiOutput = Join-Path $buildRoot 'msi'
$msiIntermediate = Join-Path $buildRoot 'obj-msi'
foreach ($path in @(
        $publishDirectory,
        $checkerOutput,
        $cleanupOutput,
        $msiOutput,
        $msiIntermediate
    )) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$trayProject = Join-Path $repoRoot 'src\Peggy.Tray\Peggy.Tray.csproj'
& dotnet publish $trayProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:Version=$($versionInfo.SemanticVersion) `
    -p:FileVersion=$($versionInfo.FileVersion) `
    -p:ContinuousIntegrationBuild=true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Peggy publish failed (exit $LASTEXITCODE)."
}

$scriptsDirectory = Join-Path $publishDirectory 'scripts'
New-Item -ItemType Directory -Path $scriptsDirectory -Force | Out-Null
foreach ($file in @(
        'install.ps1',
        'uninstall.ps1',
        'README.md',
        'THIRD-PARTY-NOTICES.txt'
    )) {
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot $file) `
        -Destination $publishDirectory `
        -Force
}
foreach ($script in @(
        'Install-SpeechModels.ps1',
        'Collect-PeggyDiagnostics.ps1',
        'Remove-PeggyStartupEntry.ps1'
    )) {
    Copy-Item `
        -LiteralPath (Join-Path $PSScriptRoot $script) `
        -Destination $scriptsDirectory `
        -Force
}
Set-Content `
    -LiteralPath (Join-Path $publishDirectory 'version.txt') `
    -Value $versionInfo.Tag `
    -Encoding ascii

$cleanupProject = Join-Path (
    Join-Path $repoRoot 'installer\Peggy.UninstallCleanup'
) 'Peggy.UninstallCleanup.csproj'
& dotnet publish $cleanupProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --nologo `
    -p:Version=$($versionInfo.SemanticVersion) `
    -p:FileVersion=$($versionInfo.FileVersion) `
    -o $cleanupOutput
if ($LASTEXITCODE -ne 0) {
    throw "Peggy uninstall cleanup publish failed (exit $LASTEXITCODE)."
}
$cleanupPath = Join-Path $cleanupOutput 'peggy-uninstall-cleanup.exe'
if (-not (Test-Path -LiteralPath $cleanupPath -PathType Leaf)) {
    throw 'The Peggy uninstall cleanup executable was not produced.'
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
    -p:FileVersion=$($versionInfo.FileVersion) `
    -o $checkerOutput
if ($LASTEXITCODE -ne 0) {
    throw "Peggy prerequisite checker publish failed (exit $LASTEXITCODE)."
}
$checkerPath = Join-Path $checkerOutput 'peggy-prerequisite-check.exe'
if (-not (Test-Path -LiteralPath $checkerPath -PathType Leaf)) {
    throw 'The Peggy prerequisite checker was not produced.'
}

$zipPath = Join-Path $outputRoot 'peggy-win-x64.zip'
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive `
    -Path (Join-Path $publishDirectory '*') `
    -DestinationPath $zipPath `
    -CompressionLevel Optimal

$payloadSource = Join-Path $buildRoot 'Peggy.Payload.generated.wxs'
& (Join-Path $PSScriptRoot 'New-PeggyPayloadWixSource.ps1') `
    -PayloadDirectory $publishDirectory `
    -OutputPath $payloadSource
if ($LASTEXITCODE -ne 0) {
    throw "WiX payload generation failed (exit $LASTEXITCODE)."
}

$installerProject = Join-Path $repoRoot 'installer\Peggy.Installer.wixproj'
$msiArguments = @(
    'build',
    $installerProject,
    '-c', $Configuration,
    '--nologo',
    "-p:ProductVersion=$($versionInfo.ProductVersion)",
    "-p:SemanticVersion=$($versionInfo.SemanticVersion)",
    "-p:ProductCode=$($versionInfo.ProductCode)",
    "-p:PrerequisiteCheckerPath=$checkerPath",
    "-p:UninstallCleanupPath=$cleanupPath",
    "-p:PayloadDirectory=$publishDirectory",
    "-p:GeneratedPayloadSource=$payloadSource",
    "-p:IconPath=$iconPath",
    "-p:BaseIntermediateOutputPath=$msiIntermediate\",
    '-o', $msiOutput
)
& dotnet @msiArguments
if ($LASTEXITCODE -ne 0) {
    throw "Peggy MSI build failed (exit $LASTEXITCODE)."
}

$builtMsi = Get-ChildItem `
    -LiteralPath $msiOutput `
    -Filter 'peggy-win-x64.msi' `
    -File `
    -Recurse |
    Select-Object -First 1
if ($null -eq $builtMsi) {
    throw 'The Peggy MSI was not produced.'
}
$finalMsi = Join-Path $outputRoot 'peggy-win-x64.msi'
Copy-Item -LiteralPath $builtMsi.FullName -Destination $finalMsi -Force

if (-not $SkipBundle) {
    & (Join-Path $PSScriptRoot 'Build-PeggyBundle.ps1') `
        -Version $versionInfo.SemanticVersion `
        -MsiPath $finalMsi `
        -IconPath $iconPath `
        -OutputDirectory $outputRoot `
        -Configuration $Configuration `
        -SkipChecksum
}

if (-not $SkipChecksums) {
    & (Join-Path $PSScriptRoot 'New-PeggyReleaseChecksums.ps1') `
        -ArtifactDirectory $outputRoot `
        -RequireBootstrapper:(-not $SkipBundle)
}

[pscustomobject]@{
    Version = $versionInfo.SemanticVersion
    ProductVersion = $versionInfo.ProductVersion
    FileVersion = $versionInfo.FileVersion
    ProductCode = $versionInfo.ProductCode
    PublishDirectory = $publishDirectory
    Zip = $zipPath
    Msi = $finalMsi
    Bundle = if ($SkipBundle) {
        $null
    } else {
        Join-Path $outputRoot 'peggy-win-x64-setup.exe'
    }
}
