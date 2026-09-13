namespace VrcTranslate.Core.Speech;

/// <summary>A half-open sample range inside one captured segment.</summary>
public readonly record struct SpeechSpan(int StartSample, int EndSample)
{
    public int Length => EndSample - StartSample;
}

/// <summary>
/// Speaker separation for captions. Implementations own the local models and the
/// voiceprint library; the capture pipeline only sees this contract, and every
/// member is safe to call while the models are missing.
/// </summary>
public interface ISpeakerIdentifier
{
    /// <summary>False when the speaker models are unavailable, so captions keep the plain path.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<SpeakerIdentity> Speakers { get; }

    /// <summary>
    /// Extracts a voiceprint and matches it, registering a new speaker when nothing
    /// is close enough. Null when the clip was too short to identify, in which case
    /// the caller keeps whatever label the previous sentence used.
    /// </summary>
    SpeakerMatch? Identify(ReadOnlyMemory<float> samples, int sampleRate);

    /// <summary>
    /// Cheap check for two voices inside one segment. Comparing the head and tail
    /// voiceprints is far cheaper than running segmentation over every sentence,
    /// so it is used to decide whether segmentation is worth paying for.
    /// </summary>
    bool IsSpeakerChangeSuspected(ReadOnlyMemory<float> samples, int sampleRate);

    /// <summary>Splits one segment at the speaker changes a segmentation model finds.</summary>
    IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate);

    void Rename(string speakerId, string? name);

    bool Merge(string sourceId, string targetId);

    bool Forget(string speakerId);

    void Clear();
}