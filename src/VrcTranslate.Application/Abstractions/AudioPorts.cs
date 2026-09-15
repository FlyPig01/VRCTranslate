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

/// <summary>
/// Small platform boundary for microphone and loopback audio capture. Events
/// only carry application-layer values: no NAudio, COM or Win32 type ever
/// crosses this boundary.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    AudioCaptureMode Mode { get; }

    /// <summary>The source this capture delivers right now.</summary>
    AudioCaptureSourceKind SourceKind { get; }

    int SampleRate { get; }

    event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    /// <summary>Raised whenever the delivered source changes; never raised twice for one state.</summary>
    event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    /// <summary>Raised when the source fails; the capture may still be recovering.</summary>
    event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    /// <summary>Raised at most once per start, after the capture stopped delivering samples.</summary>
    event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Composition boundary for platform audio devices. Desktop pages request a
/// source without depending on NAudio or a Windows implementation.
/// </summary>
public interface IAudioCaptureFactory
{
    /// <summary>
    /// Creates a capture session for the requested source. A process loopback
    /// request on a platform without that capability fails with
    /// <see cref="ProcessLoopbackNotSupportedException"/> instead of silently
    /// falling back to a different source.
    /// </summary>
    IAudioCapture Create(AudioCaptureRequest request);
}

/// <summary>One selectable audio capture endpoint shown in the UI.</summary>
public sealed record AudioDeviceInfo(string Id, string DisplayName, bool IsDefault);

/// <summary>Enumerates the capture devices the user can pick for own-voice input.</summary>
public interface IAudioDeviceEnumerator
{
    /// <summary>Active recording endpoints, the Windows default first.</summary>
    IReadOnlyList<AudioDeviceInfo> ListMicrophones();
}
