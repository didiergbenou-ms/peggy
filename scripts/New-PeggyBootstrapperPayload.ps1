[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$root = (Resolve-Path -LiteralPath $PayloadDirectory).Path
foreach ($required in @(
        'peggy-setup.exe', 'peggy-setup.dll', 'peggy-setup.deps.json',
        'peggy-setup.runtimeconfig.json', 'peggy-tray.dll', 'Peggy.Core.dll',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll',
        'WixToolset.BootstrapperApplicationApi.dll', 'mbanative.dll',
        'scripts\Install-SpeechModels.ps1'
    )) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) {
        throw "Self-contained Peggy setup payload is missing '$required'."
    }
}
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse |
    Sort-Object { [IO.Path]::GetRelativePath($root, $_.FullName) })
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Generated WiX source must be outside the bootstrapper payload directory.'
}
[void](New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force)
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$writer = [Xml.XmlWriter]::Create($output, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs')
    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('PayloadGroup')
    $writer.WriteAttributeString('Id', 'PeggyBootstrapperPayloads')
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
        # The BA entry point is authored by BootstrapperApplication/@SourceFile.
        if ($relative -ieq 'peggy-setup.exe') { continue }
        if ($relative -match '(^|\\)(state\.dat|models)(\\|$)') {
            throw "User state or model data cannot be bundled: '$relative'."
        }
        $id = New-PeggyDeterministicGuid -Value "Peggy/SetupPayload/$($relative.ToLowerInvariant())"
        $writer.WriteStartElement('Payload')
        $writer.WriteAttributeString('Id', "setup_$($id.ToString('N'))")
        $writer.WriteAttributeString('Name', $relative)
        $writer.WriteAttributeString('SourceFile', $file.FullName)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
} finally {
    $writer.Dispose()
}
Write-Output "Generated self-contained Peggy setup payload for $($files.Count) files."
