using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Captures microphone input for own voice or Windows output for VRChat voice
/// captions. The capture device is converted to mono 16 kHz float samples before
/// it crosses the application boundary, which is the format used by Whisper.
/// </summary>
public sealed class WindowsAudioCapture : IAudioCapture
{
    private readonly object _sync = new();
    private readonly AudioCaptureMode _mode;
    private readonly string? _microphoneEndpointId;
    private readonly int _microphoneDeviceNumber;
    private readonly bool _useDefaultMicrophoneEndpoint;
    private IWaveIn? _capture;
    private bool _started;
    private bool _disposed;

    public WindowsAudioCapture(AudioCaptureMode mode, string? microphoneDeviceId = null)
    {
        _mode = mode;
        _useDefaultMicrophoneEndpoint = IsDefaultMicrophoneId(microphoneDeviceId);
        // A WASAPI endpoint id is a long "{...}" GUID string; plain digits are
        // legacy wave-in indexes from older settings.
        _microphoneEndpointId = !_useDefaultMicrophoneEndpoint && !IsWaveInIndex(microphoneDeviceId)
            ? microphoneDeviceId!.Trim()
            : null;
        _microphoneDeviceNumber = ParseMicrophoneDeviceNumber(microphoneDeviceId);
    }

    public AudioCaptureMode Mode => _mode;

    /// <summary>Resolved wave-in index used when <see cref="Mode"/> is Microphone.</summary>
    public int MicrophoneDeviceNumber => _microphoneDeviceNumber;

    /// <summary>Whether the Windows default recording endpoint will be used.</summary>
    public bool UsesDefaultMicrophoneEndpoint => _useDefaultMicrophoneEndpoint;

    public int SampleRate => AudioSampleConverter.TargetSampleRate;

    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started) return Task.CompletedTask;

            IWaveIn capture = _mode == AudioCaptureMode.Microphone
                ? CreateMicrophoneCapture()
                : new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            try
            {
                capture.StartRecording();
                _started = true;
            }
            catch
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                _capture = null;
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWaveIn? capture;
        lock (_sync)
        {
            if (!_started)
            {
                return Task.CompletedTask;
            }

            _started = false;
            capture = _capture;
            _capture = null;
        }

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (InvalidOperationException) { }
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private IWaveIn CreateMicrophoneCapture()
    {
        if (_useDefaultMicrophoneEndpoint)
        {
            // Device 0 is the first MME input, not necessarily the Windows
            // default recording endpoint. WASAPI follows the user's selected
            // default device and works with USB/Bluetooth endpoints that do
            // not appear at MME index 0.
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return new WasapiCapture(endpoint);
        }

        if (_microphoneEndpointId is not null)
        {
            // Endpoint ids come from WindowsAudioDeviceEnumerator, so the
            // selected device (e.g. a Bluetooth headset) is used even when it
            // is not the system default.
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = enumerator.GetDevice(_microphoneEndpointId);
            return new WasapiCapture(endpoint);
        }

        var capture = new WaveInEvent
        {
            DeviceNumber = _microphoneDeviceNumber,
            BufferMilliseconds = 100,
            NumberOfBuffers = 3,
            WaveFormat = new WaveFormat(AudioSampleConverter.TargetSampleRate, 16, 1)
        };
        return capture;
    }

    private static bool IsDefaultMicrophoneId(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId)
        || deviceId.Trim().Equals("default", StringComparison.OrdinalIgnoreCase);

    private static bool IsWaveInIndex(string? deviceId) =>
        int.TryParse(deviceId?.Trim(), out var index) && index >= 0;

    private static int ParseMicrophoneDeviceNumber(string? deviceId)
    {
        if (IsDefaultMicrophoneId(deviceId))
        {
            return 0;
        }

        return int.TryParse(deviceId?.Trim(), out var index) && index >= 0
            ? index
            : 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0 || sender is not IWaveIn capture) return;
        var format = capture.WaveFormat;
        var samples = AudioSampleConverter.ToMono16k(args.Buffer, args.BytesRecorded, format);
        if (samples.Length == 0) return;
        try
        {
            var eventArgs = new AudioSamplesEventArgs(samples, SampleRate);
            SamplesReady?.Invoke(this, eventArgs);
        }
        catch
        {
            // Capture callbacks run on a device thread. Subscriber failures must
            // never tear down the audio device.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        // A disconnected device is observed by the next start attempt. No UI
        // work is performed on the NAudio callback thread.
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsAudioCapture));
    }
}

/// <summary>Windows composition-root implementation of the audio port.</summary>
public sealed class WindowsAudioCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture Create(AudioCaptureMode mode) => new WindowsAudioCapture(mode);

    public IAudioCapture Create(AudioCaptureMode mode, string? deviceId = null) =>
        new WindowsAudioCapture(mode, deviceId);
}

/// <summary>
/// Lists active recording endpoints through WASAPI so the picker can offer
/// every connected microphone (USB, Bluetooth, arrays), unlike the legacy
/// wave-in enumeration that hides several device classes.
/// </summary>
public sealed class WindowsAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> ListMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        string defaultId;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID;
        }
        catch
        {
            // No default endpoint (e.g. a fresh session without recording
            // devices) still allows listing whatever is active.
            defaultId = string.Empty;
        }

        var result = new List<AudioDeviceInfo>(devices.Count);
        foreach (var device in devices)
        {
            string id;
            string name;
            try
            {
                id = device.ID;
                name = device.FriendlyName;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new AudioDeviceInfo(id, string.IsNullOrWhiteSpace(name) ? "麦克风" : name, id == defaultId));
        }

        // The default device first so the picker matches the system order the
        // user already knows from Windows sound settings.
        return result
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }
}

/// <summary>Deterministic PCM/float conversion kept separate for unit testing.</summary>
public static class AudioSampleConverter
{
    public const int TargetSampleRate = 16_000;

    public static float[] ToMono16k(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);
        if (bytesRecorded <= 0) return [];
        bytesRecorded = Math.Min(bytesRecorded, buffer.Length);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameBytes = bytesPerSample * Math.Max(1, format.Channels);
        if (frameBytes <= 0) return [];
        var frameCount = bytesRecorded / frameBytes;
        if (frameCount == 0) return [];

        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * frameBytes;
            var sum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                sum += ReadSample(buffer, offset + channel * bytesPerSample, bytesPerSample, format.Encoding);
            }
            mono[frame] = Math.Clamp(sum / Math.Max(1, format.Channels), -1f, 1f);
        }

        if (format.SampleRate == TargetSampleRate) return mono;
        var outputCount = Math.Max(1, (int)Math.Round(mono.Length * (double)TargetSampleRate / format.SampleRate));
        var output = new float[outputCount];
        var ratio = (double)format.SampleRate / TargetSampleRate;
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * ratio;
            var left = Math.Min(mono.Length - 1, (int)position);
            var right = Math.Min(mono.Length - 1, left + 1);
            var fraction = (float)(position - left);
            output[i] = mono[left] + ((mono[right] - mono[left]) * fraction);
        }
        return output;
    }

    private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, WaveFormatEncoding encoding)
    {
        if (offset < 0 || offset + bytesPerSample > buffer.Length) return 0;
        if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample >= 4)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => Read24Bit(buffer, offset) / 8_388_608f,
            4 => BitConverter.ToInt32(buffer, offset) / 2_147_483_648f,
            _ => 0f
        };
    }

    private static int Read24Bit(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
