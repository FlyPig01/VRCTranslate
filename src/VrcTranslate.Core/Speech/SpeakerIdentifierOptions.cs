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
    /// Segments shorter than this are never split. The floor comes from the
    /// segmentation model, not from any comparison window: measured on a real
    /// two-speaker podcast (2026-09-16), 2.5 s slices yield change points in 6/24
    /// cases and 3.0 s slices in 8/20, so 3 s is the shortest slice that still
    /// carries usable evidence. Lowering it further only asks the model questions
    /// it cannot answer.
    /// </summary>
    public float MinimumSplitSeconds { get; init; } = 3.0f;

    /// <summary>Shortest piece a split may produce; a fragment shorter than this is not worth recognizing on its own.</summary>
    public float MinimumPartSeconds { get; init; } = 1.0f;

    /// <summary>Most pieces one segment may be split into.</summary>
    public int MaxParts { get; init; } = 3;
}