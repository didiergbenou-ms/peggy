#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('peggy-extract-test-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $root)
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Expand-PeggyBundleMsi.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Extractor script does not parse.' }
$definition = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Invoke-PeggyBundleExtraction'
}, $false))
if ($definition.Count -ne 1) { throw 'Expected one extraction helper.' }
. ([scriptblock]::Create($definition[0].Extent.Text))
$script:assertions = 0
function Assert-Extraction([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Extraction regression: $Message" }
    $script:assertions++
}
function Start-Sleep {
    param([int]$Seconds)
    $script:delays.Add($Seconds)
}
function dotnet {
    [CmdletBinding(PositionalBinding = $false)]
    param(
        [string]$o,
        [string]$oba,
        [string]$intermediateFolder,
        [switch]$v,
        [switch]$wx8503,
        [Parameter(ValueFromRemainingArguments)][string[]]$Arguments
    )
    $script:calls++
    Assert-Extraction ($Arguments[0] -eq $script:wix -and
        $Arguments[1] -eq 'burn' -and $Arguments[2] -eq 'extract') 'Only pinned, non-executing extraction is invoked.'
    Assert-Extraction ($v -and $wx8503) 'Detailed output and attached-container error escalation are enabled.'
    $payload = $o
    $work = $intermediateFolder
    $script:payloads.Add($payload)
    Assert-Extraction (-not (Test-Path -LiteralPath $payload)) 'Every attempt starts with an empty output path.'
    Assert-Extraction (Test-Path -LiteralPath $work -PathType Container) 'Fresh intermediate directory exists.'
    [void](New-Item -ItemType Directory -Path $payload)
    [IO.File]::WriteAllText((Join-Path $payload 'peggy-win-x64.msi'), "attempt-$script:calls")
    if ($script:mode -eq 'mutate') {
        [IO.File]::AppendAllText($script:bundle, 'unexpected mutation')
    }
    $script:LASTEXITCODE = if ($script:mode -eq 'recover' -and $script:calls -ge 2) { 0 }
        elseif ($script:mode -eq 'success' -or $script:mode -eq 'mutate-success') { 0 } else { 5 }
    if ($script:mode -eq 'mutate-success') {
        [IO.File]::AppendAllText($script:bundle, 'unexpected mutation')
    }
    "Synthetic WiX output for attempt $script:calls"
}
try {
    $script:wix = Join-Path $root 'pinned-wix.dll'
    $script:bundle = Join-Path $root 'synthetic-setup.exe'
    foreach ($mode in @('success', 'recover', 'permanent', 'mutate', 'mutate-success')) {
        $script:mode = $mode
        [IO.File]::WriteAllText($script:bundle, 'unchanged synthetic EXE - never executed')
        $hash = (Get-FileHash -LiteralPath $script:bundle).Hash
        $script:calls = 0
        $script:delays = [Collections.Generic.List[int]]::new()
        $script:payloads = [Collections.Generic.List[string]]::new()
        $result = @()
        $failure = ''
        $destination = Join-Path $root $mode
        try {
            $result = @(Invoke-PeggyBundleExtraction -WixPath $script:wix -Bundle $script:bundle `
                -Destination $destination -ExpectedHash $hash)
        } catch { $failure = $_.Exception.Message }
        $attempts = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Filter 'attempt.json')
        foreach ($attempt in $attempts) {
            $metadata = Get-Content -LiteralPath $attempt.FullName -Raw | ConvertFrom-Json
            Assert-Extraction ($metadata.ExpectedSha256 -eq $hash -and $metadata.StartedUtc -and
                $metadata.ElapsedMilliseconds -ge 0 -and $metadata.WixVersion -eq '6.0.2') 'Attempt evidence is preserved.'
            Assert-Extraction ((Get-Content -LiteralPath (Join-Path $attempt.DirectoryName 'extract.log') -Raw) `
                -match 'Synthetic WiX output') 'Tool output survives a failed attempt.'
        }
        if ($mode -in @('success', 'recover')) {
            $expectedCalls = if ($mode -eq 'success') { 1 } else { 2 }
            Assert-Extraction ($failure.Length -eq 0 -and $result.Count -eq 1 -and
                $result[0] -eq $script:payloads[-1] -and $script:calls -eq $expectedCalls) `
                "Success returns only the successful attempt path ($mode; calls=$script:calls; failure=$failure)."
            Assert-Extraction ((Get-Content -LiteralPath (Join-Path $result[0] 'peggy-win-x64.msi') -Raw) `
                -eq "attempt-$expectedCalls") 'A stale failed-attempt MSI is never accepted.'
            Assert-Extraction ($script:delays.Count -eq $expectedCalls - 1) 'Backoff only follows a failed native invocation.'
        } elseif ($mode -eq 'permanent') {
            Assert-Extraction ($failure -match 'after 3 attempts' -and $script:calls -eq 3 -and
                $result.Count -eq 0 -and $attempts.Count -eq 3) 'Permanent failure stays fatal with all attempt evidence.'
            Assert-Extraction (($script:delays -join ',') -eq '2,4') 'Retry bound/backoff is deterministic.'
        } else {
            Assert-Extraction ($failure -match 'checksum changed' -and $script:calls -eq 1 -and
                $result.Count -eq 0) 'Changed bundle bytes are never retried or accepted.'
        }
        Assert-Extraction (@($script:payloads | Sort-Object -Unique).Count -eq $script:calls) 'Attempts never reuse extraction paths.'
    }
    $before = $script:calls
    $failure = ''
    try {
        Invoke-PeggyBundleExtraction -WixPath $script:wix -Bundle $script:bundle `
            -Destination (Join-Path $root 'bad-checksum') -ExpectedHash ('0' * 64) | Out-Null
    } catch { $failure = $_.Exception.Message }
    Assert-Extraction ($failure -match 'checksum changed' -and $script:calls -eq $before) 'Checksum failure performs no extraction.'
    Write-Host "Peggy bounded bundle extraction tests passed: $script:assertions assertions."
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
