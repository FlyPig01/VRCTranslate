using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// Captures microphone input for own voice or Windows output for VRChat voice
/// captions. The capture device is converted to mono 16 kHz float samples before
/// it crosses the application boundary, which is the format SenseVoice needs.
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
    private bool _stopPublished;

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

    public AudioCaptureSourceKind SourceKind => _mode == AudioCaptureMode.Microphone
        ? AudioCaptureSourceKind.Microphone
        : AudioCaptureSourceKind.SystemLoopback;

    /// <summary>Resolved wave-in index used when <see cref="Mode"/> is Microphone.</summary>
    public int MicrophoneDeviceNumber => _microphoneDeviceNumber;

    /// <summary>Whether the Windows default recording endpoint will be used.</summary>
    public bool UsesDefaultMicrophoneEndpoint => _useDefaultMicrophoneEndpoint;

    public int SampleRate => AudioSampleConverter.TargetSampleRate;

    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    /// <summary>This capture never changes source, so the event only reports the initial one.</summary>
    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = false;
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
                _stopPublished = false;
                started = true;
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

        // Published outside the lock so a subscriber may call back into the capture.
        if (started) RaiseSourceChanged();
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
            // Deterministic counterpart to the platform callback: whichever
            // path runs first publishes, the other one is suppressed.
            RaiseStopped(exception: null);
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
        // NAudio reports an invalidated device here; nothing is repaired on the
        // callback thread, the events only let the coordinator decide what to do.
        if (args.Exception is not null) RaiseFaulted(args.Exception);
        RaiseStopped(args.Exception);
    }

    private void RaiseSourceChanged()
    {
        try
        {
            SourceChanged?.Invoke(this, new AudioSourceChangedEventArgs(
                new AudioSourceState(SourceKind), generation: 0, isBoundary: false));
        }
        catch { }
    }

    private void RaiseFaulted(Exception exception)
    {
        try { Faulted?.Invoke(this, new AudioCaptureFaultedEventArgs(SourceKind, exception)); }
        catch { }
    }

    private void RaiseStopped(Exception? exception)
    {
        lock (_sync)
        {
            if (_stopPublished) return;
            _stopPublished = true;
        }

        try { Stopped?.Invoke(this, new AudioCaptureStoppedEventArgs(SourceKind, exception)); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsAudioCapture));
    }
}

/// <summary>Windows composition-root implementation of the audio port.</summary>
public sealed class WindowsAudioCaptureFactory : IAudioCaptureFactory
{
    private readonly IProcessLoopbackSupport _processLoopbackSupport;

    /// <summary>Creates the factory with the real operating system build check.</summary>
    public WindowsAudioCaptureFactory()
        : this(WindowsProcessLoopbackSupport.Instance)
    {
    }

    /// <summary>
    /// Creates the factory with an injected capability decision, which is how
    /// the "this build is too old to try" path stays testable on a machine that
    /// is new enough.
    /// </summary>
    public WindowsAudioCaptureFactory(IProcessLoopbackSupport processLoopbackSupport) =>
        _processLoopbackSupport = processLoopbackSupport ?? throw new ArgumentNullException(nameof(processLoopbackSupport));

    public IAudioCapture Create(AudioCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.SourceKind switch
        {
            AudioCaptureSourceKind.Microphone =>
                new WindowsAudioCapture(AudioCaptureMode.Microphone, request.MicrophoneDeviceId),
            AudioCaptureSourceKind.ProcessLoopback => CreateProcessLoopback(request),
            _ => new WindowsAudioCapture(AudioCaptureMode.SystemLoopback)
        };
    }

    /// <summary>
    /// A request for process loopback is answered before any COM object is
    /// touched: a build below the Application Loopback floor is reported as
    /// unsupported and the caller keeps system audio, instead of failing at an
    /// activation that could never succeed. A supported build gets a real
    /// capture; it is never silently downgraded to the system mix.
    /// </summary>
    private IAudioCapture CreateProcessLoopback(AudioCaptureRequest request)
    {
        if (!_processLoopbackSupport.IsSupported)
        {
            throw new ProcessLoopbackNotSupportedException(
                $"进程回环采集需要 Windows 10 Build {WindowsProcessLoopbackSupport.MinimumBuild} 或更高版本，"
                + $"当前系统 Build 为 {WindowsProcessLoopbackSupport.DescribeBuild(_processLoopbackSupport.Build)}；"
                + "已改用系统回环。");
        }

        var target = request.TargetProcess
            ?? throw new ArgumentException("进程回环请求缺少目标进程。", nameof(request));
        return new ProcessLoopbackAudioCapture(target);
    }
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

    /// <summary>
    /// Resolves the real sample type of a WASAPI mix format. WASAPI reports the
    /// shared mix format as <c>WAVEFORMATEXTENSIBLE</c>, which NAudio keeps as
    /// <see cref="WaveFormatEncoding.Extensible"/> and describes through
    /// <see cref="WaveFormatExtensible.SubFormat"/>; without this step a 32-bit
    /// float stream would be decoded as 32-bit integer and every sample would be
    /// wrong. Formats that are already explicit are returned unchanged.
    /// </summary>
    public static WaveFormat NormalizeFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format is WaveFormatExtensible extensible && format.Encoding == WaveFormatEncoding.Extensible
            ? extensible.ToStandardWaveFormat()
            : format;
    }

    /// <summary>
    /// Whether <see cref="ToMono16k"/> can decode the format. A format that
    /// fails this check must be reported as a fault instead of being converted
    /// into silence or noise.
    /// </summary>
    public static bool IsConvertible(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        format = NormalizeFormat(format);
        if (format.Channels <= 0 || format.SampleRate <= 0) return false;
        if (format.BitsPerSample / 8 is not (2 or 3 or 4)) return false;
        return format.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat;
    }

    public static float[] ToMono16k(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);
        format = NormalizeFormat(format);
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
