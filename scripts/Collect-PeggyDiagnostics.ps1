[CmdletBinding()]
param(
    [string]$OutputDirectory = (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::DesktopDirectory
        )
    ),
    [switch]$RunConnectivityTests,
    [switch]$RunWorkIqTests,
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string]$WorkIqAccount,
    [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')]
    [string]$EmailTo = 'didiergbenou@microsoft.com',
    [switch]$NoEmail,
    [switch]$SendWithoutPrompt,
    [ValidateRange(100, 10000)]
    [int]$LogTailLines = 2000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Protect-Text {
    param([AllowEmptyString()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }
    $protected = $Text
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $protected = $protected -replace (
            [Text.RegularExpressions.Regex]::Escape($env:USERPROFILE)
        ), '%USERPROFILE%'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
        $protected = $protected -replace (
            [Text.RegularExpressions.Regex]::Escape($env:USERNAME)
        ), '%USERNAME%'
    }
    $protected = $protected -replace (
        '(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b'
    ), '[email]'
    $protected = $protected -replace (
        '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-' +
        '[0-9a-f]{4}-[0-9a-f]{12}\b'
    ), '[guid]'
    $protected = $protected -replace 'https?://\S+', '[url]'
    $protected = $protected -replace (
        '\b[A-Za-z0-9_-]{64,}={0,2}\b'
    ), '[opaque-id]'
    return $protected
}

function Remove-CredentialText {
    param([AllowEmptyString()][AllowNull()][string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return $Text
    }
    $filtered = $Text -replace (
        '(?im)(authorization\s*:\s*)' +
        '[A-Za-z][A-Za-z0-9_-]*\s+[^\r\n]+'
    ), '${1}[redacted]'
    $filtered = $filtered -replace (
        '(?i)(authorization\s*=\s*)' +
        '[A-Za-z][A-Za-z0-9_-]*\s+[^\r\n,;]+'
    ), '${1}[redacted]'
    $filtered = $filtered -replace (
        '(?i)("?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|' +
        'token|client[_-]?secret|secret|credential|password|' +
        'api[_-]?key|authorization|cookie)"?\s*[:=]\s*)' +
        '("[^"]*"|''[^'']*''|[^\s,;]+)'
    ), '${1}[redacted]'
    $filtered = $filtered -replace (
        '(?i)(--(?:access[_-]?token|refresh[_-]?token|id[_-]?token|' +
        'token|client[_-]?secret|secret|credential|password|' +
        'api[_-]?key|authorization|cookie)\s+)' +
        '("[^"]*"|''[^'']*''|\S+)'
    ), '${1}[redacted]'
    $filtered = $filtered -replace (
        '(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]+\.' +
        '[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+'
    ), '[jwt-redacted]'
    $filtered = $filtered -replace (
        '(?i)(?<![A-Za-z0-9_])(?:github_pat_[A-Za-z0-9_]{20,}|' +
        'gh[pousr]_[A-Za-z0-9_]{20,})(?![A-Za-z0-9_])'
    ), '[github-token-redacted]'
    return $filtered
}

function Write-TextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowEmptyString()][string]$Content
    )

    [IO.File]::WriteAllText(
        $Path,
        $Content,
        (New-Object Text.UTF8Encoding($false))
    )
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    Write-TextFile `
        -Path $Path `
        -Content (ConvertTo-Json -InputObject $Value -Depth 8)
}

function Read-PeggySetupLogTail {
    param(
        [Parameter(Mandatory)][string]$Path,
        [ValidateRange(1, 10000)][int]$LogTailLines = 2000,
        [ValidateRange(1024, 16777216)][int]$MaxReadBytes = 4MB,
        [ValidateRange(128, 65536)][int]$MaxLineCharacters = 65536
    )

    $stream = [IO.File]::Open(
        $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    )
    try {
        $sourceBytes = $stream.Length
        $header = New-Object byte[] 4
        $headerLength = $stream.Read($header, 0, $header.Length)
        $encoding = [Text.Encoding]::UTF8
        $preambleBytes = 0
        $characterBytes = 1
        if ($headerLength -ge 2 -and
            $header[0] -eq 255 -and $header[1] -eq 254) {
            $encoding = [Text.Encoding]::Unicode
            $preambleBytes = 2
            $characterBytes = 2
        }
        elseif ($headerLength -ge 2 -and
            $header[0] -eq 254 -and $header[1] -eq 255) {
            $encoding = [Text.Encoding]::BigEndianUnicode
            $preambleBytes = 2
            $characterBytes = 2
        }
        elseif ($headerLength -ge 3 -and
            $header[0] -eq 239 -and $header[1] -eq 187 -and
            $header[2] -eq 191) {
            $preambleBytes = 3
        }
        $offset = [Math]::Max($preambleBytes, $sourceBytes - $MaxReadBytes)
        $offset += ($offset - $preambleBytes) % $characterBytes
        [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ([int]($sourceBytes - $offset))
        $readBytes = 0
        while ($readBytes -lt $buffer.Length) {
            $count = $stream.Read($buffer, $readBytes, $buffer.Length - $readBytes)
            if ($count -eq 0) { break }
            $readBytes += $count
        }
        $text = $encoding.GetString($buffer, 0, $readBytes)
    }
    finally {
        $stream.Dispose()
    }

    $byteTailTruncated = $offset -gt $preambleBytes -or
        $readBytes -lt $buffer.Length
    if ($offset -gt $preambleBytes) {
        # A byte-limited tail may start inside a credential: discard that entire line.
        $newline = [regex]::Match($text, '\r\n|\n|\r')
        $text = if ($newline.Success) {
            $text.Substring($newline.Index + $newline.Length)
        } else { '' }
    }
    $lines = if ($text.Length -eq 0) { @() } else {
        $split = [regex]::Split($text, '\r\n|\n|\r')
        $length = $split.Length
        if ($split[$length - 1] -eq '') { $length-- }
        if ($length -gt 0) { $split[0..($length - 1)] }
    }
    $lines = @($lines)
    $lineCountTruncated = $lines.Count -gt $LogTailLines
    $firstLine = [Math]::Max(0, $lines.Count - $LogTailLines)
    $oversizedLineCount = 0
    $safeLines = @(
        for ($index = $firstLine; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            if ($line.Length -le $MaxLineCharacters) {
                $line = Protect-Text (Remove-CredentialText $line)
            }
            if ($line.Length -gt $MaxLineCharacters) {
                # Do not cut a quoted secret into a fragment that redaction cannot recognize.
                $oversizedLineCount++
                '[oversized setup log line omitted]'
            }
            else {
                $line
            }
        }
    )
    return [pscustomobject]@{
        Content = $safeLines -join [Environment]::NewLine
        SourceBytes = $sourceBytes
        ReadBytes = $readBytes
        LinesWritten = $safeLines.Count
        ByteTailTruncated = $byteTailTruncated
        LineCountTruncated = $lineCountTruncated
        OversizedLineCount = $oversizedLineCount
        Truncated = $byteTailTruncated -or $lineCountTruncated -or
            $oversizedLineCount -gt 0
    }
}

function Export-PeggySetupLogs {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [ValidateRange(1, 10000)][int]$LogTailLines = 2000,
        [datetime]$NowUtc = [datetime]::UtcNow
    )

    $pattern = '\APeggy_[0-9]{14}\.log\z'
    $cutoff = $NowUtc.ToUniversalTime().AddDays(-14)
    $manifest = [ordered]@{
        Source = '%TEMP%'
        FilePattern = $pattern
        CollectedAtUtc = $NowUtc.ToUniversalTime().ToString('O')
        CutoffUtc = $cutoff.ToString('O')
        MaximumLogCount = 10
        MaximumReadBytes = 4MB
        MaximumLineCharacters = 65536
        LogTailLines = $LogTailLines
        EligibleLogCount = 0
        OmittedLogCount = 0
        CollectedLogCount = 0
        Status = 'NoRecentLogs'
        ErrorType = $null
        ErrorMessage = $null
        Files = @()
    }
    try {
        $eligible = @(Get-ChildItem -LiteralPath $SourceDirectory `
            -Filter 'Peggy_*.log' -File -ErrorAction Stop | Where-Object {
                $_.Name -cmatch $pattern -and
                -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                $_.LastWriteTimeUtc -ge $cutoff -and
                $_.LastWriteTimeUtc -le $NowUtc.ToUniversalTime()
            } | Sort-Object -Property @(
                @{ Expression = 'LastWriteTimeUtc'; Descending = $true },
                @{ Expression = 'Name'; Descending = $true }
            ))
        $manifest.EligibleLogCount = $eligible.Count
        $manifest.OmittedLogCount = [Math]::Max(0, $eligible.Count - 10)
        foreach ($file in @($eligible | Select-Object -First 10)) {
            $entry = [ordered]@{
                SourceRelativePath = $file.Name
                OutputRelativePath = $null
                ModifiedUtc = $file.LastWriteTimeUtc.ToString('O')
                SourceBytes = $file.Length
                ReadBytes = $null
                LinesWritten = 0
                ByteTailTruncated = $null
                LineCountTruncated = $null
                OversizedLineCount = 0
                Truncated = $null
                Status = 'Error'
                ErrorType = $null
                ErrorMessage = $null
            }
            try {
                $tail = Read-PeggySetupLogTail -Path $file.FullName `
                    -LogTailLines $LogTailLines `
                    -MaxReadBytes $manifest.MaximumReadBytes `
                    -MaxLineCharacters $manifest.MaximumLineCharacters
                $relativePath = Join-Path 'setup-logs' ($file.Name + '.txt')
                [void][IO.Directory]::CreateDirectory(
                    (Join-Path $DestinationDirectory 'setup-logs'))
                Write-TextFile -Path (Join-Path $DestinationDirectory $relativePath) `
                    -Content $tail.Content
                $entry.OutputRelativePath = $relativePath
                foreach ($property in @(
                    'SourceBytes', 'ReadBytes', 'LinesWritten', 'ByteTailTruncated',
                    'LineCountTruncated', 'OversizedLineCount', 'Truncated'
                )) {
                    $entry[$property] = $tail.$property
                }
                $entry.Status = 'Collected'
                $manifest.CollectedLogCount++
            }
            catch [IO.IOException], [UnauthorizedAccessException],
                [Security.SecurityException] {
                $entry.ErrorType = $_.Exception.GetBaseException().GetType().FullName
                $entry.ErrorMessage = Protect-Text (
                    Remove-CredentialText $_.Exception.Message)
            }
            $manifest.Files += [pscustomobject]$entry
        }
        if ($manifest.Files.Count -gt 0) {
            $manifest.Status = if (
                $manifest.CollectedLogCount -eq $manifest.Files.Count
            ) { 'Collected' } else { 'Partial' }
        }
    }
    catch [IO.IOException], [UnauthorizedAccessException],
        [Security.SecurityException], [Management.Automation.ItemNotFoundException] {
        $manifest.Status = 'Error'
        $manifest.ErrorType = $_.Exception.GetBaseException().GetType().FullName
        $manifest.ErrorMessage = Protect-Text (
            Remove-CredentialText $_.Exception.Message)
    }
    Write-JsonFile -Path (Join-Path $DestinationDirectory 'setup-logs-manifest.json') `
        -Value $manifest
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($stream)
        ).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-ObjectPropertyValue {
    param(
        [AllowNull()]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-CollectionCount {
    param([AllowNull()]$Value)

    if ($null -eq $Value) {
        return 0
    }
    return @($Value).Count
}

function Get-PeggyOperationalState {
    param(
        [AllowNull()][string]$Path,
        [AllowNull()][string]$DisplayPath,
        [AllowNull()][string]$ResolutionError
    )

    $result = [ordered]@{
        Loaded = $false
        Path = $DisplayPath
        ErrorType = $null
        ErrorMessage = $ResolutionError
    }
    if (-not [string]::IsNullOrWhiteSpace($ResolutionError)) {
        return [pscustomobject]$result
    }
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.ErrorMessage = 'Peggy state file was not found.'
        return [pscustomobject]$result
    }

    [byte[]]$protectedBytes = $null
    [byte[]]$plaintext = $null
    $stateJson = $null
    try {
        try {
            Add-Type `
                -AssemblyName System.Security.Cryptography.ProtectedData `
                -ErrorAction Stop
        }
        catch {
            Add-Type -AssemblyName System.Security -ErrorAction Stop
        }
        $protectedBytes = [IO.File]::ReadAllBytes($Path)
        $entropy = [Text.Encoding]::UTF8.GetBytes('Peggy.State.v1')
        $plaintext = [Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        $stateJson = [Text.Encoding]::UTF8.GetString($plaintext)
        $state = ConvertFrom-Json -InputObject $stateJson
        $settings = Get-ObjectPropertyValue $state 'settings'
        $cursor = Get-ObjectPropertyValue $state 'monitoringCursor'
        $lastFailure = Get-ObjectPropertyValue `
            $state `
            'lastMonitoringFailure'

        $result.Loaded = $true
        $result.ErrorMessage = $null
        $result.Version = Get-ObjectPropertyValue $state 'version'
        $result.PinnedAccount = Get-ObjectPropertyValue `
            $state `
            'pinnedAccount'
        $result.PinnedMailbox = Get-ObjectPropertyValue `
            $state `
            'pinnedMailbox'
        $result.PinnedUserPrincipalName = Get-ObjectPropertyValue `
            $state `
            'pinnedUserPrincipalName'
        $result.PinnedPrincipalId = Get-ObjectPropertyValue `
            $state `
            'pinnedPrincipalId'
        $result.WorkIqEulaAcceptedVersion = Get-ObjectPropertyValue `
            $state `
            'workIqEulaAcceptedVersion'
        $result.LastSuccessfulMonitoringUtc = Get-ObjectPropertyValue `
            $state `
            'lastSuccessfulMonitoringUtc'
        $result.ConsecutiveMonitoringFailures = Get-ObjectPropertyValue `
            $state `
            'consecutiveMonitoringFailures'
        $result.MonitoringInterrupted = Get-ObjectPropertyValue `
            $state `
            'monitoringInterrupted'
        if ($null -eq $lastFailure) {
            $result.LastMonitoringFailure = $null
        }
        else {
            $result.LastMonitoringFailure = [ordered]@{
                OccurredUtc = Get-ObjectPropertyValue `
                    $lastFailure `
                    'occurredUtc'
                Stage = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'stage'
                )
                Code = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'code'
                )
                ExceptionType = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'exceptionType'
                )
                Message = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'message'
                )
                StatusCode = Get-ObjectPropertyValue `
                    $lastFailure `
                    'statusCode'
                RetryAfterSeconds = Get-ObjectPropertyValue `
                    $lastFailure `
                    'retryAfterSeconds'
                Operation = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'operation'
                )
                RequestId = Remove-CredentialText (
                    Get-ObjectPropertyValue $lastFailure 'requestId'
                )
                RetrievalReferenceUtc = Get-ObjectPropertyValue `
                    $lastFailure `
                    'retrievalReferenceUtc'
                BatchLimit = Get-ObjectPropertyValue `
                    $lastFailure `
                    'batchLimit'
                ExceptionChain = @(
                    Get-ObjectPropertyValue `
                        $lastFailure `
                        'exceptionChain' |
                        ForEach-Object {
                            Remove-CredentialText ([string]$_)
                        }
                )
            }
        }
        $result.Settings = [ordered]@{
            CopilotModel = Get-ObjectPropertyValue $settings 'copilotModel'
            CopilotReasoningEffort = Get-ObjectPropertyValue `
                $settings `
                'copilotReasoningEffort'
            CopilotContextTier = Get-ObjectPropertyValue `
                $settings `
                'copilotContextTier'
            OutlookScope = Get-ObjectPropertyValue $settings 'outlookScope'
            IncludeActiveCasesFolder = Get-ObjectPropertyValue `
                $settings `
                'includeActiveCasesFolder'
            AdditionalOutlookFolders = @(
                Get-ObjectPropertyValue $settings 'additionalOutlookFolders'
            )
            TeamsScope = Get-ObjectPropertyValue $settings 'teamsScope'
            MonitorTeamsChatsInSelectedScope = Get-ObjectPropertyValue `
                $settings `
                'monitorTeamsChatsInSelectedScope'
            TeamsTeamWatchlistCount = Get-CollectionCount (
                Get-ObjectPropertyValue $settings 'teamsTeamWatchlist'
            )
            TeamsChannelWatchlistCount = Get-CollectionCount (
                Get-ObjectPropertyValue $settings 'teamsChannelWatchlist'
            )
            StartAtSignIn = Get-ObjectPropertyValue `
                $settings `
                'startAtSignIn'
            MonitoringPaused = Get-ObjectPropertyValue `
                $settings `
                'monitoringPaused'
            MonitoringIntervalMinutes = Get-ObjectPropertyValue `
                $settings `
                'monitoringIntervalMinutes'
            MonitoringScheduleEnabled = Get-ObjectPropertyValue `
                $settings `
                'monitoringScheduleEnabled'
            MonitoringWakeMinute = Get-ObjectPropertyValue `
                $settings `
                'monitoringWakeMinute'
            MonitoringSleepMinute = Get-ObjectPropertyValue `
                $settings `
                'monitoringSleepMinute'
            SpeechEnabled = Get-ObjectPropertyValue `
                $settings `
                'speechEnabled'
        }
        $result.MonitoringCursor = [ordered]@{
            Initialized = Get-ObjectPropertyValue $cursor 'initialized'
            InitialOutlookScanComplete = Get-ObjectPropertyValue `
                $cursor `
                'initialOutlookScanComplete'
            InitialScanStartedUtc = Get-ObjectPropertyValue `
                $cursor `
                'initialScanStartedUtc'
            OutlookScanStartedUtc = Get-ObjectPropertyValue `
                $cursor `
                'outlookScanStartedUtc'
            OutlookWatermarkUtc = Get-ObjectPropertyValue `
                $cursor `
                'outlookWatermarkUtc'
            OutlookBoundaryMessageCount = Get-CollectionCount (
                Get-ObjectPropertyValue $cursor 'outlookBoundaryMessageIds'
            )
            TeamsScanStartedUtc = Get-ObjectPropertyValue `
                $cursor `
                'teamsScanStartedUtc'
            TeamsWatermarkUtc = Get-ObjectPropertyValue `
                $cursor `
                'teamsWatermarkUtc'
            TeamsSearchWindowEndUtc = Get-ObjectPropertyValue `
                $cursor `
                'teamsSearchWindowEndUtc'
            TeamsSearchOffset = Get-ObjectPropertyValue `
                $cursor `
                'teamsSearchOffset'
            DirectChatWatermarkUtc = Get-ObjectPropertyValue `
                $cursor `
                'directChatWatermarkUtc'
            ChannelScanResumeKey = Get-ObjectPropertyValue `
                $cursor `
                'channelScanResumeKey'
            ChannelContinuationPass = Get-ObjectPropertyValue `
                $cursor `
                'channelContinuationPass'
        }
        $result.Counts = [ordered]@{
            Alerts = Get-CollectionCount (
                Get-ObjectPropertyValue $state 'alerts'
            )
            FeedbackRules = Get-CollectionCount (
                Get-ObjectPropertyValue $state 'feedbackRules'
            )
            SeenMessages = Get-CollectionCount (
                Get-ObjectPropertyValue $state 'seenMessages'
            )
            CreditHistory = Get-CollectionCount (
                Get-ObjectPropertyValue $state 'creditHistory'
            )
            AuditLog = Get-CollectionCount (
                Get-ObjectPropertyValue $state 'auditLog'
            )
        }
        return [pscustomobject]$result
    }
    catch {
        $result.ErrorType = $_.Exception.GetType().FullName
        $result.ErrorMessage =
            'Peggy operational state could not be decrypted or parsed.'
        return [pscustomobject]$result
    }
    finally {
        if ($null -ne $plaintext) {
            [Array]::Clear($plaintext, 0, $plaintext.Length)
        }
        if ($null -ne $protectedBytes) {
            [Array]::Clear(
                $protectedBytes,
                0,
                $protectedBytes.Length
            )
        }
        $stateJson = $null
    }
}

function Resolve-CommandPath {
    param([Parameter(Mandatory)][string[]]$Names)

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
                return $command.Source
            }
            if (-not [string]::IsNullOrWhiteSpace($command.Path)) {
                return $command.Path
            }
        }
    }
    return $null
}

function Resolve-WorkIqExecutablePath {
    param([Parameter(Mandatory)][string]$Path)

    if (-not ($Path.EndsWith(
                '.cmd',
                [StringComparison]::OrdinalIgnoreCase) -or
            $Path.EndsWith(
                '.bat',
                [StringComparison]::OrdinalIgnoreCase))) {
        return $Path
    }

    try {
        $wrapperPath = [IO.Path]::GetFullPath($Path)
        $wrapperDirectory = [IO.Path]::GetDirectoryName($wrapperPath)
        if (-not [string]::IsNullOrWhiteSpace($wrapperDirectory)) {
            $npmExecutable = Join-Path `
                $wrapperDirectory `
                'node_modules\@microsoft\workiq\bin\win-x64\workiq.exe'
            if (Test-Path -LiteralPath $npmExecutable -PathType Leaf) {
                return $npmExecutable
            }
        }

        $contents = [IO.File]::ReadAllText($wrapperPath)
        $matches = [Text.RegularExpressions.Regex]::Matches(
            $contents,
            '"(?<path>[^"]*workiq\.exe)"',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase
        )
        for ($index = $matches.Count - 1; $index -ge 0; $index--) {
            $candidateText = $matches[$index].Groups['path'].Value
            $candidateText = [Text.RegularExpressions.Regex]::Replace(
                $candidateText,
                '%~dp0',
                [Text.RegularExpressions.MatchEvaluator]{
                    param($match)
                    return $wrapperDirectory.TrimEnd('\') + '\'
                },
                [Text.RegularExpressions.RegexOptions]::IgnoreCase
            )
            $candidate = [Environment]::ExpandEnvironmentVariables(
                $candidateText
            )
            if (-not [IO.Path]::IsPathRooted($candidate)) {
                $candidate = Join-Path $wrapperDirectory $candidate
            }
            $candidate = [IO.Path]::GetFullPath($candidate)
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }
    catch {
        return $null
    }
    return $null
}

function Resolve-DiagnosticDataPath {
    param(
        [Parameter(Mandatory)][string]$DefaultPath,
        [Parameter(Mandatory)][string]$OverrideVariable,
        [Parameter(Mandatory)]
        [ValidateSet('Leaf', 'Container')]
        [string]$ExpectedType
    )

    $configuredPath = [Environment]::GetEnvironmentVariable(
        $OverrideVariable
    )
    $path = $DefaultPath
    $displayPath = Protect-Text $DefaultPath
    if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
        $displayPath = Protect-Text $configuredPath
        try {
            $path = [IO.Path]::GetFullPath(
                [Environment]::ExpandEnvironmentVariables(
                    $configuredPath.Trim().Trim('"')
                )
            )
            $displayPath = Protect-Text $path
        }
        catch {
            return [pscustomobject]@{
                Path = $null
                DisplayPath = $displayPath
                Error = "$OverrideVariable contains an invalid path."
            }
        }
    }

    try {
        if ((Test-Path -LiteralPath $path) -and
            -not (Test-Path -LiteralPath $path -PathType $ExpectedType)) {
            $expectedDescription = if ($ExpectedType -eq 'Leaf') {
                'file'
            } else {
                'directory'
            }
            return [pscustomobject]@{
                Path = $path
                DisplayPath = $displayPath
                Error = (
                    "$OverrideVariable does not identify a " +
                    "$expectedDescription."
                )
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Path = $null
            DisplayPath = $displayPath
            Error = "$OverrideVariable contains an invalid path."
        }
    }

    return [pscustomobject]@{
        Path = $path
        DisplayPath = $displayPath
        Error = $null
    }
}

function Invoke-DiagnosticCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Candidates,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$OverrideVariable,
        [string[]]$FallbackPaths = @(),
        [switch]$PreserveTechnicalOutput,
        [switch]$RetainRawOutputForAnalysis,
        [switch]$ResolveWorkIqWrapper
    )

    $configuredPath = if ([string]::IsNullOrWhiteSpace(
            $OverrideVariable
        )) {
        $null
    } else {
        [Environment]::GetEnvironmentVariable($OverrideVariable)
    }
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        $path = Resolve-CommandPath -Names $Candidates
        if ([string]::IsNullOrWhiteSpace($path)) {
            foreach ($fallbackPath in $FallbackPaths) {
                try {
                    $candidate = [IO.Path]::GetFullPath(
                        [Environment]::ExpandEnvironmentVariables(
                            $fallbackPath
                        )
                    )
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                        $path = $candidate
                        break
                    }
                }
                catch {
                    continue
                }
            }
        }
    } else {
        try {
            $path = [IO.Path]::GetFullPath(
                [Environment]::ExpandEnvironmentVariables(
                    $configuredPath.Trim().Trim('"')
                )
            )
        }
        catch {
            return [pscustomobject]@{
                Name = $Name
                Found = $false
                Path = Protect-Text $configuredPath
                ExitCode = $null
                Stdout = ''
                Stderr = ''
                Output = "$OverrideVariable contains an invalid path."
            }
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return [pscustomobject]@{
                Name = $Name
                Found = $false
                Path = Protect-Text $path
                ExitCode = $null
                Stdout = ''
                Stderr = ''
                Output = "$OverrideVariable does not identify a file."
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        return [pscustomobject]@{
            Name = $Name
            Found = $false
            Path = $null
            ExitCode = $null
            Stdout = ''
            Stderr = ''
            Output = 'Command not found.'
        }
    }
    if ($ResolveWorkIqWrapper) {
        $wrapperPath = $path
        $path = Resolve-WorkIqExecutablePath -Path $wrapperPath
        if ([string]::IsNullOrWhiteSpace($path)) {
            return [pscustomobject]@{
                Name = $Name
                Found = $false
                Path = Protect-Text $wrapperPath
                ExitCode = $null
                Stdout = ''
                Stderr = ''
                Output = (
                    'The WorkIQ wrapper target could not be resolved safely.'
                )
            }
        }
    }

    $commandId = [Guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path (
        [IO.Path]::GetTempPath()
    ) "peggy-diagnostic-$PID-$commandId.stdout"
    $stderrPath = Join-Path (
        [IO.Path]::GetTempPath()
    ) "peggy-diagnostic-$PID-$commandId.stderr"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $path @Arguments 1> $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $previousErrorActionPreference
        $stdout = if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
            [IO.File]::ReadAllText($stdoutPath).Trim()
        } else {
            ''
        }
        $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            [IO.File]::ReadAllText($stderrPath).Trim()
        } else {
            ''
        }
        $rawStdout = $stdout
        $rawStderr = $stderr
        $output = @($stdout, $stderr) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $output = $output -join [Environment]::NewLine
        if ($PreserveTechnicalOutput) {
            $stdout = Remove-CredentialText $stdout
            $stderr = Remove-CredentialText $stderr
            $output = Remove-CredentialText $output
        } else {
            $stdout = Protect-Text $stdout
            $stderr = Protect-Text $stderr
            $output = Protect-Text $output
        }
        $commandResult = [ordered]@{
            Name = $Name
            Found = $true
            Path = Protect-Text $path
            ExitCode = $exitCode
            Stdout = $stdout
            Stderr = $stderr
            Output = $output
        }
        if ($RetainRawOutputForAnalysis) {
            $commandResult.InternalRawStdout = $rawStdout
            $commandResult.InternalRawStderr = $rawStderr
        }
        return [pscustomobject]$commandResult
    }
    catch {
        $ErrorActionPreference = $previousErrorActionPreference
        $errorText = if ($PreserveTechnicalOutput) {
            Remove-CredentialText $_.Exception.ToString()
        } else {
            Protect-Text $_.Exception.Message
        }
        return [pscustomobject]@{
            Name = $Name
            Found = $true
            Path = Protect-Text $path
            ExitCode = -1
            Stdout = ''
            Stderr = $errorText
            Output = $errorText
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Remove-Item `
            -LiteralPath $stdoutPath, $stderrPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Get-WorkIqJsonPayload {
    param([Parameter(Mandatory)][string]$Text)

    $candidates = New-Object Collections.Generic.List[int]
    $candidates.Add(0)
    $matches = [Text.RegularExpressions.Regex]::Matches(
        $Text,
        '(?m)^[\t ]*[\{\[]'
    )
    foreach ($match in $matches) {
        if (-not $candidates.Contains($match.Index)) {
            $candidates.Add($match.Index)
        }
    }
    $nativeErrorMatches = [Text.RegularExpressions.Regex]::Matches(
        $Text,
        '(?im)workiq(?:\.exe)?\s*:\s*(?<json>[\{\[])'
    )
    foreach ($match in $nativeErrorMatches) {
        $start = $match.Groups['json'].Index
        if (-not $candidates.Contains($start)) {
            $candidates.Add($start)
        }
    }
    for ($index = $candidates.Count - 1; $index -ge 0; $index--) {
        $start = $candidates[$index]
        $depth = 0
        $inString = $false
        $escaped = $false
        $end = -1
        for ($position = $start; $position -lt $Text.Length; $position++) {
            $character = $Text[$position]
            if ($inString) {
                if ($escaped) {
                    $escaped = $false
                } elseif ($character -eq '\') {
                    $escaped = $true
                } elseif ($character -eq '"') {
                    $inString = $false
                }
                continue
            }
            if ($character -eq '"') {
                $inString = $true
            } elseif ($character -eq '{' -or $character -eq '[') {
                $depth++
            } elseif ($character -eq '}' -or $character -eq ']') {
                $depth--
                if ($depth -eq 0) {
                    $end = $position
                    break
                }
            }
        }
        $candidate = if ($end -ge $start) {
            $Text.Substring($start, $end - $start + 1)
        } else {
            $Text.Substring($start)
        }
        $candidate = (
            $candidate -replace '\r?\n[\t ]*', ''
        ).Trim()
        try {
            return [pscustomobject]@{
                Found = $true
                Value = ConvertFrom-Json -InputObject $candidate
            }
        }
        catch {
            continue
        }
    }
    return [pscustomobject]@{
        Found = $false
        Value = $null
    }
}

function Get-WorkIqCommandAnalysis {
    param(
        [Parameter(Mandatory)]$Result,
        [AllowEmptyString()][AllowNull()][string]$RawStdout
    )

    $analysis = [ordered]@{
        PayloadParsed = $false
        ParseError = $null
        StatusCodes = @()
        Errors = @()
        RequestIds = @()
        Data = $null
        Succeeded = $false
    }
    if (-not $Result.Found -or $Result.ExitCode -ne 0) {
        return [pscustomobject]$analysis
    }
    if ([string]::IsNullOrWhiteSpace($RawStdout)) {
        $analysis.ParseError = 'WorkIQ returned empty stdout.'
        return [pscustomobject]$analysis
    }

    $jsonPayload = Get-WorkIqJsonPayload -Text $RawStdout
    if (-not $jsonPayload.Found) {
        $analysis.ParseError = 'WorkIQ stdout did not contain valid JSON.'
        return [pscustomobject]$analysis
    }
    try {
        $payload = $jsonPayload.Value
        $analysis.PayloadParsed = $true
        $requestId = Get-ObjectPropertyValue $payload 'requestId'
        if (-not [string]::IsNullOrWhiteSpace([string]$requestId)) {
            $analysis.RequestIds += Remove-CredentialText ([string]$requestId)
        }
        $resultValue = Get-ObjectPropertyValue $payload 'results'
        $results = @()
        if ($null -ne $resultValue) {
            $results = @($resultValue)
        }
        if ((Get-CollectionCount $results) -gt 0) {
            foreach ($entry in $results) {
                $statusCode = Get-ObjectPropertyValue `
                    $entry `
                    'statusCode'
                if ($null -ne $statusCode) {
                    $analysis.StatusCodes += [int]$statusCode
                }
                $errorText = Get-ObjectPropertyValue $entry 'error'
                if (-not [string]::IsNullOrWhiteSpace(
                        [string]$errorText
                    )) {
                    $analysis.Errors += Remove-CredentialText (
                        [string]$errorText
                    )
                }
                if ($null -eq $analysis.Data) {
                    $analysis.Data = Get-ObjectPropertyValue $entry 'data'
                }
            }
        } else {
            $statusCode = Get-ObjectPropertyValue `
                $payload `
                'statusCode'
            if ($null -ne $statusCode) {
                $analysis.StatusCodes += [int]$statusCode
            }
            $errorText = Get-ObjectPropertyValue $payload 'error'
            if (-not [string]::IsNullOrWhiteSpace(
                    [string]$errorText
                )) {
                $analysis.Errors += Remove-CredentialText (
                    [string]$errorText
                )
            }
            $analysis.Data = $payload
        }
        $analysis.Succeeded =
            @(
                $analysis.StatusCodes | Where-Object { $_ -ge 400 }
            ).Count -eq 0 -and
            (Get-CollectionCount $analysis.Errors) -eq 0
    }
    catch {
        $analysis.PayloadParsed = $false
        $analysis.ParseError = 'WorkIQ JSON response could not be analyzed.'
    }
    return [pscustomobject]$analysis
}

function Invoke-WorkIqDiagnosticProbe {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Arguments,
        [AllowNull()][string]$Account,
        [switch]$UseAccount,
        [switch]$AllowTextOutput,
        [Parameter(Mandatory)][string[]]$FallbackPaths
    )

    if ($UseAccount -and [string]::IsNullOrWhiteSpace($Account)) {
        return [pscustomobject]@{
            Name = $Name
            Skipped = $true
            Reason = 'Peggy pinned WorkIQ account was unavailable.'
            Arguments = $Arguments
            Found = $null
            Path = $null
            ExitCode = $null
            Succeeded = $false
            PayloadParsed = $false
            ParseError = $null
            ResponseStatusCodes = @()
            ResponseErrors = @()
            ResponseRequestIds = @()
            AuthenticatedMail = $null
            AuthenticatedUserPrincipalName = $null
            AuthenticatedPrincipalId = $null
            AccountMatched = $null
            Stdout = ''
            Stderr = ''
            Output = ''
        }
    }

    $effectiveArguments = @($Arguments)
    if ($UseAccount) {
        $effectiveArguments += @('--account', $Account)
    }
    $result = Invoke-DiagnosticCommand `
        -Name $Name `
        -Candidates @('workiq.exe', 'workiq.cmd', 'workiq') `
        -Arguments $effectiveArguments `
        -OverrideVariable 'PEGGY_WORKIQ_PATH' `
        -FallbackPaths $FallbackPaths `
        -PreserveTechnicalOutput `
        -RetainRawOutputForAnalysis `
        -ResolveWorkIqWrapper
    $rawStdout = Get-ObjectPropertyValue `
        $result `
        'InternalRawStdout'
    $rawStderr = Get-ObjectPropertyValue `
        $result `
        'InternalRawStderr'
    $result.PSObject.Properties.Remove('InternalRawStdout')
    $result.PSObject.Properties.Remove('InternalRawStderr')
    $rawOutput = @($rawStdout, $rawStderr) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $rawOutput = $rawOutput -join [Environment]::NewLine
    $analysis = Get-WorkIqCommandAnalysis `
        -Result $result `
        -RawStdout $rawOutput
    if ($AllowTextOutput) {
        $analysis.ParseError = $null
        $analysis.Succeeded =
            $result.Found -and
            $result.ExitCode -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($result.Output) -and
            $result.Stderr -notmatch (
                '(?i)\b(error|critical|permission denied)\b'
            )
    }
    $rawStdout = $null
    $rawStderr = $null
    $rawOutput = $null
    $authenticatedMail = $null
    $authenticatedUserPrincipalName = $null
    $authenticatedPrincipalId = $null
    $accountMatched = $null
    if ($Name -like '*profile with Peggy fields' -and
        $null -ne $analysis.Data) {
        $authenticatedMail = Get-ObjectPropertyValue `
            $analysis.Data `
            'mail'
        $authenticatedUserPrincipalName = Get-ObjectPropertyValue `
            $analysis.Data `
            'userPrincipalName'
        $authenticatedPrincipalId = Get-ObjectPropertyValue `
            $analysis.Data `
            'id'
        if ($UseAccount) {
            $accountMatched =
                [string]::Equals(
                    $authenticatedMail,
                    $Account,
                    [StringComparison]::OrdinalIgnoreCase
                ) -or
                [string]::Equals(
                    $authenticatedUserPrincipalName,
                    $Account,
                    [StringComparison]::OrdinalIgnoreCase
                )
        }
    }
    $succeeded = $analysis.Succeeded
    if ($UseAccount -and
        $Name -like '*profile with Peggy fields' -and
        $accountMatched -ne $true) {
        $succeeded = $false
    }
    return [pscustomobject]@{
        Name = $Name
        Skipped = $false
        Reason = $null
        Arguments = @(
            $effectiveArguments | ForEach-Object {
                Remove-CredentialText $_
            }
        )
        Found = $result.Found
        Path = $result.Path
        ExitCode = $result.ExitCode
        Succeeded = $succeeded
        PayloadParsed = $analysis.PayloadParsed
        ParseError = $analysis.ParseError
        ResponseStatusCodes = $analysis.StatusCodes
        ResponseErrors = $analysis.Errors
        ResponseRequestIds = $analysis.RequestIds
        AuthenticatedMail = $authenticatedMail
        AuthenticatedUserPrincipalName = $authenticatedUserPrincipalName
        AuthenticatedPrincipalId = $authenticatedPrincipalId
        AccountMatched = $accountMatched
        Stdout = $result.Stdout
        Stderr = $result.Stderr
        Output = $result.Output
    }
}

function Send-DiagnosticEmail {
    param(
        [Parameter(Mandatory)][string]$AttachmentPath,
        [Parameter(Mandatory)][string]$Recipient,
        [Parameter(Mandatory)][string]$CollectedAt,
        [switch]$WithoutPrompt
    )

    if (-not $WithoutPrompt) {
        Write-Host ''
        Write-Host (
            'The ZIP contains detailed Peggy and WorkIQ diagnostics, ' +
            'including account identifiers, folder metadata, and raw ' +
            'technical output. Credentials and tokens are excluded.'
        )
        Write-Host "Recipient: $Recipient"
        $confirmation = Read-Host (
            "Type SEND to email the diagnostic ZIP, or press Enter to keep " +
            'it locally'
        )
        if ($confirmation -cne 'SEND') {
            Write-Host 'Diagnostic email was not sent.'
            return $false
        }
    }

    $outlook = $null
    $mail = $null
    $attachments = $null
    $attachment = $null
    try {
        $outlook = New-Object -ComObject Outlook.Application
        $mail = $outlook.CreateItem(0)
        $mail.To = $Recipient
        $mail.Subject = "Peggy diagnostics $CollectedAt"
        $mail.Body = @(
            'Hello,'
            ''
            "Attached is a Peggy diagnostic bundle generated at $CollectedAt."
            ''
            'The collector includes detailed account, configuration, folder, ' +
                'and raw technical error information. Credentials, tokens, ' +
                'customer message bodies, and files are excluded.'
        ) -join [Environment]::NewLine
        $attachments = $mail.Attachments
        $attachment = $attachments.Add(
            (Resolve-Path -LiteralPath $AttachmentPath).Path
        )
        $mail.Send()
        Write-Host "Diagnostic ZIP emailed to $Recipient."
        return $true
    }
    catch {
        throw (
            "The diagnostic ZIP was created, but classic Outlook could not " +
            "email it to $Recipient. The ZIP remains at '$AttachmentPath'. " +
            "Error: $($_.Exception.Message)"
        )
    }
    finally {
        foreach ($comObject in @(
                $attachment,
                $attachments,
                $mail,
                $outlook
            )) {
            if ($null -ne $comObject -and
                [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject(
                    $comObject
                )
            }
        }
    }
}

function Get-CollectorMetadata {
    $metadata = [ordered]@{
        SchemaVersion = 2
        ScriptPath = Protect-Text $PSCommandPath
        ScriptSha256 = $null
        ScriptModifiedUtc = $null
        RepositoryRoot = $null
        GitCommit = $null
        GitBranch = $null
        GitDirty = $null
    }
    if (-not [string]::IsNullOrWhiteSpace($PSCommandPath) -and
        (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) {
        $scriptFile = Get-Item -LiteralPath $PSCommandPath
        $metadata.ScriptSha256 = Get-Sha256 -Path $scriptFile.FullName
        $metadata.ScriptModifiedUtc =
            $scriptFile.LastWriteTimeUtc.ToString('O')
        $git = Resolve-CommandPath -Names @('git.exe', 'git')
        if (-not [string]::IsNullOrWhiteSpace($git)) {
            $previousErrorActionPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $scriptDirectory = $scriptFile.DirectoryName
                $repositoryRoot = (
                    & $git `
                        -C $scriptDirectory `
                        rev-parse `
                        --show-toplevel `
                        2>$null |
                        Out-String
                ).Trim()
                if ($LASTEXITCODE -eq 0 -and
                    -not [string]::IsNullOrWhiteSpace($repositoryRoot)) {
                    $metadata.RepositoryRoot =
                        Protect-Text $repositoryRoot
                    $metadata.GitCommit = (
                        & $git `
                            -C $repositoryRoot `
                            rev-parse `
                            HEAD `
                            2>$null |
                            Out-String
                    ).Trim()
                    $metadata.GitBranch = (
                        & $git `
                            -C $repositoryRoot `
                            branch `
                            --show-current `
                            2>$null |
                            Out-String
                    ).Trim()
                    $status = (
                        & $git `
                            -C $repositoryRoot `
                            status `
                            --porcelain `
                            2>$null |
                            Out-String
                    ).Trim()
                    $metadata.GitDirty =
                        -not [string]::IsNullOrWhiteSpace($status)
                }
            }
            catch {
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
        }
    }
    return [pscustomobject]$metadata
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = (Get-Location).Path
}
$OutputDirectory = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($OutputDirectory)
)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$staging = Join-Path (
    [IO.Path]::GetTempPath()
) "Peggy-Diagnostics-$timestamp-$PID"
$zipPath = Join-Path $OutputDirectory "Peggy-Diagnostics-$timestamp.zip"
New-Item -ItemType Directory -Path $staging | Out-Null

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\Peggy'
$defaultStateRoot = Join-Path $env:LOCALAPPDATA 'Peggy'
$stateResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path $defaultStateRoot 'state.dat') `
    -OverrideVariable 'PEGGY_STATE_PATH' `
    -ExpectedType Leaf
$statePath = $stateResolution.Path
$modelsResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path $defaultStateRoot 'models') `
    -OverrideVariable 'PEGGY_SPEECH_MODELS' `
    -ExpectedType Container
$modelsRoot = $modelsResolution.Path
$manifestPath = if ($null -eq $modelsRoot) {
    $null
} else {
    Join-Path $modelsRoot 'speech-models.json'
}
$logResolution = Resolve-DiagnosticDataPath `
    -DefaultPath (Join-Path ([IO.Path]::GetTempPath()) 'peggy.log') `
    -OverrideVariable 'PEGGY_LOG_PATH' `
    -ExpectedType Leaf
$logPath = $logResolution.Path
$copilotFallbackPaths = @(
    Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\copilot.exe'
    Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\copilot.exe'
)
$workIqFallbackPaths = @(
    Join-Path (
        Join-Path $env:APPDATA 'npm'
    ) 'node_modules\@microsoft\workiq\bin\win-x64\workiq.exe'
)

try {
    Write-JsonFile `
        -Path (Join-Path $staging 'collector-metadata.json') `
        -Value (Get-CollectorMetadata)
    $os = Get-CimInstance Win32_OperatingSystem
    $computer = Get-CimInstance Win32_ComputerSystem
    Write-JsonFile `
        -Path (Join-Path $staging 'system.json') `
        -Value ([ordered]@{
            CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            OsCaption = $os.Caption
            OsVersion = $os.Version
            BuildNumber = $os.BuildNumber
            OsArchitecture = $os.OSArchitecture
            ProcessArchitecture = (
                [Runtime.InteropServices.RuntimeInformation]::
                    ProcessArchitecture.ToString()
            )
            PowerShellVersion = $PSVersionTable.PSVersion.ToString()
            PowerShellEdition = $PSVersionTable.PSEdition
            MemoryGiB = [Math]::Round(
                $computer.TotalPhysicalMemory / 1GB,
                1
            )
        })

    $installFiles = @()
    $coreHashes = @()
    if (Test-Path -LiteralPath $installRoot -PathType Container) {
        $allFiles = @(Get-ChildItem `
            -LiteralPath $installRoot `
            -Recurse `
            -File `
            -Force)
        $installFiles = foreach ($file in $allFiles) {
            [pscustomobject]@{
                RelativePath = $file.FullName.Substring(
                    $installRoot.Length + 1
                )
                Length = $file.Length
                ModifiedUtc = $file.LastWriteTimeUtc.ToString('O')
            }
        }
        $coreNames = @(
            'peggy-tray.exe',
            'peggy-tray.dll',
            'Peggy.Core.dll',
            'version.txt',
            'install.ps1',
            'uninstall.ps1',
            'Peggy.Mcp\peggy-mcp.exe',
            'Peggy.Speech.Host\peggy-speech-host.exe'
        )
        $coreHashes = foreach ($relativePath in $coreNames) {
            $file = Join-Path $installRoot $relativePath
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                [pscustomobject]@{
                    RelativePath = $relativePath
                    Length = (Get-Item -LiteralPath $file).Length
                    Sha256 = Get-Sha256 -Path $file
                }
            }
        }
    }
    $trayAssembly = Join-Path $installRoot 'peggy-tray.dll'
    $assemblyVersion = if (Test-Path -LiteralPath $trayAssembly) {
        [Reflection.AssemblyName]::GetAssemblyName(
            $trayAssembly
        ).Version.ToString()
    } else {
        $null
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'installation.json') `
        -Value ([ordered]@{
            InstallRoot = Protect-Text $installRoot
            Installed = Test-Path `
                -LiteralPath (Join-Path $installRoot 'peggy-tray.exe')
            Version = if (Test-Path (
                    Join-Path $installRoot 'version.txt'
                )) {
                (Get-Content `
                    -LiteralPath (Join-Path $installRoot 'version.txt') `
                    -Raw).Trim()
            } else {
                $null
            }
            AssemblyVersion = $assemblyVersion
            FileCount = $installFiles.Count
            Files = $installFiles
            CoreFileHashes = $coreHashes
        })

    $processNames = @(
        'peggy-tray.exe',
        'peggy-mcp.exe',
        'peggy-speech-host.exe',
        'copilot.exe',
        'workiq.exe'
    )
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in $processNames
    } | Select-Object `
        Name,
        ProcessId,
        ParentProcessId,
        @{Name = 'ExecutablePath'; Expression = {
            Protect-Text $_.ExecutablePath
        }},
        @{Name = 'CommandLine'; Expression = {
            Remove-CredentialText $_.CommandLine
        }})
    Write-JsonFile `
        -Path (Join-Path $staging 'processes.json') `
        -Value $processes

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runProperty = Get-ItemProperty `
        $runKey `
        -Name Peggy `
        -ErrorAction SilentlyContinue
    $runValue = if ($null -eq $runProperty) {
        $null
    } else {
        $runProperty.Peggy
    }
    $programs = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::Programs
    )
    $installedApps = @()
    foreach ($root in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
        )) {
        if (-not (Test-Path $root)) {
            continue
        }
        foreach ($key in Get-ChildItem $root) {
            $value = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($null -ne $value -and
                $null -ne $value.PSObject.Properties['DisplayName'] -and
                $value.DisplayName -eq 'Peggy') {
                $displayVersion = $value.PSObject.Properties[
                    'DisplayVersion'
                ]
                $uninstallString = $value.PSObject.Properties[
                    'UninstallString'
                ]
                $installedApps += [pscustomobject]@{
                    RegistryRoot = $root
                    ProductCode = $key.PSChildName
                    DisplayVersion = if ($null -eq $displayVersion) {
                        $null
                    } else {
                        $displayVersion.Value
                    }
                    UninstallString = if ($null -eq $uninstallString) {
                        $null
                    } else {
                        Protect-Text $uninstallString.Value
                    }
                }
            }
        }
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'startup-and-registration.json') `
        -Value ([ordered]@{
            StartupValue = Protect-Text $runValue
            RootShortcutExists = Test-Path (
                Join-Path $programs 'Peggy.lnk'
            )
            FolderShortcutExists = Test-Path (
                Join-Path $programs 'Peggy\Peggy.lnk'
            )
            InstalledAppsEntries = $installedApps
        })

    $stateMetadata = [ordered]@{
        Exists = (
            $null -ne $statePath -and
            (Test-Path -LiteralPath $statePath -PathType Leaf)
        )
        Path = $stateResolution.DisplayPath
        ResolutionError = $stateResolution.Error
    }
    if ($stateMetadata.Exists) {
        $stateFile = Get-Item -LiteralPath $statePath
        $stateMetadata.Length = $stateFile.Length
        $stateMetadata.ModifiedUtc = $stateFile.LastWriteTimeUtc.ToString('O')
        $stateMetadata.Sha256 = Get-Sha256 -Path $statePath
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'state-metadata-only.json') `
        -Value $stateMetadata
    $operationalState = Get-PeggyOperationalState `
        -Path $statePath `
        -DisplayPath $stateResolution.DisplayPath `
        -ResolutionError $stateResolution.Error
    Write-JsonFile `
        -Path (Join-Path $staging 'state-operational.json') `
        -Value $operationalState

    $modelFiles = @()
    if ($null -ne $modelsRoot -and
        (Test-Path -LiteralPath $modelsRoot -PathType Container)) {
        $modelFiles = @(Get-ChildItem `
            -LiteralPath $modelsRoot `
            -Recurse `
            -File `
            -Force | Select-Object `
                @{Name = 'RelativePath'; Expression = {
                    $_.FullName.Substring($modelsRoot.Length + 1)
                }},
                Length,
                @{Name = 'ModifiedUtc'; Expression = {
                    $_.LastWriteTimeUtc.ToString('O')
                }})
    }
    [long]$totalModelBytes = 0
    foreach ($modelFile in $modelFiles) {
        $totalModelBytes += $modelFile.Length
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'speech-models.json') `
        -Value ([ordered]@{
            RootPath = $modelsResolution.DisplayPath
            ResolutionError = $modelsResolution.Error
            ManifestExists = (
                $null -ne $manifestPath -and
                (Test-Path -LiteralPath $manifestPath -PathType Leaf)
            )
            FileCount = $modelFiles.Count
            TotalBytes = $totalModelBytes
            Files = $modelFiles
        })

    if ($null -ne $logPath -and
        (Test-Path -LiteralPath $logPath -PathType Leaf)) {
        $logContent = (
            Get-Content `
                -LiteralPath $logPath `
                -Tail $LogTailLines `
                -ErrorAction Stop
        ) -join [Environment]::NewLine
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.txt') `
            -Content (Protect-Text $logContent)
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.raw.txt') `
            -Content (Remove-CredentialText $logContent)
    } else {
        $logDetail = if ([string]::IsNullOrWhiteSpace(
                $logResolution.Error
            )) {
            "Peggy log not found at $($logResolution.DisplayPath)."
        } else {
            $logResolution.Error
        }
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.txt') `
            -Content $logDetail
        Write-TextFile `
            -Path (Join-Path $staging 'peggy.log.raw.txt') `
            -Content $logDetail
    }

    Export-PeggySetupLogs `
        -SourceDirectory ([IO.Path]::GetTempPath()) `
        -DestinationDirectory $staging `
        -LogTailLines $LogTailLines

    $scopeFiles = @(Get-ChildItem `
        -LiteralPath ([IO.Path]::GetTempPath()) `
        -File `
        -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -like 'peggy-*.json'
        } | Select-Object `
            Name,
            Length,
            @{Name = 'ModifiedUtc'; Expression = {
                $_.LastWriteTimeUtc.ToString('O')
            }})
    Write-JsonFile `
        -Path (Join-Path $staging 'temporary-scope-metadata-only.json') `
        -Value $scopeFiles

    $safeVariables = @(
        'PEGGY_MESSAGE_PROVIDER',
        'PEGGY_COPILOT_MODEL',
        'PEGGY_INCLUDE_ACTIVE_CASES_FOLDER',
        'PEGGY_OUTLOOK_SCOPE',
        'PEGGY_TEAMS_SCOPE',
        'PEGGY_TEAMS_MODE',
        'PEGGY_TEAMS_MONITOR_CHATS',
        'PEGGY_LOG_PATH',
        'PEGGY_STATE_PATH',
        'PEGGY_COPILOT_PATH',
        'PEGGY_WORKIQ_PATH',
        'PEGGY_SPEECH_MODELS',
        'PEGGY_SPEECH_HOST_PATH',
        'PEGGY_SPEECH_MODEL_ROOT',
        'PEGGY_SPEECH_THREADS',
        'PEGGY_SPEECH_VOICE_ID',
        'PEGGY_SPEECH_SPEED',
        'PEGGY_SPEECH_INPUT_GAIN'
    )
    $sensitiveVariables = @(
        'PEGGY_WORKIQ_ACCOUNT',
        'COPILOT_GITHUB_TOKEN',
        'GH_TOKEN',
        'GITHUB_TOKEN'
    )
    $environmentStatus = @(
        foreach ($name in $safeVariables) {
            $value = [Environment]::GetEnvironmentVariable($name)
            [pscustomobject]@{
                Name = $name
                Present = -not [string]::IsNullOrWhiteSpace($value)
                Value = Protect-Text $value
            }
        }
        foreach ($name in $sensitiveVariables) {
            $value = [Environment]::GetEnvironmentVariable($name)
            [pscustomobject]@{
                Name = $name
                Present = -not [string]::IsNullOrWhiteSpace($value)
                Value = if ([string]::IsNullOrWhiteSpace($value)) {
                    $null
                } else {
                    '[redacted]'
                }
            }
        }
    )
    Write-JsonFile `
        -Path (Join-Path $staging 'environment-overrides.json') `
        -Value $environmentStatus

    $commands = @(
        Invoke-DiagnosticCommand `
            -Name 'PowerShell 7' `
            -Candidates @('pwsh.exe', 'pwsh') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'Node.js' `
            -Candidates @('node.exe', 'node') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'npm' `
            -Candidates @('npm.cmd', 'npm') `
            -Arguments @('--version')
        if ($RunWorkIqTests -and -not $RunConnectivityTests) {
            [pscustomobject]@{
                Name = 'GitHub Copilot CLI'
                Found = $null
                Path = $null
                ExitCode = $null
                Stdout = ''
                Stderr = ''
                Output = 'Skipped in WorkIQ-only mode.'
            }
        } else {
            Invoke-DiagnosticCommand `
                -Name 'GitHub Copilot CLI' `
                -Candidates @('copilot.exe', 'copilot') `
                -Arguments @('version') `
                -OverrideVariable 'PEGGY_COPILOT_PATH' `
                -FallbackPaths $copilotFallbackPaths
        }
        Invoke-DiagnosticCommand `
            -Name 'Microsoft WorkIQ' `
            -Candidates @('workiq.exe', 'workiq.cmd', 'workiq') `
            -Arguments @('version') `
            -OverrideVariable 'PEGGY_WORKIQ_PATH' `
            -FallbackPaths $workIqFallbackPaths
        Invoke-DiagnosticCommand `
            -Name '.NET' `
            -Candidates @('dotnet.exe', 'dotnet') `
            -Arguments @('--info')
        Invoke-DiagnosticCommand `
            -Name 'curl' `
            -Candidates @('curl.exe') `
            -Arguments @('--version')
        Invoke-DiagnosticCommand `
            -Name 'tar' `
            -Candidates @('tar.exe') `
            -Arguments @('--version')
    )
    Write-JsonFile `
        -Path (Join-Path $staging 'prerequisites.json') `
        -Value $commands

    $workIqConfig = Join-Path $env:USERPROFILE '.work-iq-cli\.workiq.json'
    $eulaMarkerPresent = $false
    if (Test-Path -LiteralPath $workIqConfig -PathType Leaf) {
        $configText = Get-Content -LiteralPath $workIqConfig -Raw
        $eulaMarkerPresent = $configText -match (
            '(?i)"?I-accept-EULA"?\s*:\s*true'
        )
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'workiq-config-metadata-only.json') `
        -Value ([ordered]@{
            ConfigExists = Test-Path -LiteralPath $workIqConfig
            EulaMarkerPresent = $eulaMarkerPresent
        })

    $eventResults = @()
    try {
        $eventResults = @(Get-WinEvent `
            -FilterHashtable @{
                LogName = 'Application'
                StartTime = (Get-Date).AddDays(-7)
            } `
            -MaxEvents 1000 `
            -ErrorAction Stop | Where-Object {
                $_.Message -match (
                    '(?i)\bPeggy\b|peggy-tray|peggy-mcp|' +
                    'peggy-speech-host'
                )
            } | Select-Object -First 100 | ForEach-Object {
                [pscustomobject]@{
                    TimeCreatedUtc = $_.TimeCreated.ToUniversalTime().
                        ToString('O')
                    ProviderName = $_.ProviderName
                    Id = $_.Id
                    Level = $_.LevelDisplayName
                    Message = Remove-CredentialText $_.Message
                }
            })
    }
    catch {
        $eventResults = @(
            [pscustomobject]@{
                CollectionError = Protect-Text $_.Exception.Message
            }
        )
    }
    Write-JsonFile `
        -Path (Join-Path $staging 'windows-application-events.json') `
        -Value $eventResults

    if ($RunConnectivityTests -or $RunWorkIqTests) {
        $stateAccount = Get-ObjectPropertyValue `
            $operationalState `
            'PinnedAccount'
        $environmentAccount = [Environment]::GetEnvironmentVariable(
            'PEGGY_WORKIQ_ACCOUNT'
        )
        $effectiveWorkIqAccount = if (-not [string]::IsNullOrWhiteSpace(
                $WorkIqAccount
            )) {
            $WorkIqAccount
        } elseif (-not [string]::IsNullOrWhiteSpace($stateAccount)) {
            $stateAccount
        } elseif (-not [string]::IsNullOrWhiteSpace(
                $environmentAccount
            )) {
            $environmentAccount
        } else {
            $null
        }
        $workIqAccountSource = if (-not [string]::IsNullOrWhiteSpace(
                $WorkIqAccount
            )) {
            'Parameter'
        } elseif (-not [string]::IsNullOrWhiteSpace($stateAccount)) {
            'PeggyState'
        } elseif (-not [string]::IsNullOrWhiteSpace(
                $environmentAccount
            )) {
            'Environment'
        } else {
            'Unavailable'
        }

        $copilotTest = $null
        if ($RunConnectivityTests) {
            $previousCi = $env:CI
            try {
                $env:CI = 'true'
                $copilotTest = Invoke-DiagnosticCommand `
                    -Name 'Copilot generic connectivity' `
                    -Candidates @('copilot.exe', 'copilot') `
                    -Arguments @(
                        '-p',
                        'Reply with OK only.',
                        '--silent',
                        '--disable-builtin-mcps',
                        '--no-custom-instructions',
                        '--no-remote',
                        '--no-remote-export'
                    ) `
                    -OverrideVariable 'PEGGY_COPILOT_PATH' `
                    -FallbackPaths $copilotFallbackPaths `
                    -PreserveTechnicalOutput
            }
            finally {
                $env:CI = $previousCi
            }
        }
        $configurationProbe = Invoke-WorkIqDiagnosticProbe `
            -Name 'WorkIQ configuration' `
            -Arguments @(
                'config',
                'show',
                '--log-level',
                'Debug'
            ) `
            -Account $effectiveWorkIqAccount `
            -AllowTextOutput `
            -FallbackPaths $workIqFallbackPaths
        $defaultProfileProbe = Invoke-WorkIqDiagnosticProbe `
            -Name 'WorkIQ default profile with Peggy fields' `
            -Arguments @(
                'fetch',
                '--urls',
                '/me?$select=id,mail,userPrincipalName,proxyAddresses',
                '--log-level',
                'Debug'
            ) `
            -Account $effectiveWorkIqAccount `
            -FallbackPaths $workIqFallbackPaths
        $pinnedProfileProbe = Invoke-WorkIqDiagnosticProbe `
            -Name 'WorkIQ pinned profile with Peggy fields' `
            -Arguments @(
                'fetch',
                '--urls',
                '/me?$select=id,mail,userPrincipalName,proxyAddresses',
                '--log-level',
                'Debug'
            ) `
            -Account $effectiveWorkIqAccount `
            -UseAccount `
            -FallbackPaths $workIqFallbackPaths
        $workIqProbes = @(
            $configurationProbe
            $defaultProfileProbe
            $pinnedProfileProbe
        )
        $statePrincipalId = Get-ObjectPropertyValue `
            $operationalState `
            'PinnedPrincipalId'
        $principalMatched = if (-not [string]::IsNullOrWhiteSpace(
                $statePrincipalId
            )) {
            [string]::Equals(
                $statePrincipalId,
                $pinnedProfileProbe.AuthenticatedPrincipalId,
                [StringComparison]::OrdinalIgnoreCase
            )
        } else {
            $pinnedProfileProbe.AccountMatched -eq $true
        }
        if ($principalMatched) {
            $workIqProbes += @(
            Invoke-WorkIqDiagnosticProbe `
                -Name 'WorkIQ pinned Inbox metadata' `
                -Arguments @(
                    'fetch',
                    '--urls',
                    (
                        '/me/mailFolders/inbox?$select=id,displayName,' +
                        'parentFolderId,childFolderCount'
                    ),
                    '--log-level',
                    'Debug'
                ) `
                -Account $effectiveWorkIqAccount `
                -UseAccount `
                -FallbackPaths $workIqFallbackPaths
            Invoke-WorkIqDiagnosticProbe `
                -Name 'WorkIQ pinned Inbox child folders' `
                -Arguments @(
                    'fetch',
                    '--urls',
                    (
                        '/me/mailFolders/inbox/childFolders?' +
                        '$top=50&$select=id,displayName,parentFolderId,' +
                        'childFolderCount'
                    ),
                    '--log-level',
                    'Debug'
                ) `
                -Account $effectiveWorkIqAccount `
                -UseAccount `
                -FallbackPaths $workIqFallbackPaths
            Invoke-WorkIqDiagnosticProbe `
                -Name 'WorkIQ pinned Inbox message permission' `
                -Arguments @(
                    'fetch',
                    '--urls',
                    (
                        '/me/mailFolders/inbox/messages?' +
                        '$top=1&$select=id,receivedDateTime,isRead'
                    ),
                    '--log-level',
                    'Debug'
                ) `
                -Account $effectiveWorkIqAccount `
                -UseAccount `
                -FallbackPaths $workIqFallbackPaths
            Invoke-WorkIqDiagnosticProbe `
                -Name 'WorkIQ pinned tenant policies' `
                -Arguments @(
                    'policy',
                    'list',
                    '--log-level',
                    'Debug'
                ) `
                -Account $effectiveWorkIqAccount `
                -UseAccount `
                -AllowTextOutput `
                -FallbackPaths $workIqFallbackPaths
            )
        }
        else {
            $workIqProbes += [pscustomobject]@{
                Name = 'WorkIQ account-scoped probes'
                Skipped = $true
                Reason = (
                    'The authenticated WorkIQ principal did not match Peggy''s ' +
                    'verified principal. Inbox, folder, message, and policy ' +
                    'probes were not run.'
                )
                Arguments = @()
                Found = $pinnedProfileProbe.Found
                Path = $pinnedProfileProbe.Path
                ExitCode = $null
                Succeeded = $false
                PayloadParsed = $false
                ParseError = $null
                ResponseStatusCodes = @()
                ResponseErrors = @()
                ResponseRequestIds = @()
                AuthenticatedMail = $null
                AuthenticatedUserPrincipalName = $null
                AuthenticatedPrincipalId = $null
                AccountMatched = $false
                Stdout = ''
                Stderr = ''
                Output = ''
            }
        }
        Write-JsonFile `
            -Path (Join-Path $staging 'workiq-deep-diagnostics.json') `
            -Value ([ordered]@{
                AccountSource = $workIqAccountSource
                Account = $effectiveWorkIqAccount
                Probes = $workIqProbes
            })
        $workIq = if (-not [string]::IsNullOrWhiteSpace(
                $effectiveWorkIqAccount
            )) {
            $workIqProbes | Where-Object {
                $_.Name -eq 'WorkIQ pinned profile with Peggy fields'
            } | Select-Object -First 1
        } else {
            $workIqProbes | Where-Object {
                $_.Name -eq 'WorkIQ default profile with Peggy fields'
            } | Select-Object -First 1
        }
        $connectivity = @(
            if ($null -ne $copilotTest) {
                [pscustomobject]@{
                    Name = $copilotTest.Name
                    Found = $copilotTest.Found
                    ExitCode = $copilotTest.ExitCode
                    Succeeded = $copilotTest.ExitCode -eq 0 -and
                        $copilotTest.Output.Trim() -eq 'OK'
                    Detail = $copilotTest.Output
                }
            }
            [pscustomobject]@{
                Name = $workIq.Name
                Found = $workIq.Found
                ExitCode = $workIq.ExitCode
                Succeeded = $workIq.Succeeded
                Detail = $workIq.Output
            }
        )
        Write-JsonFile `
            -Path (Join-Path $staging 'connectivity-tests.json') `
            -Value $connectivity
    } else {
        Write-TextFile `
            -Path (Join-Path $staging 'connectivity-tests.txt') `
            -Content (
                'Connectivity tests were not requested. Run the collector ' +
                'with -RunConnectivityTests to test Copilot and WorkIQ ' +
                'using generic, non-customer input, or -RunWorkIqTests to ' +
                'run only deterministic WorkIQ probes.'
            )
    }

    Write-TextFile `
        -Path (Join-Path $staging 'README.txt') `
        -Content @"
Peggy diagnostic bundle

Collected:
- Collector script hash and source Git commit when available
- Operating system and PowerShell metadata
- Peggy version, installed file inventory, and selected file hashes
- Process command lines, startup, shortcut, and Installed apps status
- Encrypted-state file metadata, hash, and decrypted operational summary
- Speech-model inventory only
- Raw and redacted Peggy lifecycle logs
- Redacted main Burn setup logs (newest 10 modified within 14 days), with
  collection/error manifest; each is limited to the last $LogTailLines lines
  and a 4 MiB tail, with lines over 65,536 characters omitted
- Temporary Peggy scope-file metadata only
- Prerequisite versions
- Full Peggy-related Windows Application event messages
- Optional Copilot connectivity with full technical errors
- Optional WorkIQ configuration, account, policy, profile, Inbox, folder, and
  message-permission probes with full technical output

Explicitly excluded:
- Credentials, tokens, passwords, cookies, and authentication caches
- Customer message bodies, Teams message bodies, files, and calendar contents
- Copilot session-state, prompts, responses, and command history
- Environment token values
- Temporary Peggy focus/context/action file contents
- Speech model binaries

Review the ZIP before sharing it outside your support team.
"@

    Compress-Archive `
        -Path (Join-Path $staging '*') `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Host ''
Write-Host 'Peggy diagnostic ZIP created:'
Write-Host "  $zipPath"
Write-Host ''
Write-Host 'Review the ZIP before sharing it.'

if (-not $NoEmail) {
    [void](Send-DiagnosticEmail `
        -AttachmentPath $zipPath `
        -Recipient $EmailTo `
        -CollectedAt $timestamp `
        -WithoutPrompt:$SendWithoutPrompt)
}
