namespace VrcTranslate.Application.Speech;

/// <summary>
/// Turns captured audio into bounded speech segments. Implemented by the
/// adaptive energy gate and by the Silero VAD gate; capture sessions stay
/// independent of which one is active.
/// </summary>
public interface ISpeechSegmenter
{
    int SampleRate { get; }

    /// <summary>Feeds one capture buffer; returns segments that just closed.</summary>
    IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk);

    /// <summary>Closes any pending tail audio; returns zero or more final segments.</summary>
    IReadOnlyList<ReadOnlyMemory<float>> Flush();
}
