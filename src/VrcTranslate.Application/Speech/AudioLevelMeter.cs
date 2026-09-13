namespace VrcTranslate.Application.Speech;

/// <summary>How raw capture levels become a 0..1 meter reading.</summary>
public sealed record AudioLevelMeterOptions
{
    /// <summary>Bars kept in the rolling history.</summary>
    public int BarCount { get; init; } = 16;

    /// <summary>Bottom of the displayed scale.</summary>
    public float FloorDecibels { get; init; } = -60f;

    /// <summary>Top of the displayed scale.</summary>
    public float CeilingDecibels { get; init; } = 0f;

    /// <summary>
    /// Below this level is room noise rather than a voice: the meter reads zero
    /// and the auto-gain reference stops moving, so a silent room cannot pump the
    /// display up.
    /// </summary>
    public float NoiseFloor { get; init; } = 0.06f;

    /// <summary>
    /// Display curve applied to the decibel scale. Speech lives in the lower half
    /// of a linear decibel range, so a power below one spreads it across the meter
    /// while still leaving room for a shout to stand out.
    /// </summary>
    public float Gamma { get; init; } = 0.6f;

    /// <summary>What the loudest recent speech is lifted towards.</summary>
    public float GainTarget { get; init; } = 0.8f;

    /// <summary>
    /// How fast the loudness reference forgets, per display tick. Slow on purpose:
    /// it should follow who is talking now, not the syllable in flight, or every
    /// voice would be flattened to the same height.
    /// </summary>
    public float ReferenceDecay { get; init; } = 0.004f;

    /// <summary>
    /// Ceiling on the auto-gain. Deliberately small: enough that a quiet
    /// microphone still fills the meter, small enough that a shout stays visibly
    /// taller than normal speech instead of everything being normalised flat.
    /// </summary>
    public float MaxGain { get; init; } = 1.4f;

    /// <summary>Per-tick fall once the input gets quieter; about -18 dB per second.</summary>
    public float Release { get; init; } = 0.88f;

    /// <summary>
    /// Display ticks to hold the last level while no capture callback arrives.
    /// The capture device delivers a buffer roughly every 100 ms while the meter
    /// draws faster than that, so without a hold the level sags on every other bar
    /// and the meter looks sparse instead of like a steady voice.
    /// </summary>
    public int HoldTicks { get; init; } = 3;
}

/// <summary>
/// Turns capture levels into the value a level meter draws.
/// 
/// A linear amplitude is the wrong scale for speech: a comfortable voice sits
/// near 0.05 RMS, which reads as 5% of a linear bar and makes every speaker look
/// quiet. The level is therefore converted to decibels first — speech occupies
/// roughly -40..-12 dBFS, which fills the scale — and then stretched by an
/// auto-gain that tracks the loudest recent speech, so the display uses its full
/// height without amplifying a silent room.
/// </summary>
public sealed class AudioLevelMeter
{
    private readonly AudioLevelMeterOptions _options;
    private double[] _history;
    private IReadOnlyList<double> _readOnlyHistory;
    private double _level;
    private double _pending;
    private double _reference;
    private int _ticksSinceObservation;

    public AudioLevelMeter(AudioLevelMeterOptions? options = null)
    {
        _options = options ?? new AudioLevelMeterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BarCount, 1);
        _history = new double[_options.BarCount];
        _readOnlyHistory = Array.AsReadOnly(_history);
    }

    public double Level => _level;

    /// <summary>Oldest first, newest last; updates on every <see cref="Advance"/>.</summary>
    public IReadOnlyList<double> History => _readOnlyHistory;

    /// <summary>
    /// Feeds one capture callback. The loudest reading since the last
    /// <see cref="Advance"/> wins, so a burst between display ticks is not lost.
    /// </summary>
    public void Observe(float rms)
    {
        var normalized = Normalize(rms);
        if (normalized < _options.NoiseFloor) return;

        _ticksSinceObservation = 0;
        var shaped = Math.Pow(normalized, _options.Gamma);
        _reference = Math.Max(shaped, Math.Max(_options.NoiseFloor, _reference - _options.ReferenceDecay));
        var gain = Math.Clamp(
            _options.GainTarget / Math.Max(_reference, 0.45f),
            1d,
            _options.MaxGain);
        _pending = Math.Max(_pending, Math.Clamp(shaped * gain, 0d, 1d));
    }

    /// <summary>Moves the meter one display step and records it in the history.</summary>
    public void Advance()
    {
        var target = _pending;
        _pending = 0;
        _ticksSinceObservation++;

        // Between two capture callbacks there is nothing new to show; holding the
        // last level keeps the meter continuous instead of strobing.
        if (target > 0 || _ticksSinceObservation > _options.HoldTicks)
        {
            _level = target >= _level ? target : _level * _options.Release;
        }

        if (_level < 0.005) _level = 0;

        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = _level;
    }

    /// <summary>
    /// Rebuilds the history for a new meter width. The card resizes with the
    /// window, so the number of bars has to follow it instead of leaving a gap.
    /// </summary>
    public void Resize(int barCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        if (barCount == _history.Length) return;
        _history = new double[barCount];
        _readOnlyHistory = Array.AsReadOnly(_history);
    }

    public void Reset()
    {
        _level = 0;
        _pending = 0;
        _reference = 0;
        _ticksSinceObservation = 0;
        Array.Clear(_history);
    }

    private double Normalize(float rms)
    {
        var decibels = 20 * Math.Log10(Math.Max(rms, 1e-5f));
        var span = _options.CeilingDecibels - _options.FloorDecibels;
        return Math.Clamp((decibels - _options.FloorDecibels) / span, 0d, 1d);
    }
}