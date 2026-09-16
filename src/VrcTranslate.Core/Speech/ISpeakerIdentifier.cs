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
    /// Splits one segment at the speaker changes a segmentation model finds.
    /// <para>
    /// There is deliberately no cheap "is a change likely" pre-check in front of
    /// this: the head/tail voiceprint comparison that used to gate it disagreed with
    /// the segmentation model on 11 of 29 real dialogue segments (38% of the real
    /// speaker changes were skipped) while saving nothing measurable - on the
    /// segments it rejected, segmentation itself returned in 33~233 ms. Measured
    /// 2026-09-16 on a two-speaker podcast; see docs/分析-声纹功能是否保留.md.
    /// </para>
    /// </summary>
    IReadOnlyList<SpeechSpan> SplitAtSpeakerChanges(ReadOnlyMemory<float> samples, int sampleRate);

    void Rename(string speakerId, string? name);

    bool Merge(string sourceId, string targetId);

    bool Forget(string speakerId);

    void Clear();
}