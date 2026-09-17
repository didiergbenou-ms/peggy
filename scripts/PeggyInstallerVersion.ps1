function New-PeggyDeterministicGuid {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value))
    } finally {
        $sha256.Dispose()
    }
    $bytes = [byte[]]::new(16)
    [Array]::Copy($hash, $bytes, $bytes.Length)
    $bytes[7] = [byte](($bytes[7] -band 0x0f) -bor 0x50)
    $bytes[8] = [byte](($bytes[8] -band 0x3f) -bor 0x80)
    return [Guid]::new($bytes)
}

function ConvertTo-PeggyInstallerVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $normalized = $Version.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    $match = [Text.RegularExpressions.Regex]::Match(
        $normalized,
        '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.' +
        '(?<patch>0|[1-9]\d*)(?:-(?<prerelease>[0-9A-Za-z.-]+))?$')
    if (-not $match.Success) {
        throw "Version '$Version' is not a supported semantic version."
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $patch = [int]$match.Groups['patch'].Value
    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 654) {
        throw "Version '$Version' exceeds Peggy's MSI limits: major/minor " +
            'must be at most 255 and patch must be at most 654.'
    }

    $prerelease = $match.Groups['prerelease'].Value
    $slot = 99
    if (-not [string]::IsNullOrWhiteSpace($prerelease)) {
        $prereleaseMatch = [Text.RegularExpressions.Regex]::Match(
            $prerelease,
            '^(?<channel>alpha|beta|rc)\.(?<number>[1-9]\d*)$',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $prereleaseMatch.Success) {
            throw "Prerelease '$prerelease' cannot be ordered safely by MSI. " +
                'Use alpha.N (N<=29), beta.N (N<=30), or rc.N (N<=39).'
        }
        $number = [int]$prereleaseMatch.Groups['number'].Value
        $channel = $prereleaseMatch.Groups['channel'].Value.ToLowerInvariant()
        $slot = switch ($channel) {
            'alpha' {
                if ($number -gt 29) {
                    throw 'MSI alpha prerelease numbers must be at most 29.'
                }
                $number
            }
            'beta' {
                if ($number -gt 30) {
                    throw 'MSI beta prerelease numbers must be at most 30.'
                }
                29 + $number
            }
            'rc' {
                if ($number -gt 39) {
                    throw 'MSI rc prerelease numbers must be at most 39.'
                }
                59 + $number
            }
        }
    }

    $msiBuild = ($patch * 100) + $slot
    $productVersion = "$major.$minor.$msiBuild"
    $productCode = New-PeggyDeterministicGuid -Value (
        "Peggy/ProductCode/$normalized"
    )
    return [pscustomobject]@{
        SemanticVersion = $normalized
        Tag = "v$normalized"
        ProductVersion = $productVersion
        FileVersion = "$productVersion.0"
        BundleVersion = "$productVersion.0"
        ProductCode = "{$($productCode.ToString().ToUpperInvariant())}"
        IsPrerelease = -not [string]::IsNullOrWhiteSpace($prerelease)
    }
}
