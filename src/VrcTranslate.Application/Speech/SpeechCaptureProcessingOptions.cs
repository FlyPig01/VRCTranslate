namespace VrcTranslate.Application.Speech;

/// <summary>
/// What a capture session does with the audio of one segment (D29). Splitting at
/// speaker changes and attaching speaker labels are independent decisions: the
/// other-player session always splits - it is what keeps two people taking turns
/// in one VAD segment as two captions - while labels follow the user's toggle,
/// and the own-voice session does neither.
/// </summary>
public sealed record SpeechCaptureProcessingOptions
{
    /// <summary>Run speaker-change segmentation so alternating voices become separate captions.</summary>
    public bool SplitAtSpeakerChanges { get; init; }

    /// <summary>Identify the voice and attach a speaker label to each result.</summary>
    public bool AttachSpeakerLabels { get; init; }

    /// <summary>
    /// How long an audible stream may produce no VAD segment before the managed
    /// energy gate takes over. Must stay longer than the native max-speech cut.
    /// </summary>
    public TimeSpan SilentVadFallbackWindow { get; init; } = TimeSpan.FromSeconds(16);

    /// <summary>
    /// Absolute ceiling for a gate that keeps reporting "speech detected" but
    /// never completes a segment; protects against a genuinely stuck native gate.
    /// </summary>
    public TimeSpan SilentVadHardFallbackWindow { get; init; } = TimeSpan.FromSeconds(24);

    public static SpeechCaptureProcessingOptions None { get; } = new();
}
