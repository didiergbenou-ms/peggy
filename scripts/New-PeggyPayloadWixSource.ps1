[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')

$payloadRoot = (Resolve-Path -LiteralPath $PayloadDirectory).Path.TrimEnd('\')
$requiredFiles = @(
    'peggy-tray.exe',
    'Peggy.Mcp\peggy-mcp.exe',
    'Peggy.Speech.Host\peggy-speech-host.exe',
    'install.ps1',
    'uninstall.ps1',
    'README.md',
    'THIRD-PARTY-NOTICES.txt',
    'scripts\Install-SpeechModels.ps1',
    'scripts\Collect-PeggyDiagnostics.ps1',
    'scripts\Remove-PeggyStartupEntry.ps1',
    'version.txt'
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (
            Join-Path $payloadRoot $requiredFile
        ) -PathType Leaf)) {
        throw "Installer payload is missing '$requiredFile'."
    }
}

function Get-StableHash {
    param([Parameter(Mandatory)][string]$Value)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString(
            $sha256.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
            )
        ).ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Get-WixId {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$Value
    )

    $hash = Get-StableHash -Value $Value
    return "$Prefix$($hash.Substring(0, 32))"
}

$files = @(
    Get-ChildItem -LiteralPath $payloadRoot -File -Recurse |
        Sort-Object {
            [IO.Path]::GetRelativePath($payloadRoot, $_.FullName)
        }
)
if ($files.Count -eq 0) {
    throw "Installer payload '$payloadRoot' is empty."
}

$directoryPaths = @(
    $files |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath(
                $payloadRoot,
                $_.DirectoryName)
            if ($relative -ne '.') {
                $relative.Replace('/', '\')
            }
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
)
$directoryIds = @{}
foreach ($directoryPath in $directoryPaths) {
    $directoryIds[$directoryPath] = Get-WixId -Prefix 'dir_' -Value $directoryPath
}

function Write-Directories {
    param(
        [Parameter(Mandatory)]
        [Xml.XmlWriter]$Writer,

        [AllowEmptyString()]
        [string]$ParentPath
    )

    $children = @(
        $directoryPaths | Where-Object {
            $candidateParent = Split-Path -Parent $_
            if ($candidateParent -eq '.') {
                $candidateParent = ''
            }
            $candidateParent -eq $ParentPath
        }
    )
    foreach ($child in $children) {
        $Writer.WriteStartElement('Directory')
        $Writer.WriteAttributeString('Id', $directoryIds[$child])
        $Writer.WriteAttributeString('Name', (Split-Path -Leaf $child))
        Write-Directories -Writer $Writer -ParentPath $child
        $Writer.WriteEndElement()
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputParent = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null

$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace

$writer = [Xml.XmlWriter]::Create($resolvedOutput, $settings)
try {
    $namespace = 'http://wixtoolset.org/schemas/v4/wxs'
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', $namespace)

    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('DirectoryRef')
    $writer.WriteAttributeString('Id', 'INSTALLFOLDER')
    Write-Directories -Writer $writer -ParentPath ''
    $writer.WriteEndElement()
    $writer.WriteEndElement()

    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('ComponentGroup')
    $writer.WriteAttributeString('Id', 'PayloadComponents')

    $rootGuid = New-PeggyDeterministicGuid -Value 'Peggy/Component/Directory/root'
    $writer.WriteStartElement('Component')
    $writer.WriteAttributeString('Id', 'cmp_payload_root')
    $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')
    $writer.WriteAttributeString(
        'Guid',
        "{$($rootGuid.ToString().ToUpperInvariant())}")
    $writer.WriteStartElement('RemoveFolder')
    $writer.WriteAttributeString('Id', 'remove_payload_root')
    $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')
    $writer.WriteAttributeString('On', 'uninstall')
    $writer.WriteEndElement()
    $writer.WriteStartElement('RegistryValue')
    $writer.WriteAttributeString('Root', 'HKCU')
    $writer.WriteAttributeString('Key', 'Software\Peggy\Installer\Components')
    $writer.WriteAttributeString('Name', 'directory_root')
    $writer.WriteAttributeString('Type', 'integer')
    $writer.WriteAttributeString('Value', '1')
    $writer.WriteAttributeString('KeyPath', 'yes')
    $writer.WriteEndElement()
    $writer.WriteEndElement()

    foreach ($directoryPath in $directoryPaths) {
        $hash = Get-StableHash -Value "directory/$directoryPath"
        $componentGuid = New-PeggyDeterministicGuid -Value (
            "Peggy/Component/Directory/$($directoryPath.ToLowerInvariant())"
        )
        $writer.WriteStartElement('Component')
        $writer.WriteAttributeString('Id', "cmp_dir_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString('Directory', $directoryIds[$directoryPath])
        $writer.WriteAttributeString(
            'Guid',
            "{$($componentGuid.ToString().ToUpperInvariant())}")
        $writer.WriteStartElement('RemoveFolder')
        $writer.WriteAttributeString('Id', "remove_dir_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString('Directory', $directoryIds[$directoryPath])
        $writer.WriteAttributeString('On', 'uninstall')
        $writer.WriteEndElement()
        $writer.WriteStartElement('RegistryValue')
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\Peggy\Installer\Components')
        $writer.WriteAttributeString('Name', "directory_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }

    foreach ($file in $files) {
        $relativePath = [IO.Path]::GetRelativePath(
            $payloadRoot,
            $file.FullName).Replace('/', '\')
        $relativeDirectory = Split-Path -Parent $relativePath
        $directoryId = if (
            [string]::IsNullOrWhiteSpace($relativeDirectory)
        ) {
            'INSTALLFOLDER'
        } else {
            $directoryIds[$relativeDirectory]
        }
        $hash = Get-StableHash -Value "file/$relativePath"
        $componentGuid = New-PeggyDeterministicGuid -Value (
            "Peggy/Component/File/$($relativePath.ToLowerInvariant())"
        )

        $writer.WriteStartElement('Component')
        $writer.WriteAttributeString('Id', "cmp_file_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString('Directory', $directoryId)
        $writer.WriteAttributeString(
            'Guid',
            "{$($componentGuid.ToString().ToUpperInvariant())}")
        $writer.WriteStartElement('File')
        $writer.WriteAttributeString('Id', "fil_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString(
            'Source',
            "!(bindpath.Payload)\$relativePath")
        $writer.WriteAttributeString('Name', $file.Name)
        $writer.WriteEndElement()
        $writer.WriteStartElement('RegistryValue')
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\Peggy\Installer\Components')
        $writer.WriteAttributeString('Name', "file_$($hash.Substring(0, 32))")
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
} finally {
    $writer.Dispose()
}

Write-Output "Generated WiX payload source for $($files.Count) files."
