using VrcTranslate.Application.Abstractions;
using Xunit;

namespace VrcTranslate.Application.Tests;

/// <summary>The platform-independent audio contract introduced for source switching.</summary>
public sealed class AudioCaptureContractsTests
{
    [Fact]
    public void A_process_identity_requires_a_real_process_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessIdentity(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessIdentity(-4));
    }

    [Fact]
    public void A_process_identity_matches_only_the_same_process()
    {
        var start = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(new ProcessIdentity(100, start).Matches(new ProcessIdentity(100, start)));
        Assert.False(new ProcessIdentity(100, start).Matches(new ProcessIdentity(101, start)));
        Assert.False(new ProcessIdentity(100, start).Matches(null));
    }

    [Fact]
    public void A_reused_process_id_with_a_new_start_time_does_not_match()
    {
        var start = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.False(new ProcessIdentity(100, start).Matches(new ProcessIdentity(100, start.AddSeconds(30))));
    }

    [Fact]
    public void An_unreadable_start_time_falls_back_to_the_process_id()
    {
        var start = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(new ProcessIdentity(100).Matches(new ProcessIdentity(100, start)));
        Assert.True(new ProcessIdentity(100, start).Matches(new ProcessIdentity(100)));
        Assert.False(new ProcessIdentity(100).Matches(new ProcessIdentity(101, start)));
    }

    [Fact]
    public void Process_loopback_requests_require_a_target()
    {
        Assert.Throws<ArgumentException>(() => new AudioCaptureRequest(AudioCaptureSourceKind.ProcessLoopback));
        Assert.Throws<ArgumentNullException>(() => AudioCaptureRequest.ProcessLoopback(null!));

        var request = AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(42));
        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, request.SourceKind);
        Assert.Equal(42, request.TargetProcess!.ProcessId);
    }

    [Fact]
    public void Capture_requests_derive_the_legacy_mode()
    {
        Assert.Equal(AudioCaptureMode.Microphone, AudioCaptureRequest.Microphone("default").Mode);
        Assert.Equal(AudioCaptureMode.SystemLoopback, AudioCaptureRequest.SystemLoopback().Mode);
        Assert.Equal(AudioCaptureMode.SystemLoopback, AudioCaptureRequest.SystemLoopbackFallback().Mode);
        Assert.Equal(AudioCaptureMode.SystemLoopback, AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(7)).Mode);
        Assert.Equal("mic-1", AudioCaptureRequest.Microphone("mic-1").MicrophoneDeviceId);
    }

    [Fact]
    public void Only_the_fallback_source_reports_compatibility_mode()
    {
        Assert.True(new AudioSourceState(AudioCaptureSourceKind.SystemLoopbackFallback).IsCompatibilityMode);
        Assert.False(new AudioSourceState(AudioCaptureSourceKind.SystemLoopback).IsCompatibilityMode);
        Assert.False(new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, new ProcessIdentity(9)).IsCompatibilityMode);
    }

    [Fact]
    public void Source_changes_carry_the_generation_and_the_boundary_flag()
    {
        var args = new AudioSourceChangedEventArgs(
            new AudioSourceState(AudioCaptureSourceKind.ProcessLoopback, new ProcessIdentity(9)),
            generation: 7,
            isBoundary: true);

        Assert.Equal(7, args.Generation);
        Assert.True(args.IsBoundary);
        Assert.Equal(9, args.State.ProcessIdentity!.ProcessId);
    }

    [Fact]
    public void Fault_events_require_an_exception_and_keep_the_source_kind()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AudioCaptureFaultedEventArgs(AudioCaptureSourceKind.SystemLoopback, null!));
        Assert.Throws<ArgumentNullException>(
            () => new AudioSourceChangedEventArgs(null!, 1, isBoundary: true));

        var args = new AudioCaptureFaultedEventArgs(
            AudioCaptureSourceKind.ProcessLoopback,
            new InvalidOperationException("boom"),
            "0x88890004",
            new ProcessIdentity(11));

        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, args.SourceKind);
        Assert.Equal("0x88890004", args.FaultCode);
        Assert.Equal(11, args.ProcessIdentity!.ProcessId);
    }
}
