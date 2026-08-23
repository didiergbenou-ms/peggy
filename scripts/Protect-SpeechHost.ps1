#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$HostPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($HostPath)) {
    $outputRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot (
            '..\src\Peggy.Tray\bin\Release\net10.0-windows'
        ))
    )
    $candidatePaths = @(
        (Join-Path $PSScriptRoot (
            '..\Peggy.Speech.Host\peggy-speech-host.exe'
        )),
        (Join-Path $outputRoot (
            'publish\Peggy.Speech.Host\peggy-speech-host.exe'
        )),
        (Join-Path $outputRoot (
            'Peggy.Speech.Host\peggy-speech-host.exe'
        ))
    )
    if (Test-Path -LiteralPath $outputRoot -PathType Container) {
        $candidatePaths += @(
            Get-ChildItem -Path (
                Join-Path $outputRoot (
                    '*\Peggy.Speech.Host\peggy-speech-host.exe'
                )
            ) -File -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName
        )
        $candidatePaths += @(
            Get-ChildItem -Path (
                Join-Path $outputRoot (
                    '*\publish\Peggy.Speech.Host\peggy-speech-host.exe'
                )
            ) -File -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName
        )
    }
    $HostPath = $candidatePaths |
        Where-Object {
            Test-Path -LiteralPath $_ -PathType Leaf
        } |
        ForEach-Object {
            Get-Item -LiteralPath $_
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($HostPath)) {
        throw (
            'Peggy speech host not found. Build Peggy.Tray in Release mode ' +
            'or pass the packaged executable with -HostPath.'
        )
    }
}

$HostPath = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($HostPath)
)
if (-not (Test-Path -LiteralPath $HostPath -PathType Leaf)) {
    throw "Peggy speech host not found: $HostPath"
}

$ruleName = 'PeggySpeechHostBlockOutbound'
$rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if ($null -ne $rule) {
    Remove-NetFirewallRule -Name $ruleName
}

$rule = New-NetFirewallRule `
    -Name $ruleName `
    -DisplayName 'Peggy local speech: block outbound network' `
    -Description 'Keeps Peggy microphone audio and local model runtime isolated from outbound network access.' `
    -Direction Outbound `
    -Program $HostPath `
    -Action Block `
    -Profile Any `
    -Enabled True

$verifiedRule = Get-NetFirewallRule -Name $ruleName
$verifiedFilter = $verifiedRule | Get-NetFirewallApplicationFilter
if ($verifiedRule.Enabled.ToString() -ne 'True' -or
    $verifiedRule.Action.ToString() -ne 'Block' -or
    $verifiedFilter.Program -ne $HostPath) {
    throw 'The Peggy speech host firewall rule could not be verified.'
}

Write-Host "Outbound network access is blocked for $HostPath"
