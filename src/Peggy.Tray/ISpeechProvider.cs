using System;
using System.Threading;
using System.Threading.Tasks;

namespace Peggy.Tray;

public interface ISpeechProvider : IDisposable
{
    bool IsSpeaking { get; }

    bool IsListening { get; }

    bool IsLoaded { get; }

    Task WarmUpAsync(CancellationToken ct);

    Task<string?> RecognizeOnceAsync(
        TimeSpan silenceTimeout,
        Action<string>? onPartial,
        CancellationToken ct);

    Task SpeakAsync(string text, CancellationToken ct);

    void CancelSpeaking();

    void CancelListening();

    void Unload();
}
