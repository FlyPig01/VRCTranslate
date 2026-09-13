using NAudio.Wave;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class AudioSampleConverterTests
{
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
        await using var microphone = factory.Create(AudioCaptureMode.Microphone);
        await using var loopback = factory.Create(AudioCaptureMode.SystemLoopback);

        Assert.Equal(AudioCaptureMode.Microphone, microphone.Mode);
        Assert.Equal(AudioCaptureMode.SystemLoopback, loopback.Mode);
        Assert.Equal(16_000, microphone.SampleRate);
        Assert.Equal(16_000, loopback.SampleRate);
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

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        var bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 2);
    }
}
