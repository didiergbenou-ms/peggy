using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Peggy.Core;
using SherpaOnnx;

namespace Peggy.Speech.Host;

internal sealed record SpeechHostOptions(
    string ModelRoot,
    int NumThreads,
    int VoiceId,
    float VoiceSpeed,
    float InputGain)
{
    public static SpeechHostOptions FromEnvironment()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(
            "PEGGY_SPEECH_MODEL_ROOT");
        var modelRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? PeggyDefaults.SpeechModelDirectory
            : Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    configuredRoot.Trim().Trim('"')));
        return new SpeechHostOptions(
            modelRoot,
            ParseInt("PEGGY_SPEECH_THREADS", 8, 1, 8),
            ParseInt("PEGGY_SPEECH_VOICE_ID", 1, 0, 10),
            ParseFloat("PEGGY_SPEECH_SPEED", 1.15F, 0.75F, 1.5F),
            ParseFloat("PEGGY_SPEECH_INPUT_GAIN", 12.0F, 1.0F, 20.0F));
    }

    private static int ParseInt(
        string name,
        int fallback,
        int minimum,
        int maximum)
        => int.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static float ParseFloat(
        string name,
        float fallback,
        float minimum,
        float maximum)
        => float.TryParse(
            Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}

internal sealed class SpeechRuntime : IDisposable
{
    private const int SampleRate = 16000;
    private const int VadWindowSize = 512;

    private readonly SpeechHostOptions _options;
    private readonly SpeechModelPaths _models;
    private OfflineRecognizer? _recognizer;
    private OnlineRecognizer? _streamingRecognizer;
    private VoiceActivityDetector? _vad;
    private OfflineTts? _tts;
    private bool _disposed;

    public SpeechRuntime(SpeechHostOptions options)
    {
        _options = options;
        _models = SpeechModelPaths.Resolve(options.ModelRoot);
    }

    public void WarmUp()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = GetStreamingRecognizer();
        _ = GetVad();
        var tts = GetTts();
        if (_options.VoiceId >= tts.NumSpeakers)
        {
            throw new InvalidDataException(
                $"Kitten exposes {tts.NumSpeakers} voices, but voice " +
                $"{_options.VoiceId} was configured.");
        }
    }

    public string? RecognizeOnce(
        TimeSpan initialSilenceTimeout,
        Action<string>? onPartial = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var recognizerLoad = Task.Run(GetRecognizer);
        var streamingRecognizer = GetStreamingRecognizer();
        var vad = GetVad();
        vad.Reset();
        using var streaming = streamingRecognizer.CreateStream();

        using var capture = new WasapiCapture();
        var inputBuffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        ISampleProvider samples = inputBuffer.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new MonoSampleProvider(samples);
        }
        samples = new GainSampleProvider(samples, _options.InputGain);
        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(samples, SampleRate);
        }

        using var dataReady = new AutoResetEvent(false);
        using var recordingStopped = new ManualResetEvent(false);
        Exception? recordingError = null;
        long capturedFrames = 0;
        EventHandler<WaveInEventArgs> dataAvailable = (_, args) =>
        {
            inputBuffer.AddSamples(
                args.Buffer,
                0,
                args.BytesRecorded);
            Interlocked.Add(
                ref capturedFrames,
                args.BytesRecorded / capture.WaveFormat.BlockAlign);
            dataReady.Set();
        };
        EventHandler<StoppedEventArgs> stopped = (_, args) =>
        {
            recordingError = args.Exception;
            dataReady.Set();
            recordingStopped.Set();
        };
        capture.DataAvailable += dataAvailable;
        capture.RecordingStopped += stopped;

        var silenceStopwatch = Stopwatch.StartNew();
        var overallStopwatch = Stopwatch.StartNew();
        var speechDetected = false;
        var pending = new List<float>(8192);
        var capturedAudio = new List<float>(SampleRate * 10);
        var pendingOffset = 0;
        var readBuffer = new float[2048];
        var outputSampleBudget = 0.0D;
        var lastPartial = string.Empty;
        var partialStability = new Stopwatch();
        var acousticSilence = Stopwatch.StartNew();
        var noiseFloor = 0.002F;
        var acousticSpeechDetected = false;
        capture.StartRecording();
        try
        {
            while (true)
            {
                dataReady.WaitOne(50);
                if (recordingError is not null)
                {
                    throw new InvalidOperationException(
                        "The default microphone stopped unexpectedly.",
                        recordingError);
                }

                var newFrames = Interlocked.Exchange(
                    ref capturedFrames,
                    0);
                outputSampleBudget +=
                    newFrames *
                    (SampleRate /
                     (double)capture.WaveFormat.SampleRate);
                var samplesToRead = Math.Min(
                    (int)outputSampleBudget,
                    SampleRate * 5);
                while (samplesToRead > 0)
                {
                    var read = samples.Read(
                        readBuffer,
                        0,
                        Math.Min(readBuffer.Length, samplesToRead));
                    if (read <= 0)
                    {
                        break;
                    }
                    pending.AddRange(
                        readBuffer.AsSpan(0, read).ToArray());
                    outputSampleBudget -= read;
                    samplesToRead -= read;
                }

                while (pending.Count - pendingOffset >= VadWindowSize)
                {
                    var window = new float[VadWindowSize];
                    pending.CopyTo(
                        pendingOffset,
                        window,
                        0,
                        VadWindowSize);
                    pendingOffset += VadWindowSize;
                    capturedAudio.AddRange(window);
                    var maximumCapturedSamples =
                        lastPartial.Length == 0
                            ? SampleRate * 3
                            : SampleRate * 45;
                    if (capturedAudio.Count > maximumCapturedSamples)
                    {
                        capturedAudio.RemoveRange(
                            0,
                            VadWindowSize);
                    }
                    var windowRms = CalculateRms(window);
                    var energyThreshold = Math.Max(
                        0.006F,
                        noiseFloor * 3.0F);
                    if (windowRms >= energyThreshold)
                    {
                        acousticSpeechDetected = true;
                        acousticSilence.Restart();
                    }
                    else if (!acousticSpeechDetected)
                    {
                        noiseFloor =
                            (noiseFloor * 0.95F) +
                            (windowRms * 0.05F);
                    }
                    vad.AcceptWaveform(window);
                    streaming.AcceptWaveform(SampleRate, window);
                    while (streamingRecognizer.IsReady(streaming))
                    {
                        streamingRecognizer.Decode(streaming);
                    }
                    var partial = streamingRecognizer
                        .GetResult(streaming)
                        .Text
                        .Trim();
                    if (!string.IsNullOrWhiteSpace(partial) &&
                        !string.Equals(
                            partial,
                            lastPartial,
                            StringComparison.Ordinal))
                    {
                        lastPartial = partial;
                        partialStability.Restart();
                        onPartial?.Invoke(partial);
                    }
                    var vadDetected = vad.IsSpeechDetected();
                    speechDetected |= vadDetected;
                    if (vadDetected)
                    {
                        acousticSpeechDetected = true;
                    }
                    while (!vad.IsEmpty())
                    {
                        var segment = vad.Front();
                        vad.Pop();
                        if (segment.Samples.Length > 0)
                        {
                            var text = Decode(
                                recognizerLoad.GetAwaiter().GetResult(),
                                segment.Samples);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                            speechDetected = false;
                            silenceStopwatch.Restart();
                        }
                    }
                }

                if (lastPartial.Length > 0 &&
                    acousticSpeechDetected &&
                    partialStability.Elapsed >=
                        TimeSpan.FromMilliseconds(700) &&
                    acousticSilence.Elapsed >=
                        TimeSpan.FromMilliseconds(450))
                {
                    var final = Decode(
                        recognizerLoad.GetAwaiter().GetResult(),
                        capturedAudio.ToArray());
                    return string.IsNullOrWhiteSpace(final)
                        ? lastPartial
                        : final;
                }

                if (pendingOffset >= 8192)
                {
                    pending.RemoveRange(0, pendingOffset);
                    pendingOffset = 0;
                }
                if (!speechDetected &&
                    silenceStopwatch.Elapsed >= initialSilenceTimeout)
                {
                    return null;
                }
                if (overallStopwatch.Elapsed >=
                    initialSilenceTimeout + TimeSpan.FromSeconds(30))
                {
                    return null;
                }
            }

        }
        finally
        {
            capture.StopRecording();
            if (!recordingStopped.WaitOne(3000))
            {
                throw new TimeoutException(
                    "The default microphone did not stop cleanly.");
            }
            capture.DataAvailable -= dataAvailable;
            capture.RecordingStopped -= stopped;
            _ = recognizerLoad.GetAwaiter().GetResult();
        }
    }

    private static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sum = 0.0D;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    public string? TranscribeFile(string audioPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var input = ReadAudioFile(audioPath);
        if (input.Length == 0)
        {
            return null;
        }

        var vad = GetVad();
        vad.Reset();
        for (var offset = 0; offset < input.Length; offset += VadWindowSize)
        {
            var window = new float[VadWindowSize];
            Array.Copy(
                input,
                offset,
                window,
                0,
                Math.Min(VadWindowSize, input.Length - offset));
            vad.AcceptWaveform(window);
        }
        vad.Flush();
        var speech = new List<float>();
        while (!vad.IsEmpty())
        {
            speech.AddRange(vad.Front().Samples);
            vad.Pop();
        }
        return speech.Count == 0
            ? null
            : Decode(GetRecognizer(), speech.ToArray());
    }

    public string? TranscribeFileStreaming(string audioPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var input = ReadAudioFile(audioPath);
        if (input.Length == 0)
        {
            return null;
        }

        var recognizer = GetStreamingRecognizer();
        using var stream = recognizer.CreateStream();
        for (var offset = 0; offset < input.Length; offset += VadWindowSize)
        {
            var count = Math.Min(
                VadWindowSize,
                input.Length - offset);
            var window = new float[count];
            Array.Copy(input, offset, window, 0, count);
            stream.AcceptWaveform(SampleRate, window);
            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
            }
        }
        stream.AcceptWaveform(
            SampleRate,
            new float[(int)(0.66F * SampleRate)]);
        stream.InputFinished();
        while (recognizer.IsReady(stream))
        {
            recognizer.Decode(stream);
        }
        var text = recognizer.GetResult(stream).Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static float[] ReadAudioFile(string audioPath)
    {
        var fullPath = Path.GetFullPath(audioPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The speech diagnostic audio file does not exist.",
                fullPath);
        }

        using var reader = new AudioFileReader(fullPath);
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new MonoSampleProvider(samples);
        }
        if (samples.WaveFormat.SampleRate != SampleRate)
        {
            samples = new WdlResamplingSampleProvider(
                samples,
                SampleRate);
        }

        var allSamples = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            allSamples.AddRange(
                buffer.AsSpan(0, read).ToArray());
            if (allSamples.Count > SampleRate * 300)
            {
                throw new InvalidDataException(
                    "Speech diagnostic audio is longer than five minutes.");
            }
        }
        return allSamples.ToArray();
    }

    public static MicrophoneDiagnostic MeasureMicrophone(TimeSpan duration)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(
            DataFlow.Capture,
            Role.Console);
        var endpointVolume =
            device.AudioEndpointVolume.MasterVolumeLevelScalar;
        var endpointMuted = device.AudioEndpointVolume.Mute;
        using var capture = new WasapiCapture();
        var inputBuffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        ISampleProvider samples = inputBuffer.ToSampleProvider();
        var channels = samples.WaveFormat.Channels;

        using var dataReady = new AutoResetEvent(false);
        using var recordingStopped = new ManualResetEvent(false);
        Exception? recordingError = null;
        EventHandler<WaveInEventArgs> dataAvailable = (_, args) =>
        {
            inputBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            dataReady.Set();
        };
        EventHandler<StoppedEventArgs> stopped = (_, args) =>
        {
            recordingError = args.Exception;
            dataReady.Set();
            recordingStopped.Set();
        };
        capture.DataAvailable += dataAvailable;
        capture.RecordingStopped += stopped;

        var sampleCount = 0L;
        var peak = 0.0F;
        var sumSquares = 0.0D;
        var channelPeaks = new float[channels];
        var buffer = new float[2048];
        var stopwatch = Stopwatch.StartNew();
        capture.StartRecording();
        try
        {
            while (stopwatch.Elapsed < duration)
            {
                dataReady.WaitOne(50);
                if (recordingError is not null)
                {
                    throw new InvalidOperationException(
                        "The default microphone stopped unexpectedly.",
                        recordingError);
                }

                int read;
                while ((read = samples.Read(
                           buffer,
                           0,
                           buffer.Length)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        var value = buffer[index];
                        peak = Math.Max(peak, Math.Abs(value));
                        var channel = (int)(sampleCount + index) % channels;
                        channelPeaks[channel] = Math.Max(
                            channelPeaks[channel],
                            Math.Abs(value));
                        sumSquares += value * value;
                    }
                    sampleCount += read;
                }
            }
        }
        finally
        {
            capture.StopRecording();
            if (!recordingStopped.WaitOne(3000))
            {
                throw new TimeoutException(
                    "The default microphone did not stop cleanly.");
            }
            capture.DataAvailable -= dataAvailable;
            capture.RecordingStopped -= stopped;
        }

        return new MicrophoneDiagnostic(
            capture.WaveFormat.ToString(),
            sampleCount / channels,
            peak,
            sampleCount == 0
                ? 0
                : Math.Sqrt(sumSquares / sampleCount),
            channelPeaks,
            endpointVolume,
            endpointMuted);
    }

    public void Speak(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var tts = GetTts();
        if (_options.VoiceId >= tts.NumSpeakers)
        {
            throw new InvalidDataException(
                $"Kitten exposes {tts.NumSpeakers} voices, but voice " +
                $"{_options.VoiceId} was configured.");
        }

        var waveProvider = new BufferedWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(tts.SampleRate, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        using var output = new WasapiOut(
            AudioClientShareMode.Shared,
            useEventSync: false,
            latency: 80);
        Exception? playbackError = null;
        output.PlaybackStopped += (_, args) =>
            playbackError = args.Exception;
        output.Init(waveProvider);

        var playbackStarted = false;
        var callback = new OfflineTtsCallbackProgressWithArg(
            (samplePointer, sampleCount, _, _) =>
            {
                if (sampleCount <= 0)
                {
                    return 1;
                }

                var chunk = new float[sampleCount];
                Marshal.Copy(
                    samplePointer,
                    chunk,
                    0,
                    sampleCount);
                var bytes = new byte[sampleCount * sizeof(float)];
                Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
                waveProvider.AddSamples(bytes, 0, bytes.Length);
                if (!playbackStarted)
                {
                    output.Play();
                    playbackStarted = true;
                }
                return 1;
            });
        var generationConfig = new OfflineTtsGenerationConfig
        {
            Sid = _options.VoiceId,
            Speed = _options.VoiceSpeed,
            SilenceScale = 0.2F
        };
        var generated = tts.GenerateWithConfig(
            text,
            generationConfig,
            callback);
        try
        {
            if (!playbackStarted)
            {
                var samples = generated.Samples;
                if (samples.Length == 0)
                {
                    throw new InvalidDataException(
                        "Kitten generated no audio.");
                }

                var bytes = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                waveProvider.AddSamples(bytes, 0, bytes.Length);
                output.Play();
                playbackStarted = true;
            }

            while (waveProvider.BufferedBytes > 0)
            {
                if (playbackError is not null)
                {
                    throw new InvalidOperationException(
                        "Local speech playback failed.",
                        playbackError);
                }
                Thread.Sleep(10);
            }
            Thread.Sleep(100);
            output.Stop();
            Thread.Sleep(300);
            if (playbackError is not null)
            {
                throw new InvalidOperationException(
                    "Local speech playback failed.",
                    playbackError);
            }
        }
        finally
        {
            generated.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tts?.Dispose();
        _tts = null;
        _streamingRecognizer?.Dispose();
        _streamingRecognizer = null;
        _vad?.Dispose();
        _vad = null;
        _recognizer?.Dispose();
        _recognizer = null;
    }

    private OfflineRecognizer GetRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = _models.AsrEncoder;
        config.ModelConfig.Transducer.Decoder = _models.AsrDecoder;
        config.ModelConfig.Transducer.Joiner = _models.AsrJoiner;
        config.ModelConfig.Tokens = _models.AsrTokens;
        config.ModelConfig.NumThreads = _options.NumThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
        return _recognizer;
    }

    private VoiceActivityDetector GetVad()
    {
        if (_vad is not null)
        {
            return _vad;
        }

        var config = new VadModelConfig();
        config.SileroVad.Model = _models.VadModel;
        config.SileroVad.Threshold = 0.25F;
        config.SileroVad.MinSilenceDuration = 0.45F;
        config.SileroVad.MinSpeechDuration = 0.2F;
        config.SileroVad.MaxSpeechDuration = 30.0F;
        config.SileroVad.WindowSize = VadWindowSize;
        config.SampleRate = SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;
        _vad = new VoiceActivityDetector(config, 60);
        return _vad;
    }

    private OnlineRecognizer GetStreamingRecognizer()
    {
        if (_streamingRecognizer is not null)
        {
            return _streamingRecognizer;
        }

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NemoCtc.Model =
            _models.StreamingModel;
        config.ModelConfig.Tokens =
            _models.StreamingTokens;
        config.ModelConfig.NumThreads =
            Math.Min(_options.NumThreads, 2);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 0;
        _streamingRecognizer = new OnlineRecognizer(config);
        return _streamingRecognizer;
    }

    private OfflineTts GetTts()
    {
        if (_tts is not null)
        {
            return _tts;
        }

        var config = new OfflineTtsConfig();
        config.Model.Kitten.Model = _models.TtsModel;
        config.Model.Kitten.Voices = _models.TtsVoices;
        config.Model.Kitten.Tokens = _models.TtsTokens;
        config.Model.Kitten.DataDir = _models.TtsDataDirectory;
        config.Model.NumThreads = _options.NumThreads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        config.MaxNumSentences = 1;
        config.SilenceScale = 0.2F;
        _tts = new OfflineTts(config);
        return _tts;
    }

    private static string? Decode(
        OfflineRecognizer recognizer,
        float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, samples);
        recognizer.Decode(stream);
        var text = stream.Result.Text?.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text;
    }

    private sealed class MonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public MonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            var required = checked(count * channels);
            if (_sourceBuffer.Length < required)
            {
                _sourceBuffer = new float[required];
            }

            var sourceRead = _source.Read(
                _sourceBuffer,
                0,
                required);
            var frames = sourceRead / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0.0F;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += _sourceBuffer[(frame * channels) + channel];
                }
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }

    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _gain;

        public GainSampleProvider(ISampleProvider source, float gain)
        {
            _source = source;
            _gain = gain;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            for (var index = offset; index < offset + read; index++)
            {
                buffer[index] = Math.Clamp(
                    buffer[index] * _gain,
                    -1.0F,
                    1.0F);
            }
            return read;
        }
    }
}

internal sealed record MicrophoneDiagnostic(
    string CaptureFormat,
    long SampleCount,
    float Peak,
    double Rms,
    IReadOnlyList<float> ChannelPeaks,
    float EndpointVolume,
    bool EndpointMuted);

internal sealed record SpeechModelPaths(
    string AsrEncoder,
    string AsrDecoder,
    string AsrJoiner,
    string AsrTokens,
    string StreamingModel,
    string StreamingTokens,
    string VadModel,
    string TtsModel,
    string TtsVoices,
    string TtsTokens,
    string TtsDataDirectory)
{
    public static SpeechModelPaths Resolve(string root)
    {
        var asr = Path.Combine(
            root,
            "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");
        var tts = Path.Combine(root, "kitten-nano-en-v0_8-int8");
        var streaming = Path.Combine(
            root,
            "sherpa-onnx-nemo-streaming-fast-conformer-ctc-en-" +
            "80ms-int8");
        return new SpeechModelPaths(
            RequireFile(asr, "encoder.int8.onnx"),
            RequireFile(asr, "decoder.int8.onnx"),
            RequireFile(asr, "joiner.int8.onnx"),
            RequireFile(asr, "tokens.txt"),
            RequireFile(streaming, "model.int8.onnx"),
            RequireFile(streaming, "tokens.txt"),
            RequireFile(root, "silero_vad_v5.onnx"),
            RequireOneFile(tts, "model.int8.onnx", "model.onnx"),
            RequireFile(tts, "voices.bin"),
            RequireFile(tts, "tokens.txt"),
            RequireDirectory(tts, "espeak-ng-data"));
    }

    private static string RequireFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path)
            ? path
            : throw Missing(path);
    }

    private static string RequireOneFile(
        string directory,
        params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw Missing(Path.Combine(directory, fileNames[0]));
    }

    private static string RequireDirectory(
        string directory,
        string name)
    {
        var path = Path.Combine(directory, name);
        return Directory.Exists(path)
            ? path
            : throw Missing(path);
    }

    private static FileNotFoundException Missing(string path)
        => new(
            "The assistant's local English speech models are not installed or are " +
            $"incomplete. Missing: {path}",
            path);
}
