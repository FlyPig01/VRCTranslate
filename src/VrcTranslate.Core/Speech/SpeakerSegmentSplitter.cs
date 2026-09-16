namespace VrcTranslate.Core.Speech;

/// <summary>Turns detected speaker-change points into the ranges to recognize separately.</summary>
public static class SpeakerSegmentSplitter
{
    /// <summary>
    /// Turns change points into the ranges to recognize separately. A point closer
    /// than <paramref name="minPartSamples"/> to an edge is <em>pulled in</em> to the
    /// nearest position that keeps every piece recognizable instead of being dropped:
    /// on measured dialogue 2 of 36 long segments reported a change the model then
    /// threw away purely because it sat near an edge, which threw away the split too.
    /// Points closer together than the minimum are merged, and the result never
    /// exceeds <paramref name="maxParts"/> pieces, so a noisy detection still cannot
    /// shred a sentence into fragments.
    /// </summary>
    public static IReadOnlyList<SpeechSpan> Split(
        int totalSamples,
        IReadOnlyList<int> changePoints,
        int minPartSamples,
        int maxParts)
    {
        if (totalSamples <= 0 || maxParts <= 1) return [];
        ArgumentNullException.ThrowIfNull(changePoints);
        if (changePoints.Count == 0) return [];

        // When even two minimum pieces do not fit, the only meaningful boundary is
        // the middle; clamping below then guarantees a non-empty result.
        var lower = Math.Min(minPartSamples, totalSamples / 2);
        var upper = Math.Max(lower, totalSamples - minPartSamples);

        var accepted = new List<int>();
        foreach (var point in changePoints.Order())
        {
            var clamped = Math.Clamp(point, lower, upper);
            if (accepted.Count > 0 && clamped - accepted[^1] < minPartSamples) continue;
            accepted.Add(clamped);
            if (accepted.Count >= maxParts - 1) break;
        }

        if (accepted.Count == 0) return [];

        var spans = new List<SpeechSpan>(accepted.Count + 1);
        var start = 0;
        foreach (var point in accepted)
        {
            spans.Add(new SpeechSpan(start, point));
            start = point;
        }

        spans.Add(new SpeechSpan(start, totalSamples));
        return spans;
    }
}