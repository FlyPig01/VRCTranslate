namespace VrcTranslate.Core.Speech;

/// <summary>Turns detected speaker-change points into the ranges to recognize separately.</summary>
public static class SpeakerSegmentSplitter
{
    /// <summary>
    /// Change points closer than <paramref name="minPartSamples"/> to an edge or to
    /// each other are ignored, and the result never exceeds
    /// <paramref name="maxParts"/> pieces: a noisy detection must not shred a
    /// sentence into fragments too short to recognize.
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

        var accepted = new List<int>();
        foreach (var point in changePoints.Order())
        {
            if (point < minPartSamples || point > totalSamples - minPartSamples) continue;
            if (accepted.Count > 0 && point - accepted[^1] < minPartSamples) continue;
            accepted.Add(point);
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