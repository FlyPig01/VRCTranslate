using System.Runtime.InteropServices;
using NAudio.Wave;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class AudioSampleConverterTests
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid DolbySubFormat = new("00000092-0000-0010-8000-00aa00389b71");

    [Fact]
    public void Converts_stereo_pcm_to_mono_without_changing_16khz_rate()
    {
        var format = new WaveFormat(16_000, 16, 2);
        var bytes = new byte[4 * 2];
        WriteInt16(bytes, 0, 16_384);
        WriteInt16(bytes, 2, 0);
        WriteInt16(bytes, 4, 0);
        WriteInt16(bytes, 6, 16_384);

        var samples = AudioSampleConverter.ToMono16k(bytes, bytes.Length, format);

        Assert.Equal(2, samples.Length);
        Assert.InRange(samples[0], 0.24f, 0.26f);
        Assert.InRange(samples[1], 0.24f, 0.26f);
    }

    [Fact]
    public void Resamples_48khz_input_to_16khz()
    {
        var format = new WaveFormat(48_000, 16, 1);
        var bytes = new byte[480 * 2];
        for (var i = 0; i < 480; i++) WriteInt16(bytes, i * 2, 8_192);

        var samples = AudioSampleConverter.ToMono16k(bytes, bytes.Length, format);

        Assert.Equal(160, samples.Length);
        Assert.All(samples, sample => Assert.InRange(sample, 0.24f, 0.26f));
    }

    [Fact]
    public async Task Windows_factory_keeps_microphone_and_loopback_modes_separate()
    {
        var factory = new WindowsAudioCaptureFactory();
        await using var microphone = factory.Create(AudioCaptureRequest.Microphone());
        await using var loopback = factory.Create(AudioCaptureRequest.SystemLoopback());

        Assert.Equal(AudioCaptureMode.Microphone, microphone.Mode);
        Assert.Equal(AudioCaptureMode.SystemLoopback, loopback.Mode);
        Assert.Equal(AudioCaptureSourceKind.Microphone, microphone.SourceKind);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, loopback.SourceKind);
        Assert.Equal(16_000, microphone.SampleRate);
        Assert.Equal(16_000, loopback.SampleRate);
    }

    [Fact]
    public async Task Windows_factory_answers_a_process_request_according_to_the_os_build()
    {
        // Application Loopback needs Windows 10 Build 20348. Below that floor the
        // request must fail loudly instead of silently returning system loopback,
        // which would capture every application while the caller believes only
        // VRChat is being recorded. At or above it the factory must hand back a
        // real process capture - never a system one - and creating it must not
        // start any COM activation.
        var factory = new WindowsAudioCaptureFactory();
        var request = AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(4_242));

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, WindowsProcessLoopbackSupport.MinimumBuild))
        {
            Assert.Throws<ProcessLoopbackNotSupportedException>(() => factory.Create(request));
            return;
        }

        await using var capture = factory.Create(request);

        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, capture.SourceKind);
        Assert.NotEqual(AudioCaptureSourceKind.SystemLoopback, capture.SourceKind);
        Assert.Equal(AudioCaptureMode.SystemLoopback, capture.Mode);
        Assert.Equal(16_000, capture.SampleRate);
        Assert.Equal(4_242, Assert.IsType<ProcessLoopbackAudioCapture>(capture).Target.ProcessId);
    }


    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("default", 0)]
    [InlineData("DEFAULT", 0)]
    [InlineData("3", 3)]
    [InlineData(" 7 ", 7)]
    [InlineData("-1", 0)]
    [InlineData("not-a-device", 0)]
    public void Windows_factory_resolves_microphone_device_id(string? deviceId, int expectedIndex)
    {
        var capture = new WindowsAudioCapture(AudioCaptureMode.Microphone, deviceId);

        Assert.Equal(expectedIndex, capture.MicrophoneDeviceNumber);
        Assert.Equal(
            string.IsNullOrWhiteSpace(deviceId) || deviceId.Trim().Equals("default", StringComparison.OrdinalIgnoreCase),
            capture.UsesDefaultMicrophoneEndpoint);
    }

    [Fact]
    public void Loopback_capture_ignores_microphone_device_id()
    {
        var capture = new WindowsAudioCapture(AudioCaptureMode.SystemLoopback, "9");

        Assert.Equal(AudioCaptureMode.SystemLoopback, capture.Mode);
        Assert.Equal(9, capture.MicrophoneDeviceNumber);
        Assert.False(capture.UsesDefaultMicrophoneEndpoint);
    }

    [Fact]
    public void Extensible_48khz_float_mix_format_is_decoded_as_float()
    {
        // This is what WASAPI hands back for the shared mix format: NAudio keeps
        // the extensible tag, so the 32-bit float samples would be read as
        // 32-bit integers without resolving the sub-format first.
        var format = ParseExtensibleFormat(48_000, 32, 2, IeeeFloatSubFormat);
        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        var bytes = StereoFloatSine(48_000, 48_000, 440, 0.5f);

        var samples = AudioSampleConverter.ToMono16k(bytes, bytes.Length, format);

        Assert.Equal(16_000, samples.Length);
        Assert.InRange(ToneAnalysis.MagnitudeAt(samples, 440), 0.45, 0.55);
        Assert.True(
            ToneAnalysis.MagnitudeAt(samples, 440) > ToneAnalysis.MagnitudeAt(samples, 880) * 8,
            "扩展格式的 32 位浮点数据没有被按浮点解码。");
    }

    [Fact]
    public void Extensible_pcm_format_is_normalized_before_conversion()
    {
        var format = ParseExtensibleFormat(48_000, 16, 2, PcmSubFormat);
        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        Assert.Equal(WaveFormatEncoding.Pcm, AudioSampleConverter.NormalizeFormat(format).Encoding);
        var bytes = StereoPcm16Sine(48_000, 48_000, 440, 0.5f);

        var samples = AudioSampleConverter.ToMono16k(bytes, bytes.Length, format);

        Assert.Equal(16_000, samples.Length);
        Assert.InRange(ToneAnalysis.MagnitudeAt(samples, 440), 0.45, 0.55);
    }

    [Fact]
    public void Is_convertible_accepts_extensible_pcm_and_float_and_rejects_the_rest()
    {
        Assert.True(AudioSampleConverter.IsConvertible(ParseExtensibleFormat(48_000, 32, 2, IeeeFloatSubFormat)));
        Assert.True(AudioSampleConverter.IsConvertible(ParseExtensibleFormat(48_000, 16, 2, PcmSubFormat)));
        Assert.True(AudioSampleConverter.IsConvertible(new WaveFormat(48_000, 24, 2)));

        // A compressed sub-format has no PCM or float samples inside; converting
        // it would emit noise, so the capture reports a fault instead.
        Assert.False(AudioSampleConverter.IsConvertible(ParseExtensibleFormat(48_000, 16, 2, DolbySubFormat)));
        Assert.False(AudioSampleConverter.IsConvertible(new WaveFormat(16_000, 8, 1)));
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        var bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 2);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        WriteInt16(buffer, offset, unchecked((short)value));

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    /// <summary>Builds the forty-byte WAVEFORMATEXTENSIBLE WASAPI reports.</summary>
    private static WaveFormat ParseExtensibleFormat(int sampleRate, int bits, int channels, Guid subFormat)
    {
        const int size = 40;
        var bytes = new byte[size];
        var blockAlign = channels * bits / 8;
        WriteUInt16(bytes, 0, 0xFFFE);
        WriteUInt16(bytes, 2, (ushort)channels);
        WriteUInt32(bytes, 4, (uint)sampleRate);
        WriteUInt32(bytes, 8, (uint)(sampleRate * blockAlign));
        WriteUInt16(bytes, 12, (ushort)blockAlign);
        WriteUInt16(bytes, 14, (ushort)bits);
        WriteUInt16(bytes, 16, 22);
        WriteUInt16(bytes, 18, (ushort)bits);
        WriteUInt32(bytes, 20, channels == 2 ? 3u : 4u);
        subFormat.ToByteArray().CopyTo(bytes, 24);

        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, pointer, size);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static byte[] StereoFloatSine(int sampleRate, int frames, double frequency, float amplitude)
    {
        var buffer = new byte[frames * 2 * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
            for (var channel = 0; channel < 2; channel++)
            {
                BitConverter.GetBytes(value).CopyTo(buffer, ((frame * 2) + channel) * 4);
            }
        }

        return buffer;
    }

    private static byte[] StereoPcm16Sine(int sampleRate, int frames, double frequency, float amplitude)
    {
        var buffer = new byte[frames * 2 * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
            for (var channel = 0; channel < 2; channel++)
            {
                WriteInt16(buffer, ((frame * 2) + channel) * 2, value);
            }
        }

        return buffer;
    }
}
