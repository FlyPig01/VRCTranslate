namespace VrcTranslate.Core.Speech;

/// <summary>Vector helpers shared by voiceprint matching and the speaker pipeline.</summary>
public static class SpeakerMath
{
    /// <summary>Unit-length copy of the vector; empty when the input has no direction.</summary>
    public static float[] Normalize(ReadOnlySpan<float> values)
    {
        var norm = 0f;
        foreach (var value in values) norm += value * value;
        if (norm <= float.Epsilon) return [];
        var scale = 1f / MathF.Sqrt(norm);
        var normalized = new float[values.Length];
        for (var i = 0; i < values.Length; i++) normalized[i] = values[i] * scale;
        return normalized;
    }

    /// <summary>
    /// True cosine similarity. The scale of the inputs is divided out here rather
    /// than trusted, because raw model embeddings are not unit length and a dot
    /// product of them looks like a large similarity that no threshold can judge.
    /// Mismatched, empty or zero vectors return
    /// <see cref="float.NegativeInfinity"/> so they can never win a comparison.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0) return float.NegativeInfinity;
        var dot = 0f;
        var leftNorm = 0f;
        var rightNorm = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm <= float.Epsilon || rightNorm <= float.Epsilon) return float.NegativeInfinity;
        return dot / MathF.Sqrt(leftNorm * rightNorm);
    }
}