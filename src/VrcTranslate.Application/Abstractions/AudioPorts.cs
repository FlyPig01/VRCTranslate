namespace VrcTranslate.Application.Abstractions;

/// <summary>Where a live speech session receives audio from.</summary>
public enum AudioCaptureMode
{
    Microphone,
    SystemLoopback
}

public sealed class AudioSamplesEventArgs : EventArgs
{
    public AudioSamplesEventArgs(ReadOnlyMemory<float> samples, int sampleRate)
    {
        if (samples.IsEmpty) throw new ArgumentException("Audio samples are required.", nameof(samples));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Samples = samples;
        SampleRate = sampleRate;
    }

    public ReadOnlyMemory<float> Samples { get; }

    public int SampleRate { get; }
}

/// <summary>Measured activity for one captured audio buffer.</summary>
public sealed class AudioLevelEventArgs : EventArgs
{
    public AudioLevelEventArgs(float rms, float peak)
    {
        Rms = Math.Clamp(rms, 0f, 1f);
        Peak = Math.Clamp(peak, 0f, 1f);
    }

    public float Rms { get; }

    public float Peak { get; }
}

/// <summary>Small platform boundary for microphone and VRChat audio capture.</summary>
public interface IAudioCapture : IAsyncDisposable
{
    AudioCaptureMode Mode { get; }

    int SampleRate { get; }

    event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Composition boundary for platform audio devices. Desktop pages request a
/// capture mode without depending on NAudio or a Windows implementation.
/// </summary>
public interface IAudioCaptureFactory
{
    /// <summary>Creates a capture session using the platform default device.</summary>
    IAudioCapture Create(AudioCaptureMode mode);

    /// <summary>
    /// Creates a capture session for the requested source. For microphone
    /// capture, <paramref name="deviceId"/> may be <c>default</c>, a WASAPI
    /// endpoint id from <see cref="IAudioDeviceEnumerator"/>, or a numeric
    /// Windows wave-in device index from older settings. Loopback capture
    /// ignores it.
    /// </summary>
    IAudioCapture Create(AudioCaptureMode mode, string? deviceId) => Create(mode);
}

/// <summary>One selectable audio capture endpoint shown in the UI.</summary>
public sealed record AudioDeviceInfo(string Id, string DisplayName, bool IsDefault);

/// <summary>Enumerates the capture devices the user can pick for own-voice input.</summary>
public interface IAudioDeviceEnumerator
{
    /// <summary>Active recording endpoints, the Windows default first.</summary>
    IReadOnlyList<AudioDeviceInfo> ListMicrophones();
}
