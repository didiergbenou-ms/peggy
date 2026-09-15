[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstalledExecutable,

    [switch]$Restore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $actualExecutable = [IO.Path]::GetFullPath($InstalledExecutable)
    $expectedExecutable = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA 'Programs\Peggy\peggy-tray.exe')
    )
    if ($actualExecutable -ine $expectedExecutable) {
        throw "Refusing startup cleanup for unexpected path '$actualExecutable'."
    }

    $runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $runKey = if ($Restore) {
        [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey(
            $runKeyPath,
            $true)
    } else {
        [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
            $runKeyPath,
            $true)
    }
    if ($null -eq $runKey) {
        exit 0
    }

    try {
        $startupCommand = [string]$runKey.GetValue(
            'Peggy',
            $null,
            [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($Restore) {
            if ([string]::IsNullOrWhiteSpace($startupCommand)) {
                $expectedCommand = "`"$actualExecutable`""
                $runKey.SetValue(
                    'Peggy',
                    $expectedCommand,
                    [Microsoft.Win32.RegistryValueKind]::String)
                if ([string]$runKey.GetValue('Peggy', $null) -cne
                    $expectedCommand) {
                    throw 'The Peggy startup entry could not be restored.'
                }
            } else {
                Write-Output (
                    'Preserved a startup entry created during MSI rollback.'
                )
            }
            exit 0
        }
        if ([string]::IsNullOrWhiteSpace($startupCommand)) {
            exit 0
        }

        $match = [Text.RegularExpressions.Regex]::Match(
            $startupCommand,
            '^\s*(?:"([^"]+)"|([^\s]+))')
        if (-not $match.Success) {
            Write-Output 'Preserved an unrecognized Peggy startup entry.'
            exit 0
        }

        $startupExecutable = if (
            -not [string]::IsNullOrWhiteSpace($match.Groups[1].Value)
        ) {
            $match.Groups[1].Value
        } else {
            $match.Groups[2].Value
        }
        if (
            [IO.Path]::GetFullPath($startupExecutable) -ine $actualExecutable
        ) {
            Write-Output 'Preserved a Peggy startup entry that targets another install.'
            exit 0
        }

        $runKey.DeleteValue('Peggy', $false)
        if ($null -ne $runKey.GetValue('Peggy', $null)) {
            throw 'The Peggy startup entry could not be removed.'
        }
    } finally {
        $runKey.Dispose()
    }
} catch {
    Write-Error "Peggy startup cleanup failed: $_"
    exit 1
}
