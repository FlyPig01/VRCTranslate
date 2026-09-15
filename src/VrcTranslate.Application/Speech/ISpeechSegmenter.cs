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

    /// <summary>
    /// Drops buffered audio and restarts the gate with the sensitivity of a
    /// fresh segmenter. A capture source switch calls this so audio recorded
    /// before the boundary never joins a caption with what follows it.
    /// </summary>
    void Reset();
}
