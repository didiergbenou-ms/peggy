using System.Diagnostics;
using System.IO;
using System.Text;

namespace Peggy.Core.Tests;

internal static class SetupLogCollectorSmokeTests
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.Combine(directory, "setup-log-collector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "test-collector.ps1");
            await File.WriteAllTextAsync(script, TestScript, new UTF8Encoding(false));
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-CollectorPath", FindCollector(), "-Root", root
            })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            TestAssert.True(process.ExitCode == 0,
                "Synthetic setup-log collector tests failed: " + await stdout + await stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindCollector()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "scripts", "Collect-PeggyDiagnostics.ps1");
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException("The repository diagnostic collector was not found.");
    }

    private const string TestScript = """
        param([string]$CollectorPath, [string]$Root)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        $now = [datetime]::Parse('2026-09-24T12:00:00Z').ToUniversalTime()
        $utf8 = New-Object Text.UTF8Encoding($false)
        function Assert-SetupLog {
            param([bool]$Condition, [string]$Message)
            if (-not $Condition) { throw "Setup log collector: $Message" }
        }
        # Import only isolated helper definitions; never run the collector entry point.
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            $CollectorPath, [ref]$tokens, [ref]$errors)
        Assert-SetupLog ($errors.Count -eq 0) 'Collector script must parse.'
        $comActivation = @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'New-Object' -and
                @($node.CommandElements | Where-Object {
                    $_ -is [Management.Automation.Language.CommandParameterAst] -and
                        $_.ParameterName -ieq 'ComObject'
                }).Count -gt 0
        }, $true))
        Assert-SetupLog ($comActivation.Count -eq 0) `
            'Collector must not use COM object activation.'
        $functions = @{}
        foreach ($name in @(
            'Protect-Text', 'Remove-CredentialText', 'Write-TextFile', 'Write-JsonFile',
            'Read-PeggySetupLogTail', 'Export-PeggySetupLogs'
        )) {
            $definitions = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $name
            }, $false))
            Assert-SetupLog ($definitions.Count -eq 1) "Expected one $name function."
            $functions[$name] = $definitions[0].Extent.Text
            . ([scriptblock]::Create($functions[$name]))
        }
        function New-Case {
            param([string]$Name)
            $source = Join-Path $Root ($Name + '-source')
            $destination = Join-Path $Root ($Name + '-destination')
            [void][IO.Directory]::CreateDirectory($source)
            [void][IO.Directory]::CreateDirectory($destination)
            return [pscustomobject]@{ Source = $source; Destination = $destination }
        }
        function Add-Log {
            param($Case, [string]$Name, [string]$Content, [datetime]$Modified = $now)
            $path = Join-Path $Case.Source $Name
            [IO.File]::WriteAllText($path, $Content, $utf8)
            [IO.File]::SetLastWriteTimeUtc($path, $Modified)
            return $path
        }
        function Read-Manifest {
            param($Case)
            return ConvertFrom-Json ([IO.File]::ReadAllText(
                (Join-Path $Case.Destination 'setup-logs-manifest.json')))
        }
        function Read-Collected {
            param($Case, $Entry)
            return [IO.File]::ReadAllText((Join-Path $Case.Destination $Entry.OutputRelativePath))
        }
        $selection = New-Case 'selection'
        $expected = @()
        for ($index = 0; $index -lt 12; $index++) {
            $name = 'Peggy_' + $now.AddMinutes($index).ToString('yyyyMMddHHmmss') + '.log'
            $expected += $name
            [void](Add-Log $selection $name "native install event $index" $now.AddHours(-$index))
        }
        foreach ($name in @(
            'Peggy_20260924130000_000_PeggyMsi.log',
            'Peggy_20260924130000_001_Msi.log',
            'Peggy_20260924130000.log.bak',
            'OtherPeggy_20260924130000.log',
            'peggy_20260924130000.log',
            'Peggy_2026092413000.log',
            'Peggy_202609241300000.log',
            'Peggy_20260924130000_extra.log',
            'peggy.log', 'command-history.log', 'authentication-cache.json'
        )) {
            [void](Add-Log $selection $name 'MUST_NOT_COLLECT')
        }
        [void](Add-Log $selection 'Peggy_20260901000000.log' 'TOO_OLD' $now.AddDays(-15))
        [void](Add-Log $selection 'Peggy_20260925000000.log' 'FUTURE' $now.AddDays(1))
        $nested = Join-Path $selection.Source 'nested'
        [void][IO.Directory]::CreateDirectory($nested)
        [IO.File]::WriteAllText((Join-Path $nested 'Peggy_20260924130000.log'), 'NESTED')
        [void][IO.Directory]::CreateDirectory((Join-Path $selection.Source 'Peggy_20260924140000.log'))
        Export-PeggySetupLogs -SourceDirectory $selection.Source `
            -DestinationDirectory $selection.Destination -NowUtc $now
        $manifest = Read-Manifest $selection
        Assert-SetupLog ($manifest.Status -eq 'Collected' -and $manifest.Files.Count -eq 10 -and
            $manifest.CollectedLogCount -eq 10 -and $manifest.EligibleLogCount -eq 12 -and
            $manifest.OmittedLogCount -eq 2) 'Exact main-log filtering and maximum file count.'
        for ($index = 0; $index -lt 10; $index++) {
            $entry = $manifest.Files[$index]
            Assert-SetupLog ($entry.SourceRelativePath -ceq $expected[$index]) 'Newest modified logs first.'
            Assert-SetupLog ($entry.SourceBytes -gt 0 -and $entry.ModifiedUtc -and
                -not $entry.Truncated -and $entry.Status -eq 'Collected') 'Source and truncation metadata.'
            Assert-SetupLog (-not [IO.Path]::IsPathRooted($entry.OutputRelativePath) -and
                $entry.OutputRelativePath.StartsWith('setup-logs\')) 'Output paths are relative.'
            Assert-SetupLog ((Read-Collected $selection $entry) -eq "native install event $index") `
                'Selected log content must be retained.'
        }
        Assert-SetupLog (@(Get-ChildItem -LiteralPath $selection.Destination -File -Recurse).Count -eq 11) `
            'Only a manifest and ten redacted setup logs are written, without raw copies.'

        $age = New-Case 'age'
        [void](Add-Log $age 'Peggy_20260910000000.log' 'AT_CUTOFF' $now.AddDays(-14))
        [void](Add-Log $age 'Peggy_20260909000000.log' 'BEFORE_CUTOFF' $now.AddDays(-14).AddSeconds(-1))
        Export-PeggySetupLogs $age.Source $age.Destination -NowUtc $now
        $manifest = Read-Manifest $age
        Assert-SetupLog ($manifest.Files.Count -eq 1 -and
            (Read-Collected $age $manifest.Files[0]) -eq 'AT_CUTOFF') 'Inclusive fourteen-day cutoff.'

        $privacy = New-Case 'privacy'
        $session = '0123456789abcdef0123456789abcdef'
        $content = @(
            '[1234:5678][2026-09-24T10:00:00]i301: Applying execute package: PeggyMsi',
            ('[1234:5678][2026-09-24T10:00:01]i000: PEGGY_SETUP {"sessionId":"' + $session +
             '","stage":"settings.save","event":"failed","exceptionType":"System.IO.IOException",' +
             '"hresult":"0x80070005","password":"QUOTED_SECRET with spaces","account":"person@example.test"}'),
            'Authorization: Bearer BEARER_SECRET',
            'refresh_token=REFRESH_SECRET; client_secret="CLIENT_SECRET"; --password CLI_SECRET',
            'token=TOKEN_SECRET; cookie=COOKIE_SECRET',
            'ghp_abcdefghijklmnopqrstuvwxyz123456',
            'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJub3QtcmVhbCJ9.c3ludGhldGljU2lnbmF0dXJl',
            'redirect=https://example.test/auth?secret=URL_SECRET',
            '[1234:5678][2026-09-24T10:00:02]i399: Apply complete, result: 0x80070005',
            '[1234:5678][2026-09-24T10:00:03]w354: Failed to remove cached bundle'
        ) -join "`r`n"
        [void](Add-Log $privacy 'Peggy_20260924100000.log' $content)
        Export-PeggySetupLogs $privacy.Source $privacy.Destination -NowUtc $now
        $manifest = Read-Manifest $privacy
        $safe = Read-Collected $privacy $manifest.Files[0]
        foreach ($secret in @('QUOTED_SECRET', 'BEARER_SECRET', 'REFRESH_SECRET', 'CLIENT_SECRET',
            'CLI_SECRET', 'TOKEN_SECRET', 'COOKIE_SECRET', 'ghp_abcdefghijklmnopqrstuvwxyz123456',
            'eyJhbGciOiJIUzI1NiJ9', 'person@example.test', 'example.test/auth', 'URL_SECRET')) {
            Assert-SetupLog (-not $safe.Contains($secret)) "Credential/identity redaction: $secret"
        }
        foreach ($retained in @('PEGGY_SETUP', $session, 'settings.save', '"event":"failed"',
            'System.IO.IOException', '0x80070005', '[1234:5678]', 'Applying execute package',
            'Apply complete', 'Failed to remove cached bundle')) {
            Assert-SetupLog ($safe.Contains($retained)) "Operational failure context: $retained"
        }
        $zipPath = Join-Path $Root 'synthetic-setup-diagnostics.zip'
        Compress-Archive -Path (Join-Path $privacy.Destination '*') -DestinationPath $zipPath -Force
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entries = @($zip.Entries | Where-Object { $_.Name -ne '' })
            $logEntry = @($entries | Where-Object {
                $_.FullName.Replace('\', '/') -eq 'setup-logs/Peggy_20260924100000.log.txt'
            })
            Assert-SetupLog ($entries.Count -eq 2 -and $logEntry.Count -eq 1 -and
                @($entries | Where-Object { $_.FullName -eq 'setup-logs-manifest.json' }).Count -eq 1) `
                'ZIP includes only the manifest and redacted setup log.'
            $reader = New-Object IO.StreamReader($logEntry[0].Open())
            try {
                Assert-SetupLog ($reader.ReadToEnd() -eq $safe) 'ZIP contains the verified redacted content.'
            }
            finally { $reader.Dispose() }
        }
        finally { $zip.Dispose() }

        $limits = New-Case 'limits'
        $path = Add-Log $limits 'Peggy_20260924110000.log' ((1..8 | ForEach-Object { "line$_" }) -join "`n")
        $tail = Read-PeggySetupLogTail $path -LogTailLines 3
        Assert-SetupLog ($tail.Content -eq ("line6", "line7", "line8" -join [Environment]::NewLine) -and
            $tail.LinesWritten -eq 3 -and $tail.LineCountTruncated -and $tail.Truncated) 'Tail line count.'
        $path = Add-Log $limits 'Peggy_20260924110100.log' (
            'password="' + ('LONG_SECRET ' * 6000) + '"' + "`r`n" + 'PEGGY_SETUP finish.failed')
        $tail = Read-PeggySetupLogTail $path
        Assert-SetupLog ($tail.OversizedLineCount -eq 1 -and $tail.Truncated -and
            -not $tail.Content.Contains('LONG_SECRET') -and
            $tail.Content.Contains('PEGGY_SETUP finish.failed')) 'Oversized lines are omitted, not split.'
        Assert-SetupLog (@($tail.Content -split '\r\n|\n' | Where-Object { $_.Length -gt 65536 }).Count -eq 0) `
            'Every exported line obeys the character bound.'
        $record = 'PEGGY_SETUP {"sessionId":"' + $session +
            '","stage":"settings.save","outcome":"failed","errors":[{"message":"' +
            ('synthetic method frame ' * 1500) + '"}]}'
        $path = Add-Log $limits 'Peggy_20260924110130.log' $record
        $tail = Read-PeggySetupLogTail $path
        Assert-SetupLog ($record.Length -gt 16384 -and $record.Length -lt 65536 -and
            $tail.Content -eq $record -and -not $tail.Truncated -and $tail.OversizedLineCount -eq 0) `
            'Large structured failures preserve the complete record and session correlation.'
        $path = Add-Log $limits 'Peggy_20260924110200.log' (
            'password="' + ('BOUNDARY_SECRET ' * 300) + '"' + "`r`n" + 'PEGGY_SETUP launch.failed')
        $tail = Read-PeggySetupLogTail $path -MaxReadBytes 1024
        Assert-SetupLog ($tail.ReadBytes -le 1024 -and $tail.ByteTailTruncated -and
            $tail.Content -eq 'PEGGY_SETUP launch.failed') 'Byte-limited partial first lines never escape redaction.'
        $path = Add-Log $limits 'Peggy_20260924110300.log' ('NO_NEWLINE_SECRET ' * 300)
        $tail = Read-PeggySetupLogTail $path -MaxReadBytes 1024
        Assert-SetupLog ($tail.Content -eq '' -and $tail.LinesWritten -eq 0 -and
            $tail.ByteTailTruncated) 'A byte tail without a complete line is omitted.'
        $bounded = New-Case 'bounded-export'
        [void](Add-Log $bounded 'Peggy_20260924110400.log' (
            'password="' + ('Z' * (4MB + 5000)) + '"' + "`n" + 'PEGGY_SETUP finish.failed'))
        Export-PeggySetupLogs $bounded.Source $bounded.Destination -NowUtc $now
        $manifest = Read-Manifest $bounded
        Assert-SetupLog ($manifest.MaximumLineCharacters -eq 65536 -and
            $manifest.Files[0].SourceBytes -gt 4MB -and $manifest.Files[0].ReadBytes -le 4MB -and
            $manifest.Files[0].ByteTailTruncated -and
            (Read-Collected $bounded $manifest.Files[0]) -eq 'PEGGY_SETUP finish.failed') `
            'Default export byte budget and truncation metadata.'

        $encodings = New-Case 'encodings'
        foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode, [Text.Encoding]::BigEndianUnicode)) {
            $path = Join-Path $encodings.Source 'Peggy_20260924120000.log'
            $text = 'native ' + [char]0x00e9 + "`r`n" + 'PEGGY_SETUP account.failed'
            [IO.File]::WriteAllText($path, $text, $encoding)
            $tail = Read-PeggySetupLogTail $path
            Assert-SetupLog ($tail.Content -eq $text -and -not $tail.Truncated) 'Burn log BOM/encoding.'
            [IO.File]::WriteAllText($path, ('prefix ' * 500) + "`n" + 'PEGGY_SETUP account.failed', $encoding)
            $tail = Read-PeggySetupLogTail $path -MaxReadBytes 1024
            Assert-SetupLog ($tail.Content -eq 'PEGGY_SETUP account.failed') 'Bounded encoded log tail.'
        }
        $path = Add-Log $encodings 'Peggy_20260924120100.log' ''
        $tail = Read-PeggySetupLogTail $path
        Assert-SetupLog ($tail.LinesWritten -eq 0 -and $tail.Content -eq '' -and
            -not $tail.Truncated) 'Empty log file.'

        $empty = New-Case 'empty'
        Export-PeggySetupLogs $empty.Source $empty.Destination -NowUtc $now
        $manifest = Read-Manifest $empty
        Assert-SetupLog ($manifest.Status -eq 'NoRecentLogs' -and $manifest.Files.Count -eq 0 -and
            $manifest.EligibleLogCount -eq 0 -and $manifest.Source -eq '%TEMP%') 'No-log manifest is explicit.'
        $missing = New-Case 'missing'
        Export-PeggySetupLogs (Join-Path $missing.Source 'does-not-exist') $missing.Destination -NowUtc $now
        $manifest = Read-Manifest $missing
        Assert-SetupLog ($manifest.Status -eq 'Error' -and $manifest.ErrorType -and
            $manifest.ErrorMessage -and $manifest.Files.Count -eq 0) 'Enumeration errors are retained.'

        $locked = New-Case 'locked'
        $path = Add-Log $locked 'Peggy_20260924121000.log' 'LOCKED_CONTENT'
        [void](Add-Log $locked 'Peggy_20260924120900.log' 'PEGGY_SETUP saved' $now.AddSeconds(-1))
        $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Export-PeggySetupLogs $locked.Source $locked.Destination -NowUtc $now
        }
        finally { $lock.Dispose() }
        $manifest = Read-Manifest $locked
        Assert-SetupLog ($manifest.Status -eq 'Partial' -and $manifest.Files.Count -eq 2 -and
            $manifest.CollectedLogCount -eq 1 -and $manifest.Files[0].Status -eq 'Error' -and
            $manifest.Files[0].ErrorType -eq 'System.IO.IOException' -and $manifest.Files[0].ErrorMessage -and
            $null -eq $manifest.Files[0].OutputRelativePath -and $manifest.Files[0].SourceBytes -gt 0 -and
            $manifest.Files[1].Status -eq 'Collected') 'A locked log does not abort other logs or hide failure metadata.'
        Assert-SetupLog ((Read-Collected $locked $manifest.Files[1]) -eq 'PEGGY_SETUP saved') 'Collection continues.'

        $failures = New-Case 'failures'
        [void](Add-Log $failures 'Peggy_20260924122000.log' 'UNREADABLE')
        function Read-PeggySetupLogTail {
            param($Path, $LogTailLines, $MaxReadBytes, $MaxLineCharacters)
            throw [UnauthorizedAccessException]::new(
                'password="ERROR_SECRET with spaces" person@example.test https://example.test/private')
        }
        try {
            Export-PeggySetupLogs $failures.Source $failures.Destination -NowUtc $now
        }
        finally { . ([scriptblock]::Create($functions['Read-PeggySetupLogTail'])) }
        $manifest = Read-Manifest $failures
        $errorText = $manifest.Files[0].ErrorMessage
        Assert-SetupLog ($manifest.Files[0].ErrorType -eq 'System.UnauthorizedAccessException' -and
            $manifest.Files[0].SourceRelativePath -eq 'Peggy_20260924122000.log' -and
            -not $errorText.Contains('ERROR_SECRET') -and -not $errorText.Contains('person@example.test') -and
            -not $errorText.Contains('example.test/private')) 'Access error messages are sanitized with source metadata.'
        [Console]::Out.WriteLine('Synthetic setup-log collector tests passed.')
        """;
}
