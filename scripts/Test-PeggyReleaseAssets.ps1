[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PeggyInstallerVersion.ps1')
$script:assertions = 0

function Assert-Asset {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Release asset test failed: $Message" }
    $script:assertions++
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Asset $rejected $Message
}

# Load only pure/download functions; never execute the installer's entry point.
$tokens = $null
$errors = $null
$installerAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'install.ps1'), [ref]$tokens, [ref]$errors)
Assert-Asset ($errors.Count -eq 0) 'Bootstrap installer parses.'
foreach ($name in @(
    'Get-PeggyReleasePackageAssets',
    'Get-PeggyPackageChecksumPath',
    'Save-GitHubReleasePackage',
    'Assert-PackageHash'
)) {
    $definition = $installerAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
    }, $false)
    Assert-Asset ($definition.Count -eq 1) "Bootstrap function $name exists once."
    . ([scriptblock]::Create($definition[0].Extent.Text))
}

function New-TestRelease {
    param([string]$Tag, [string[]]$Names)
    return [pscustomobject]@{
        tag_name = $Tag
        assets = @($Names | ForEach-Object {
            [pscustomobject]@{
                name = $_
                url = "https://api.invalid/assets/$_"
                browser_download_url = "https://download.invalid/$Tag/$_"
            }
        })
    }
}

# These local mocks prevent network/authentication and external process execution.
function Get-GitHubHeaders {
    param([string]$Accept = 'application/vnd.github+json')
    $headers = @{ Accept = $Accept }
    if ($script:useToken) { $headers.Authorization = 'test-only' }
    return $headers
}
function Test-CommandAvailable { param([string[]]$Name) return $script:ghAvailable }
function Write-Step { param([string]$Message) }
function Invoke-RestMethod {
    param($Uri, $Headers, $Method)
    $script:lookupUri = $Uri
    if ($script:failApi) { throw 'Simulated API lookup failure.' }
    return $script:fakeRelease
}
function Invoke-WebRequest {
    param($Uri, $Headers, $OutFile, [switch]$UseBasicParsing)
    $script:webCalls.Add([pscustomobject]@{ Uri = $Uri; Headers = $Headers })
    if ($script:webCalls.Count -eq $script:failWebAt) {
        $script:fakeRelease = New-TestRelease 'v9.9.9' @()
        throw 'Simulated interrupted download after latest changed.'
    }
    Set-Content -LiteralPath $OutFile -Value 'API download fixture' -Encoding ascii
}
function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $script:ghCalls.Add($Arguments)
    $script:LASTEXITCODE = 0
    if ($Arguments[0] -eq 'api') {
        return $script:fakeRelease | ConvertTo-Json -Depth 5
    }
    Assert-Asset ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'download') `
        'Only release download may be executed.'
    $directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        if ($Arguments[$i] -eq '--pattern') {
            $name = $Arguments[$i + 1]
            Assert-Asset ($name -notmatch '[*?]') 'CLI selects exact asset names, not globs.'
            $path = Join-Path $directory $name
            Assert-Asset (-not (Test-Path -LiteralPath $path)) `
                'Partial/stale downloads are removed before CLI retry.'
            if (-not ($script:skipCliChecksum -and $name.EndsWith('.sha256'))) {
                Set-Content -LiteralPath $path -Value 'CLI download fixture' -Encoding ascii
            }
        }
    }
}

$scratch = Join-Path $repoRoot ('artifacts\release-assets-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    foreach ($version in @('v0.1.11', '0.1.11', 'v0.1.12', 'v1.2.3-beta.2')) {
        $info = ConvertTo-PeggyInstallerVersion -Version $version
        $names = $info.AssetNames
        $tag = if ($version.StartsWith('v')) { $version } else { "v$version" }
        $expected = @(
            "peggy-$tag-win-x64.zip",
            "peggy-$tag-win-x64.sha256",
            "peggy-$tag-win-x64.msi",
            "peggy-$tag-win-x64.msi.sha256",
            "peggy-$tag-win-x64-setup.exe",
            "peggy-$tag-win-x64-setup.exe.sha256",
            "peggy-$tag-install.ps1",
            "peggy-$tag-uninstall.ps1"
        )
        Assert-Asset (($names.PSObject.Properties.Value -join '|') -ceq ($expected -join '|')) `
            "All eight build filenames retain normalized semantic version $version."
        $published = @(Get-PeggyPublishedAssetNames -Version $version)
        $expectedPublished = @(
            $names.Zip, $names.ZipChecksum, $names.Bundle,
            $names.BundleChecksum, $names.InstallScript, $names.UninstallScript
        )
        Assert-Asset ($published.Count -eq 6 -and
            ($published -join '|') -ceq ($expectedPublished -join '|')) `
            'Published assets contain exactly the EXE, ZIP, checksums and standalone scripts.'
        Assert-Asset ($published -notcontains $names.Msi -and
            $published -notcontains $names.MsiChecksum) `
            'The standalone MSI and its checksum must never be selected for publication.'
        $output = Join-Path $scratch $version
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Copy-PeggyReleaseScripts -Version $version -OutputDirectory $output
        foreach ($pair in @(
            @{ Source = 'install.ps1'; Target = $names.InstallScript },
            @{ Source = 'uninstall.ps1'; Target = $names.UninstallScript }
        )) {
            Assert-Asset (
                (Get-FileHash -LiteralPath (Join-Path $output $pair.Target)).Hash -eq
                (Get-FileHash -LiteralPath (Join-Path $repoRoot $pair.Source)).Hash
            ) "Staged $($pair.Target) is an unchanged standalone script."
            Assert-Asset (-not (Test-Path -LiteralPath (Join-Path $output $pair.Source))) `
                'Release staging does not create unversioned script aliases.'
        }
        foreach ($name in @($names.Zip, $names.Msi, $names.Bundle)) {
            Set-Content -LiteralPath (Join-Path $output $name) -Value "Fixture $name" -Encoding ascii
        }
        & (Join-Path $PSScriptRoot 'New-PeggyReleaseChecksums.ps1') `
            -Version $version -ArtifactDirectory $output -RequireBootstrapper
        foreach ($pair in @(
            @{ Asset = $names.Zip; Checksum = $names.ZipChecksum },
            @{ Asset = $names.Msi; Checksum = $names.MsiChecksum },
            @{ Asset = $names.Bundle; Checksum = $names.BundleChecksum }
        )) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $output $pair.Asset)).Hash.ToLowerInvariant()
            $text = (Get-Content -LiteralPath (Join-Path $output $pair.Checksum) -Raw).Trim()
            Assert-Asset ($text -ceq "$hash  $($pair.Asset)") 'Checksum records the actual asset hash and basename.'
        }
        Assert-Asset ((Get-ChildItem -LiteralPath $output -File).Count -eq 8) `
            'Eight build artifacts, including the internal MSI/checksum, are staged.'
        foreach ($name in $published) {
            Assert-Asset (Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf) `
                "Published asset '$name' is produced by the build."
        }
        $archive = Join-Path $output $names.Zip
        $checksum = Get-PeggyPackageChecksumPath -Archive $archive
        Assert-Asset ($checksum -eq (Join-Path $output $names.ZipChecksum)) `
            'Local versioned ZIP discovers only its matching checksum.'
        Assert-PackageHash -Archive $archive -Checksum $checksum
        Add-Content -LiteralPath $archive -Value 'Corrupted fixture'
        Assert-Rejected { Assert-PackageHash -Archive $archive -Checksum $checksum } `
            'Changed ZIP must not pass checksum verification.'

        $release = New-TestRelease $tag @($names.Zip, $names.ZipChecksum)
        $selected = Get-PeggyReleasePackageAssets -Release $release
        Assert-Asset ($selected.Archive.name -eq $names.Zip -and
            $selected.Checksum.name -eq $names.ZipChecksum) 'Stable/prerelease versioned pair selected.'
    }

    $stable = (ConvertTo-PeggyInstallerVersion '0.1.11').AssetNames
    $oldNames = @('peggy-win-x64.zip', 'peggy-win-x64.sha256')
    $selected = Get-PeggyReleasePackageAssets -Release (New-TestRelease 'v0.1.10' $oldNames)
    Assert-Asset ($selected.Archive.name -eq $oldNames[0] -and
        $selected.Checksum.name -eq $oldNames[1]) 'Legacy releases remain installable.'
    Assert-Asset ((Get-PeggyPackageChecksumPath (Join-Path $scratch $oldNames[0])) -eq
        (Join-Path $scratch $oldNames[1])) 'Legacy local checksum discovery remains supported.'
    $selected = Get-PeggyReleasePackageAssets -Release (
        New-TestRelease 'v0.1.11' (@($stable.Zip, $stable.ZipChecksum) + $oldNames))
    Assert-Asset ($selected.Archive.name -eq $stable.Zip) 'Complete versioned pair wins over legacy aliases.'
    foreach ($names in @(
        @(),
        @($stable.Zip),
        @($stable.ZipChecksum),
        @($oldNames[0]),
        @($oldNames[1]),
        @($stable.Zip, $oldNames[1]),
        @($oldNames[0], $stable.ZipChecksum),
        @($stable.Zip, $oldNames[0], $oldNames[1]),
        @($stable.ZipChecksum, $oldNames[0], $oldNames[1]),
        @($stable.Zip, 'peggy-v0.1.10-win-x64.sha256'),
        @('peggy-v0.1.10-win-x64.zip', 'peggy-v0.1.10-win-x64.sha256'),
        @($stable.Zip, $stable.Zip, $stable.ZipChecksum)
    )) {
        Assert-Rejected {
            Get-PeggyReleasePackageAssets -Release (New-TestRelease 'v0.1.11' $names)
        } "Incomplete, mixed, wrong-tag, or duplicate assets must fail: $names"
    }

    $Repository = 'example/peggy-fixture'
    foreach ($case in @(
        @{ Name = 'public-latest'; Version = 'latest'; Token = $false; FailApi = $false; FailWeb = 0; Legacy = $false },
        @{ Name = 'token-explicit'; Version = 'v0.1.11'; Token = $true; FailApi = $false; FailWeb = 0; Legacy = $false },
        @{ Name = 'legacy-explicit'; Version = 'v0.1.10'; Token = $false; FailApi = $false; FailWeb = 0; Legacy = $true },
        @{ Name = 'cli-latest'; Version = 'latest'; Token = $false; FailApi = $true; FailWeb = 0; Legacy = $false },
        @{ Name = 'cli-legacy'; Version = 'v0.1.10'; Token = $false; FailApi = $true; FailWeb = 0; Legacy = $true },
        @{ Name = 'cli-retry-pinned'; Version = 'latest'; Token = $false; FailApi = $false; FailWeb = 2; Legacy = $false }
    )) {
        $Version = $case.Version
        $script:useToken = $case.Token
        $script:failApi = $case.FailApi
        $script:failWebAt = $case.FailWeb
        $script:ghAvailable = $true
        $script:skipCliChecksum = $false
        $script:webCalls = [Collections.Generic.List[object]]::new()
        $script:ghCalls = [Collections.Generic.List[object]]::new()
        $tag = if ($case.Legacy) { 'v0.1.10' } else { 'v0.1.11' }
        $pair = if ($case.Legacy) { $oldNames } else { @($stable.Zip, $stable.ZipChecksum) }
        $script:fakeRelease = New-TestRelease $tag $pair
        $destination = Join-Path $scratch $case.Name
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $download = Save-GitHubReleasePackage -Destination $destination
        Assert-Asset ($download.Version -eq $tag -and
            $download.Archive -eq (Join-Path $destination $pair[0]) -and
            $download.Checksum -eq (Join-Path $destination $pair[1])) `
            "Download resolves matching paths and actual tag: $($case.Name)."
        $endpoint = if ($Version -eq 'latest') { 'latest' } else { "tags/$Version" }
        Assert-Asset ($script:lookupUri -eq "https://api.github.com/repos/$Repository/releases/$endpoint") `
            'Explicit version or latest lookup is preserved.'
        Assert-Asset ((Get-ChildItem -LiteralPath $destination -File).Count -eq 2) `
            'Download stages only the selected ZIP/checksum pair.'
        if ($case.FailApi -or $case.FailWeb) {
            $call = $script:ghCalls[$script:ghCalls.Count - 1]
            Assert-Asset ($call[2] -eq $tag -and $call -contains $pair[0] -and
                $call -contains $pair[1]) 'CLI retry is pinned to the resolved tag and both exact names.'
        } else {
            Assert-Asset ($script:ghCalls.Count -eq 0) 'Successful API download needs no GitHub CLI.'
            foreach ($call in $script:webCalls) {
                Assert-Asset (($call.Uri.StartsWith('https://api.invalid/')) -eq $case.Token) `
                    'Token download uses asset API; public download uses browser URL.'
                Assert-Asset ($call.Headers.ContainsKey('Authorization') -eq $case.Token) `
                    'Authentication is sent only through the existing token path.'
            }
        }
    }

    $script:failApi = $true
    $script:ghAvailable = $false
    Assert-Rejected { Save-GitHubReleasePackage -Destination $scratch } `
        'API failure without GitHub CLI fails without selecting another release.'
    $script:ghAvailable = $true
    $script:fakeRelease = New-TestRelease 'v0.1.11' @($stable.Zip, $stable.ZipChecksum)
    $script:skipCliChecksum = $true
    $destination = Join-Path $scratch 'partial-cli'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $destination $stable.ZipChecksum) -Value 'stale checksum'
    Assert-Rejected { Save-GitHubReleasePackage -Destination $destination } `
        'Partial CLI download cannot reuse a stale checksum.'
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}

Write-Output "Peggy release asset tests passed: $script:assertions assertions."
