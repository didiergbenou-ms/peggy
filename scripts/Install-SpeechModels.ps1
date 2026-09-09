[CmdletBinding()]
param(
    [string]$Destination = (
        Join-Path $env:LOCALAPPDATA 'Peggy\models'
    ),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-OfficialUrl {
    param([Parameter(Mandatory)][string]$Url)

    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com') {
        throw "Refusing non-official speech model URL: $Url"
    }
}

function Get-VerifiedAsset {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$OutputPath
    )

    Assert-OfficialUrl $Url
    $partialPath = "$OutputPath.partial"
    if (Test-Path -LiteralPath $partialPath) {
        Remove-Item -LiteralPath $partialPath -Force
    }

    Write-Host "Downloading $(Split-Path $OutputPath -Leaf)..."
    & curl.exe -L --fail --retry 3 --silent --show-error `
        --output $partialPath $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed: $Url"
    }

    $actual = (Get-FileHash -LiteralPath $partialPath -Algorithm SHA256).
        Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $partialPath -Force
        throw "SHA-256 mismatch for $Url. Expected $Sha256, received $actual."
    }

    Move-Item -LiteralPath $partialPath -Destination $OutputPath -Force
}

function Install-Archive {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string[]]$RequiredFiles
    )

    $target = Join-Path $Destination $Name
    $complete = Test-Path -LiteralPath $target -PathType Container
    foreach ($relativePath in $RequiredFiles) {
        $complete = $complete -and (
            Test-Path -LiteralPath (
                Join-Path $target $relativePath
            )
        )
    }
    if ($complete -and -not $Force) {
        Write-Host "$Name is already installed."
        return
    }
    if ((Test-Path -LiteralPath $target) -and -not $Force) {
        Write-Host "$Name is incomplete and will be repaired."
    }

    $archive = Join-Path $Destination "$Name.tar.bz2"
    Get-VerifiedAsset -Url $Url -Sha256 $Sha256 -OutputPath $archive
    $staging = Join-Path $Destination ".staging-$Name-$PID"
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    New-Item -ItemType Directory -Path $staging | Out-Null

    try {
        Write-Host "Extracting $Name..."
        & tar.exe -xjf $archive -C $staging
        if ($LASTEXITCODE -ne 0) {
            throw "Extraction failed: $archive"
        }

        $source = Join-Path $staging $Name
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            $directories = @(
                Get-ChildItem -LiteralPath $staging -Directory
            )
            if ($directories.Count -ne 1) {
                throw "The archive did not contain one model directory."
            }
            $source = $directories[0].FullName
        }

        foreach ($relativePath in $RequiredFiles) {
            $required = Join-Path $source $relativePath
            if (-not (Test-Path -LiteralPath $required)) {
                throw "Downloaded model is missing: $required"
            }
        }

        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        Move-Item -LiteralPath $source -Destination $target

        foreach ($relativePath in $RequiredFiles) {
            $required = Join-Path $target $relativePath
            if (-not (Test-Path -LiteralPath $required)) {
                throw "Installed model is missing: $required"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        if (Test-Path -LiteralPath $archive) {
            Remove-Item -LiteralPath $archive -Force
        }
    }
}

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw 'Windows curl.exe is required to download the speech models.'
}
if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
    throw 'Windows tar.exe is required to extract the speech models.'
}

$Destination = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($Destination)
)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$asrName = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8'
$asrUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2'
$asrHash = '157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad'
Install-Archive `
    -Name $asrName `
    -Url $asrUrl `
    -Sha256 $asrHash `
    -RequiredFiles @(
        'encoder.int8.onnx',
        'decoder.int8.onnx',
        'joiner.int8.onnx',
        'tokens.txt'
    )

$streamingName =
    'sherpa-onnx-nemo-streaming-fast-conformer-ctc-en-80ms-int8'
$streamingUrl =
    'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/' +
    "$streamingName.tar.bz2"
$streamingHash =
    '479759fbd5c69c909e7175d7773105a1bfabf82fa533de68c546c89d85f234e8'
Install-Archive `
    -Name $streamingName `
    -Url $streamingUrl `
    -Sha256 $streamingHash `
    -RequiredFiles @(
        'model.int8.onnx',
        'tokens.txt'
    )

$ttsName = 'kitten-nano-en-v0_8-int8'
$ttsUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kitten-nano-en-v0_8-int8.tar.bz2'
$ttsHash = '6fa5be852612ce761094ba74ee6123b4fc4acfefa79bf64dc63acae4a83af2fd'
Install-Archive `
    -Name $ttsName `
    -Url $ttsUrl `
    -Sha256 $ttsHash `
    -RequiredFiles @(
        'model.int8.onnx',
        'voices.bin',
        'tokens.txt',
        'espeak-ng-data'
    )

$vadName = 'silero_vad_v5.onnx'
$vadUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad_v5.onnx'
$vadHash = '6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396'
$vadPath = Join-Path $Destination $vadName
$vadValid = (Test-Path -LiteralPath $vadPath -PathType Leaf) -and (
    (Get-FileHash -LiteralPath $vadPath -Algorithm SHA256).
        Hash.ToLowerInvariant() -eq $vadHash
)
if (-not $vadValid) {
    if ((Test-Path -LiteralPath $vadPath) -and -not $Force) {
        Write-Host "$vadName is invalid and will be repaired."
    }
    Get-VerifiedAsset -Url $vadUrl -Sha256 $vadHash -OutputPath $vadPath
}
else {
    Write-Host "$vadName is already installed."
}

$manifest = [ordered]@{
    schemaVersion = 1
    language = 'en'
    installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    assets = @(
        [ordered]@{
            name = "$asrName.tar.bz2"
            sha256 = $asrHash
            source = $asrUrl
        },
        [ordered]@{
            name = "$ttsName.tar.bz2"
            sha256 = $ttsHash
            source = $ttsUrl
        },
        [ordered]@{
            name = "$streamingName.tar.bz2"
            sha256 = $streamingHash
            source = $streamingUrl
        },
        [ordered]@{
            name = $vadName
            sha256 = $vadHash
            source = $vadUrl
        }
    )
}
$manifestPath = Join-Path $Destination 'speech-models.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 5
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, $manifestJson, $utf8NoBom)

Write-Host "Peggy's English speech models are ready in $Destination"
