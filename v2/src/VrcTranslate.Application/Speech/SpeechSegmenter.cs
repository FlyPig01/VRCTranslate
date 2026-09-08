namespace VrcTranslate.Application.Speech;

/// <summary>
/// Turns short capture buffers into bounded speech sentences using a small RMS
/// gate. It keeps the UI pages independent from device buffer sizes and prevents
/// a translation request for every 100 ms callback.
/// </summary>
public sealed class SpeechSegmenter
{
    private readonly object _sync = new();
    private readonly List<float> _samples = [];
    private readonly int _sampleRate;
    private readonly float _energyThreshold;
    private readonly int _silenceSamples;
    private readonly int _maximumSamples;
    private int _silentSamples;
    private bool _hasSpeech;

    public SpeechSegmenter(
        int sampleRate = 16_000,
        float energyThreshold = 0.012f,
        int silenceMilliseconds = 520,
        int maximumMilliseconds = 8_000)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (energyThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(energyThreshold));
        if (silenceMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(silenceMilliseconds));
        if (maximumMilliseconds <= silenceMilliseconds) throw new ArgumentOutOfRangeException(nameof(maximumMilliseconds));
        _sampleRate = sampleRate;
        _energyThreshold = energyThreshold;
        _silenceSamples = Math.Max(1, sampleRate * silenceMilliseconds / 1000);
        _maximumSamples = Math.Max(_silenceSamples + 1, sampleRate * maximumMilliseconds / 1000);
    }

    public IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk)
    {
        if (chunk.IsEmpty) return [];
        lock (_sync)
        {
            var completed = new List<ReadOnlyMemory<float>>();
            var source = chunk.Span;
            var offset = 0;
            const int window = 320;
            while (offset < source.Length)
            {
                var count = Math.Min(window, source.Length - offset);
                var slice = source.Slice(offset, count);
                var active = Rms(slice) >= _energyThreshold;
                if (active) _hasSpeech = true;
                _samples.AddRange(slice.ToArray());
                if (_hasSpeech)
                {
                    _silentSamples = active ? 0 : _silentSamples + count;
                    if (_silentSamples >= _silenceSamples || _samples.Count >= _maximumSamples)
                    {
                        var segment = CompleteSegment();
                        if (!segment.IsEmpty) completed.Add(segment);
                    }
                }
                else
                {
                    // Keep a short pre-roll so the first consonant is not cut.
                    var preRoll = Math.Min(_sampleRate / 5, _samples.Count);
                    if (_samples.Count > preRoll) _samples.RemoveRange(0, _samples.Count - preRoll);
                }
                offset += count;
            }
            return completed;
        }
    }

    public int SampleRate => _sampleRate;

    public ReadOnlyMemory<float> Flush()
    {
        lock (_sync) return CompleteSegment();
    }

    private ReadOnlyMemory<float> CompleteSegment()
    {
        if (!_hasSpeech || _samples.Count == 0)
        {
            _samples.Clear();
            _silentSamples = 0;
            _hasSpeech = false;
            return ReadOnlyMemory<float>.Empty;
        }

        var segment = _samples.ToArray();
        _samples.Clear();
        _silentSamples = 0;
        _hasSpeech = false;
        return segment;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
