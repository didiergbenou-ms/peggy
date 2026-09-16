using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Peggy.Core;

namespace Peggy.Tray;

public sealed class LocalSpeechService : ISpeechProvider
{
    private readonly PeggySettings _settings;
    private readonly string _hostPath;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _processLock = new();
    private readonly object _operationLock = new();
    private Process? _process;
    private Task<string>? _stderr;
    private CancellationTokenSource? _activeOperation;
    private SpeechOperation _operation;
    private bool _disposed;

    public LocalSpeechService(PeggySettings? settings = null)
    {
        _settings = settings ?? new PeggySettings();
        _hostPath = ResolveSpeechHost();
    }

    public bool IsSpeaking
        => GetOperation() == SpeechOperation.Speaking;

    public bool IsListening
        => GetOperation() == SpeechOperation.Listening;

    public bool IsLoaded
    {
        get
        {
            lock (_processLock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public async Task WarmUpAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendAsync(
            new SpeechHostRequest
            {
                Command = SpeechHostCommands.WarmUp
            },
            SpeechOperation.WarmingUp,
            ct).ConfigureAwait(false);
    }

    public async Task<string?> RecognizeOnceAsync(
        TimeSpan silenceTimeout,
        Action<string>? onPartial,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (silenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
        }

        var response = await SendAsync(
            new SpeechHostRequest
            {
                Command = SpeechHostCommands.Recognize,
                SilenceTimeoutMs = checked((int)Math.Clamp(
                    silenceTimeout.TotalMilliseconds,
                    1000,
                    60000))
            },
            SpeechOperation.Listening,
            ct,
            onPartial).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response.Text)
            ? null
            : response.Text.Trim();
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await SendAsync(
                new SpeechHostRequest
                {
                    Command = SpeechHostCommands.Speak,
                    Text = text
                },
                SpeechOperation.Speaking,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // An explicit barge-in stops playback without ending the turn.
        }
    }

    public void CancelSpeaking()
        => CancelOperation(SpeechOperation.Speaking);

    public void CancelListening()
        => CancelOperation(SpeechOperation.Listening);

    public void Unload()
    {
        CancelOperation(SpeechOperation.None);
        TerminateHost();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();
    }

    private async Task<SpeechHostResponse> SendAsync(
        SpeechHostRequest request,
        SpeechOperation operation,
        CancellationToken ct,
        Action<string>? onPartial = null)
    {
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        using var operationCts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_operationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperation = operationCts;
            _operation = operation;
        }

        try
        {
            var process = EnsureHost();
            using var registration = operationCts.Token.Register(
                TerminateHost);
            operationCts.Token.ThrowIfCancellationRequested();

            var payload = JsonSerializer.Serialize(request);
            await process.StandardInput.WriteLineAsync(
                payload.AsMemory(),
                operationCts.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(
                operationCts.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(
                    operationCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException(
                        await GetHostFailureAsync().ConfigureAwait(false));
                }

                var response =
                    JsonSerializer.Deserialize<SpeechHostResponse>(
                        line)
                    ?? throw new InvalidDataException(
                        "The local speech host returned an empty response.");
                if (!string.Equals(
                        response.Id,
                        request.Id,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The local speech host response did not match its " +
                        $"request (expected '{request.Id}', received " +
                        $"'{response.Id}').");
                }
                if (!response.Success)
                {
                    throw new InvalidOperationException(
                        response.Error ??
                        "The local speech host reported an unknown failure.");
                }
                if (response.IsPartial)
                {
                    if (!string.IsNullOrWhiteSpace(response.Text))
                    {
                        onPartial?.Invoke(response.Text.Trim());
                    }
                    continue;
                }
                return response;
            }
        }
        finally
        {
            lock (_operationLock)
            {
                if (ReferenceEquals(_activeOperation, operationCts))
                {
                    _activeOperation = null;
                    _operation = SpeechOperation.None;
                }
            }
            _commandGate.Release();
        }
    }

    private Process EnsureHost()
    {
        lock (_processLock)
        {
            if (_process is { HasExited: false })
            {
                return _process;
            }

            _process?.Dispose();
            var startInfo = new ProcessStartInfo
            {
                FileName = _hostPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            startInfo.Environment["PEGGY_SPEECH_MODEL_ROOT"] =
                ResolveModelRoot();
            startInfo.Environment["PEGGY_SPEECH_THREADS"] =
                Math.Clamp(_settings.SpeechThreads, 1, 8)
                    .ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["PEGGY_SPEECH_VOICE_ID"] =
                Math.Clamp(_settings.SpeechVoiceId, 0, 10)
                    .ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["PEGGY_SPEECH_SPEED"] =
                Math.Clamp(_settings.SpeechSpeed, 0.75F, 1.5F)
                    .ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["PEGGY_SPEECH_INPUT_GAIN"] =
                Math.Clamp(_settings.SpeechInputGain, 1.0F, 20.0F)
                    .ToString(CultureInfo.InvariantCulture);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The local speech host could not be started.");
            _stderr = _process.StandardError.ReadToEndAsync();
            return _process;
        }
    }

    private void CancelOperation(SpeechOperation expected)
    {
        lock (_operationLock)
        {
            if (expected == SpeechOperation.None ||
                _operation == expected)
            {
                _activeOperation?.Cancel();
            }
        }
    }

    private SpeechOperation GetOperation()
    {
        lock (_operationLock)
        {
            return _operation;
        }
    }

    private void TerminateHost()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
            _stderr = null;
        }
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<string> GetHostFailureAsync()
    {
        var stderr = _stderr;
        var detail = stderr is not null && stderr.IsCompleted
            ? (await stderr.ConfigureAwait(false)).Trim()
            : string.Empty;
        return detail.Length == 0
            ? "The local speech host exited without a response."
            : $"The local speech host exited: {detail}";
    }

    private string ResolveModelRoot()
        => PeggyDefaults.ResolveSpeechModelDirectory(
            _settings.SpeechModelDirectory);

    private static string ResolveSpeechHost()
    {
        var configured = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configured.Trim().Trim('"')));
            return File.Exists(explicitPath)
                ? explicitPath
                : throw new FileNotFoundException(
                    "PEGGY_SPEECH_HOST_PATH does not exist.",
                    explicitPath);
        }

        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar);
        var packaged = Path.Combine(
            baseDirectory,
            "Peggy.Speech.Host",
            "peggy-speech-host.exe");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        var configuration = baseDirectory.Contains(
            @"\Release\",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0;
             depth < 7 && directory is not null;
             depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Peggy.Speech.Host",
                "bin",
                configuration,
                "net10.0-windows",
                "peggy-speech-host.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The assistant's isolated local speech host is missing. Rebuild or " +
            "reinstall the application.");
    }

    private enum SpeechOperation
    {
        None,
        WarmingUp,
        Speaking,
        Listening
    }
}
