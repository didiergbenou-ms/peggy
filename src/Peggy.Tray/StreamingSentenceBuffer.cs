using System;
using System.Text;

namespace Peggy.Tray;

public sealed class StreamingSentenceBuffer
{
    private const int MaximumPendingCharacters = 180;
    private readonly Action<string> _emit;
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _received = new();
    private readonly object _sync = new();

    public StreamingSentenceBuffer(Action<string> emit)
    {
        _emit = emit;
    }

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        lock (_sync)
        {
            _received.Append(delta);
            _pending.Append(delta);
            DrainCompleteSentences();
        }
    }

    public void Complete(string finalText)
    {
        lock (_sync)
        {
            if (_received.Length == 0)
            {
                _pending.Append(finalText);
            }
            else
            {
                var received = _received.ToString();
                if (finalText.StartsWith(
                        received,
                        StringComparison.Ordinal))
                {
                    _pending.Append(finalText[received.Length..]);
                }
            }

            DrainCompleteSentences();
            EmitPending();
        }
    }

    private void DrainCompleteSentences()
    {
        while (_pending.Length > 0)
        {
            var boundary = FindSentenceBoundary(_pending);
            if (boundary < 0 &&
                _pending.Length > MaximumPendingCharacters)
            {
                boundary = FindSafeWordBoundary(_pending);
            }
            if (boundary < 0)
            {
                return;
            }

            EmitPrefix(boundary + 1);
        }
    }

    private void EmitPending()
    {
        if (_pending.Length > 0)
        {
            EmitPrefix(_pending.Length);
        }
    }

    private void EmitPrefix(int length)
    {
        var sentence = _pending.ToString(0, length).Trim();
        _pending.Remove(0, length);
        if (sentence.Length > 0)
        {
            _emit(sentence);
        }
    }

    private static int FindSentenceBoundary(StringBuilder value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\n' or '\r')
            {
                return index;
            }
            if (character is not ('.' or '!' or '?'))
            {
                continue;
            }
            if (index + 1 >= value.Length ||
                char.IsWhiteSpace(value[index + 1]))
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindSafeWordBoundary(StringBuilder value)
    {
        for (var index = MaximumPendingCharacters - 1;
             index >= MaximumPendingCharacters / 2;
             index--)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }
        return MaximumPendingCharacters - 1;
    }
}
