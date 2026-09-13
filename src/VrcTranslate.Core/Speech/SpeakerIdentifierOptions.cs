namespace VrcTranslate.Core.Speech;

/// <summary>
/// Policy for splitting one captured segment between speakers. The defaults are
/// conservative: segmentation only runs when the cheap head/tail comparison
/// already suspects two voices, and a noisy detection may not shred a sentence
/// into pieces too short to recognize.
/// </summary>
public sealed record SpeakerIdentifierOptions
{
    /// <summary>
    /// Window taken from each end for the cheap comparison. Measured on the
    /// bundled CAM++ model: at 1 s the same speaker scores 0.45 against themselves,
    /// which straddles any usable threshold, while at 2 s the same speaker scores
    /// 0.69 and a different speaker 0.09. Shorter windows simply are not
    /// discriminative, so 2 s is the floor rather than a tuning preference.
    /// </summary>
    public float ChangeWindowSeconds { get; init; } = 2.0f;

    /// <summary>Head/tail similarity below this suggests two speakers in one segment.</summary>
    public float ChangeSimilarityThreshold { get; init; } = 0.45f;

    /// <summary>
    /// RMS a comparison window must reach to count as speech. Segments carry a
    /// silence tail by construction, and the embedding of silence resembles no
    /// voice at all, so unvoiced windows are skipped instead of compared. The
    /// default matches the capture segmenter's gate.
    /// </summary>
    public float SilenceRmsFloor { get; init; } = 0.012f;

    /// <summary>
    /// Segments shorter than this are never split: two non-overlapping comparison
    /// windows have to fit, plus room for the change itself.
    /// </summary>
    public float MinimumSplitSeconds { get; init; } = 4.5f;

    /// <summary>Shortest piece a split may produce; a fragment shorter than this is not worth recognizing on its own.</summary>
    public float MinimumPartSeconds { get; init; } = 1.0f;

    /// <summary>Most pieces one segment may be split into.</summary>
    public int MaxParts { get; init; } = 3;
}