[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "peggy-setup-payload-$([Guid]::NewGuid().ToString('N'))"
$payload = Join-Path $root 'payload'
$output = Join-Path $root 'setup.wxs'
$generator = Join-Path $PSScriptRoot 'New-PeggyBootstrapperPayload.ps1'
$required = @(
    'peggy-setup.exe', 'peggy-setup.dll', 'peggy-setup.deps.json',
    'peggy-setup.runtimeconfig.json', 'peggy-tray.dll', 'Peggy.Core.dll',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
    'WixToolset.BootstrapperApplicationApi.dll', 'mbanative.dll',
    'scripts\Install-SpeechModels.ps1'
)
$assertions = 0
function Assert-SetupPayload([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Setup payload regression: $Message" }
    $script:assertions++
}
function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Assert-SetupPayload ($message -like "*$ExpectedMessage*") "Expected rejection '$ExpectedMessage', got '$message'."
}
try {
    foreach ($name in $required + @('Assets\Peggy.ico', 'locales\en-US\name & value.txt')) {
        $path = Join-Path $payload $name
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force)
        Set-Content -LiteralPath $path -Value "Synthetic fixture: $name" -Encoding ascii
    }
    & $generator -PayloadDirectory $payload -OutputPath $output | Out-Null
    $first = Get-Content -LiteralPath $output -Raw
    [xml]$xml = $first
    $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
    $group = $xml.SelectSingleNode('/w:Wix/w:Fragment/w:PayloadGroup[@Id="PeggyBootstrapperPayloads"]', $ns)
    Assert-SetupPayload ($null -ne $group) 'The BA payload group is missing.'
    $entries = @($group.SelectNodes('w:Payload', $ns))
    Assert-SetupPayload ($entries.Count -eq $required.Count + 1) 'Payload inventory is not exact.'
    Assert-SetupPayload (@($entries | Where-Object { $_.Name -eq 'peggy-setup.exe' }).Count -eq 0) `
        'The BA entry point must not be duplicated.'
    foreach ($entry in $entries) {
        Assert-SetupPayload ($entry.SourceFile -ceq (Join-Path $payload $entry.Name)) `
            'Source and destination relative paths differ.'
        Assert-SetupPayload (Test-Path -LiteralPath (Join-Path $payload $entry.Name)) `
            'Payload destination does not resolve to its input file.'
    }
    Assert-SetupPayload (@($entries | Where-Object { $_.Name -ceq 'locales\en-US\name & value.txt' }).Count -eq 1) `
        'Nested paths or XML characters were not preserved.'
    Assert-SetupPayload (@($entries.Id | Sort-Object -Unique).Count -eq $entries.Count) `
        'Payload identifiers must be unique.'
    & $generator -PayloadDirectory $payload -OutputPath $output | Out-Null
    Assert-SetupPayload ($first -ceq (Get-Content -LiteralPath $output -Raw)) 'Generation is not deterministic.'
    Assert-Rejected { & $generator -PayloadDirectory $payload -OutputPath (Join-Path $payload 'nested.wxs') } `
        'must be outside'
    foreach ($name in $required) {
        $path = Join-Path $payload $name
        Move-Item -LiteralPath $path -Destination "$path.saved"
        try {
            Assert-Rejected { & $generator -PayloadDirectory $payload -OutputPath $output } "missing '$name'"
        } finally {
            Move-Item -LiteralPath "$path.saved" -Destination $path
        }
    }
    Set-Content -LiteralPath (Join-Path $payload 'state.dat') -Value 'synthetic state'
    Assert-Rejected { & $generator -PayloadDirectory $payload -OutputPath $output } 'cannot be bundled'
    Write-Output "Peggy self-contained setup payload: $assertions assertions passed."
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
