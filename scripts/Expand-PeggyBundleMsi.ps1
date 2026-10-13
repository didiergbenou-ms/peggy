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

function Invoke-PeggyBundleExtraction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WixPath,
        [Parameter(Mandatory)][string]$Bundle,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedHash,
        [ValidateRange(1, 3)][int]$MaximumAttempts = 3
    )

    # Handle native exit status here, even when the calling shell promotes it to an error.
    $PSNativeCommandUseErrorActionPreference = $false
    $operationId = [Guid]::NewGuid().ToString('N')
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        if ((Get-FileHash -LiteralPath $Bundle -Algorithm SHA256).Hash -ine $ExpectedHash) {
            throw 'Setup checksum changed before extraction; refusing to retry.'
        }
        # Failed attempts may leave partial files; none may satisfy the next attempt.
        $attemptRoot = (New-Item -ItemType Directory -Path (
            Join-Path $Destination "bundle-$([Guid]::NewGuid().ToString('N'))"
        )).FullName
        $payloads = Join-Path $attemptRoot 'payloads'
        $work = Join-Path $attemptRoot 'work'
        New-Item -ItemType Directory -Path $work | Out-Null
        $log = Join-Path $attemptRoot 'extract.log'
        $started = [DateTimeOffset]::UtcNow
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & dotnet $WixPath burn extract -v -wx8503 -intermediateFolder $work $Bundle `
            -o $payloads -oba (Join-Path $attemptRoot 'ba') *> $log
        $exitCode = $LASTEXITCODE
        $watch.Stop()
        [ordered]@{
            OperationId = $operationId
            Attempt = $attempt
            StartedUtc = $started.ToString('O')
            ElapsedMilliseconds = $watch.ElapsedMilliseconds
            ExitCode = $exitCode
            ExitCodeHex = '0x{0:X8}' -f $exitCode
            BundleName = [IO.Path]::GetFileName($Bundle)
            BundleBytes = (Get-Item -LiteralPath $Bundle).Length
            ExpectedSha256 = $ExpectedHash.ToLowerInvariant()
            WixVersion = '6.0.2'
            RunnerImage = $env:ImageOS
            RunnerImageVersion = $env:ImageVersion
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $attemptRoot 'attempt.json') -Encoding utf8
        if ($exitCode -eq 0) {
            if ((Get-FileHash -LiteralPath $Bundle -Algorithm SHA256).Hash -ine $ExpectedHash) {
                throw 'Setup checksum changed during extraction; refusing extracted files.'
            }
            Write-Host "WiX extraction passed: $([IO.Path]::GetFileName($Bundle)), attempt $attempt/$MaximumAttempts."
            return $payloads
        }

        Write-Warning "WiX extraction failed: $([IO.Path]::GetFileName($Bundle)), attempt $attempt/$MaximumAttempts, exit $exitCode. Log: $log"
        foreach ($line in @(Get-Content -LiteralPath $log -Tail 40)) {
            # Prefix tool output so an embedded workflow command cannot be executed.
            Write-Host "WiX diagnostic: $line"
        }
        if ($attempt -eq $MaximumAttempts) {
            throw "WiX bundle extraction failed after $MaximumAttempts attempts (exit $exitCode); diagnostics under '$Destination', operation $operationId."
        }
        Start-Sleep -Seconds (2 * $attempt)
    }
}

if (-not $IsWindows) { throw 'Bundle inspection requires Windows and Windows Installer.' }
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
. (Join-Path $PSScriptRoot 'PeggyMsiNative.ps1')
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
$payloads = Invoke-PeggyBundleExtraction -WixPath $wix -Bundle $bundle.FullName `
    -Destination $OutputDirectory -ExpectedHash $expectedHash
$msis = @(Get-ChildItem -LiteralPath $payloads -Recurse -File -Force -Filter '*.msi')
if ($msis.Count -ne 1 -or $msis[0].Name -cne 'peggy-win-x64.msi') {
    throw 'The setup bundle must contain exactly one MSI named peggy-win-x64.msi.'
}

$database = [IntPtr]::Zero
$view = [IntPtr]::Zero
try {
    $database = [PeggyMsiNative]::OpenDatabase($msis[0].FullName, 0)
    $view = [PeggyMsiNative]::OpenView(
        $database,
        'SELECT `Property`, `Value` FROM `Property`')
    [PeggyMsiNative]::ExecuteView($view)
    $properties = @{}
    while ($true) {
        $record = [IntPtr]::Zero
        if (-not [PeggyMsiNative]::Fetch($view, [ref]$record)) {
            break
        }
        try {
            $name = [PeggyMsiNative]::RecordString($record, 1)
            $properties[$name] = [PeggyMsiNative]::RecordString($record, 2)
        } finally {
            [PeggyMsiNative]::Close($record)
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
    if ($view -ne [IntPtr]::Zero) {
        [PeggyMsiNative]::Close($view)
    }
    if ($database -ne [IntPtr]::Zero) {
        [PeggyMsiNative]::Close($database)
    }
}

return (Resolve-Path -LiteralPath $msis[0].FullName).ProviderPath
