using System.Text;
using System.Text.Json;
using Peggy.Core;

namespace Peggy.Speech.Host;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--transcribe-file", var audioPath])
        {
            using var diagnosticRuntime = new SpeechRuntime(
                SpeechHostOptions.FromEnvironment());
            var transcript = diagnosticRuntime.TranscribeFile(audioPath);
            Console.WriteLine(transcript ?? string.Empty);
            return string.IsNullOrWhiteSpace(transcript) ? 1 : 0;
        }
        if (args is ["--stream-transcribe-file", var streamingAudioPath])
        {
            using var diagnosticRuntime = new SpeechRuntime(
                SpeechHostOptions.FromEnvironment());
            var transcript = diagnosticRuntime.TranscribeFileStreaming(
                streamingAudioPath);
            Console.WriteLine(transcript ?? string.Empty);
            return string.IsNullOrWhiteSpace(transcript) ? 1 : 0;
        }
        if (args is ["--microphone-diagnostic", var secondsText] &&
            double.TryParse(
                secondsText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds))
        {
            var diagnostic = SpeechRuntime.MeasureMicrophone(
                TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)));
            Console.WriteLine(JsonSerializer.Serialize(diagnostic));
            return diagnostic.SampleCount > 0 ? 0 : 1;
        }

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        var testMode = string.Equals(
            Environment.GetEnvironmentVariable("PEGGY_SPEECH_TEST_MODE"),
            "1",
            StringComparison.Ordinal);
        SpeechRuntime? runtime = null;
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                SpeechHostRequest? request = null;
                SpeechHostResponse response;
                try
                {
                    request = JsonSerializer.Deserialize<SpeechHostRequest>(
                        line.TrimStart('\uFEFF'),
                        JsonOptions)
                        ?? throw new InvalidDataException(
                            "The speech request was empty.");
                    response = testMode
                        ? await HandleTestRequestAsync(
                            request,
                            WritePartial)
                        : HandleRequest(
                            request,
                            ref runtime,
                            WritePartial);
                }
                catch (Exception ex)
                {
                    response = new SpeechHostResponse
                    {
                        Id = request?.Id ?? string.Empty,
                        Success = false,
                        Error = $"{ex.GetType().Name}: {ex.Message}"
                    };
                }

                await Console.Out.WriteLineAsync(
                    JsonSerializer.Serialize(response, JsonOptions));
                await Console.Out.FlushAsync();
                if (string.Equals(
                        request?.Command,
                        SpeechHostCommands.Shutdown,
                        StringComparison.Ordinal))
                {
                    break;
                }
            }
            return 0;
        }
        finally
        {
            runtime?.Dispose();
        }
    }

    private static SpeechHostResponse HandleRequest(
        SpeechHostRequest request,
        ref SpeechRuntime? runtime,
        Action<SpeechHostRequest, string> partial)
    {
        ValidateRequest(request);
        if (request.Command == SpeechHostCommands.Ping)
        {
            return Success(request, "ready");
        }
        if (request.Command == SpeechHostCommands.Shutdown)
        {
            return Success(request);
        }

        runtime ??= new SpeechRuntime(SpeechHostOptions.FromEnvironment());
        if (request.Command == SpeechHostCommands.WarmUp)
        {
            runtime.WarmUp();
            return Success(request, "ready");
        }
        if (request.Command == SpeechHostCommands.Speak)
        {
            runtime.Speak(request.Text!);
            return Success(request);
        }
        if (request.Command == SpeechHostCommands.Recognize)
        {
            var text = runtime.RecognizeOnce(
                TimeSpan.FromMilliseconds(
                    Math.Clamp(request.SilenceTimeoutMs, 1000, 60000)),
                text => partial(request, text));
            return Success(request, text);
        }

        throw new InvalidDataException(
            $"Unsupported speech command '{request.Command}'.");
    }

    private static async Task<SpeechHostResponse> HandleTestRequestAsync(
        SpeechHostRequest request,
        Action<SpeechHostRequest, string> partial)
    {
        ValidateRequest(request);
        var delay = ParseIntEnvironment(
            "PEGGY_SPEECH_TEST_DELAY_MS",
            0,
            0,
            30000);
        if (delay > 0 &&
            request.Command is
                SpeechHostCommands.Speak or
                SpeechHostCommands.Recognize)
        {
            await Task.Delay(delay);
        }

        if (request.Command == SpeechHostCommands.Recognize &&
            Environment.GetEnvironmentVariable(
                "PEGGY_SPEECH_TEST_PARTIAL") is { Length: > 0 } partialText)
        {
            partial(request, partialText);
        }

        return request.Command switch
        {
            SpeechHostCommands.Ping => Success(request, "ready"),
            SpeechHostCommands.WarmUp => Success(request, "ready"),
            SpeechHostCommands.Speak => Success(request),
            SpeechHostCommands.Recognize => Success(
                request,
                Environment.GetEnvironmentVariable(
                    "PEGGY_SPEECH_TEST_TRANSCRIPT")),
            SpeechHostCommands.Shutdown => Success(request),
            _ => throw new InvalidDataException(
                $"Unsupported speech command '{request.Command}'.")
        };
    }

    private static void ValidateRequest(SpeechHostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) ||
            string.IsNullOrWhiteSpace(request.Command))
        {
            throw new InvalidDataException(
                "Speech requests require an ID and command.");
        }
        if (request.Command == SpeechHostCommands.Speak &&
            string.IsNullOrWhiteSpace(request.Text))
        {
            throw new InvalidDataException(
                "Speech synthesis requires text.");
        }
    }

    private static SpeechHostResponse Success(
        SpeechHostRequest request,
        string? text = null)
        => new()
        {
            Id = request.Id,
            Success = true,
            Text = text
        };

    private static void WritePartial(
        SpeechHostRequest request,
        string text)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(
            new SpeechHostResponse
            {
                Id = request.Id,
                Success = true,
                IsPartial = true,
                Text = text
            },
            JsonOptions));
        Console.Out.Flush();
    }

    private static int ParseIntEnvironment(
        string name,
        int fallback,
        int minimum,
        int maximum)
        => int.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}
