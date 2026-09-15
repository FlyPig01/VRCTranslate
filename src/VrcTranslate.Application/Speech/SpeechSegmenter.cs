namespace VrcTranslate.Application.Speech;

/// <summary>Counters that make segmentation misses measurable while tuning.</summary>
public readonly record struct SpeechSegmenterDiagnostics(
    int CompletedSegments,
    int DroppedShortSegments,
    float NoiseFloor,
    long TotalWindows,
    long ActiveWindows)
{
    public double ActiveRatio => TotalWindows == 0 ? 0 : (double)ActiveWindows / TotalWindows;
}

/// <summary>
/// Turns short capture buffers into bounded speech sentences using an RMS gate
/// whose threshold follows the measured noise floor. A fixed threshold cannot
/// serve both a noisy VRChat loopback (game sound keeps it "open") and a quiet
/// microphone (speech never reaches it), so the floor is tracked as a low
/// percentile of recent windows while nobody is talking, and the active/silence
/// thresholds derive from it with hysteresis.
/// </summary>
public sealed class SpeechSegmenter : ISpeechSegmenter
{
    private const float MinimumActiveThreshold = 0.006f;
    private const float MaximumActiveThreshold = 0.05f;
    private const float ActiveToFloorRatio = 4f;
    private const float SilenceToActiveRatio = 0.625f;
    private const int NoiseWindowCapacity = 100; // 2 s of 20 ms windows
    private const double NoisePercentile = 0.10;
    private const int FloorRecomputeWindows = 5; // refresh the percentile every 100 ms
    private const int PreRollMilliseconds = 320;
    private const int WindowSamples = 320;       // 20 ms at 16 kHz

    private readonly object _sync = new();
    private readonly List<float> _samples = [];
    private readonly float[] _noiseWindows = new float[NoiseWindowCapacity];
    private readonly int _sampleRate;
    private readonly int _silenceSamples;
    private readonly int _maximumSamples;
    private readonly int _minimumSpeechSamples;
    private readonly int _preRollSamples;
    private int _noiseWindowHead;
    private int _windowsSinceFloorUpdate;
    private float _noiseFloor;
    private int _silentSamples;
    private int _activeSamples;
    private bool _hasSpeech;
    private int _completedSegments;
    private int _droppedShortSegments;
    private long _totalWindows;
    private long _activeWindows;

    public SpeechSegmenter(
        int sampleRate = 16_000,
        float energyThreshold = 0.012f,
        int silenceMilliseconds = 600,
        int maximumMilliseconds = 12_000,
        int minimumSpeechMilliseconds = 240)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (energyThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(energyThreshold));
        if (silenceMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(silenceMilliseconds));
        if (maximumMilliseconds <= silenceMilliseconds) throw new ArgumentOutOfRangeException(nameof(maximumMilliseconds));
        if (minimumSpeechMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(minimumSpeechMilliseconds));
        _sampleRate = sampleRate;
        _silenceSamples = Math.Max(1, sampleRate * silenceMilliseconds / 1000);
        _maximumSamples = Math.Max(_silenceSamples + 1, sampleRate * maximumMilliseconds / 1000);
        _minimumSpeechSamples = sampleRate * minimumSpeechMilliseconds / 1000;
        _preRollSamples = sampleRate * PreRollMilliseconds / 1000;
        // Seed the tracker so the first utterance is judged against the
        // requested sensitivity until real ambient data takes over.
        Array.Fill(_noiseWindows, energyThreshold);
        _noiseFloor = energyThreshold;
    }

    public int SampleRate => _sampleRate;

    public IReadOnlyList<ReadOnlyMemory<float>> Append(ReadOnlyMemory<float> chunk)
    {
        if (chunk.IsEmpty) return [];
        lock (_sync)
        {
            var completed = new List<ReadOnlyMemory<float>>();
            var source = chunk.Span;
            var offset = 0;
            while (offset < source.Length)
            {
                var count = Math.Min(WindowSamples, source.Length - offset);
                var slice = source.Slice(offset, count);
                var rms = Rms(slice);
                _totalWindows++;

                var activeThreshold = Math.Clamp(
                    _noiseFloor * ActiveToFloorRatio, MinimumActiveThreshold, MaximumActiveThreshold);
                var active = rms >= activeThreshold;
                if (active)
                {
                    _activeWindows++;
                    _hasSpeech = true;
                }

                _samples.AddRange(slice.ToArray());
                if (_hasSpeech)
                {
                    _activeSamples += active ? count : 0;
                    // Release needs to be quieter than attack, otherwise a
                    // voice that hovers around the threshold flaps the gate.
                    var silenceThreshold = activeThreshold * SilenceToActiveRatio;
                    _silentSamples = rms < silenceThreshold ? _silentSamples + count : 0;
                    if (_silentSamples >= _silenceSamples || _samples.Count >= _maximumSamples)
                    {
                        var segment = CompleteSegment();
                        if (!segment.IsEmpty) completed.Add(segment);
                    }
                }
                else
                {
                    UpdateNoiseFloor(rms);
                    // Keep a pre-roll so the first consonant is not cut.
                    var preRoll = Math.Min(_preRollSamples, _samples.Count);
                    if (_samples.Count > preRoll) _samples.RemoveRange(0, _samples.Count - preRoll);
                }
                offset += count;
            }
            return completed;
        }
    }

    public IReadOnlyList<ReadOnlyMemory<float>> Flush()
    {
        lock (_sync)
        {
            var segment = CompleteSegment();
            return segment.IsEmpty ? [] : [segment];
        }
    }

    public SpeechSegmenterDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new SpeechSegmenterDiagnostics(
                    _completedSegments, _droppedShortSegments, _noiseFloor, _totalWindows, _activeWindows);
            }
        }
    }

    /// <summary>
    /// Tracks ambient loudness as a low percentile of recent windows. Only
    /// updated while the gate is closed: a long utterance must not drag the
    /// "noise" estimate up to voice level and cut itself off.
    /// </summary>
    private void UpdateNoiseFloor(float windowRms)
    {
        _noiseWindows[_noiseWindowHead] = windowRms;
        _noiseWindowHead = (_noiseWindowHead + 1) % NoiseWindowCapacity;
        _windowsSinceFloorUpdate++;
        if (_windowsSinceFloorUpdate < FloorRecomputeWindows) return;
        _windowsSinceFloorUpdate = 0;

        var snapshot = new float[NoiseWindowCapacity];
        Array.Copy(_noiseWindows, snapshot, NoiseWindowCapacity);
        Array.Sort(snapshot);
        _noiseFloor = snapshot[(int)Math.Floor(NoiseWindowCapacity * NoisePercentile)];
    }

    private ReadOnlyMemory<float> CompleteSegment()
    {
        var segment = _samples.ToArray();
        var activeSamples = _activeSamples;
        _samples.Clear();
        _silentSamples = 0;
        _activeSamples = 0;
        _hasSpeech = false;
        if (segment.Length == 0 || activeSamples < _minimumSpeechSamples)
        {
            // A click or door slam opens the gate for a few windows; sending
            // that to the recognizer only produces empty captions. A tail with
            // no active audio at all (a stop-time flush) is not a miss.
            if (activeSamples > 0) _droppedShortSegments++;
            return ReadOnlyMemory<float>.Empty;
        }

        _completedSegments++;
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
