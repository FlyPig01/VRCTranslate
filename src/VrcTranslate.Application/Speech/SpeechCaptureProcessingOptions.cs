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

    public static SpeechCaptureProcessingOptions None { get; } = new();
}
