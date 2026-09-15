using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Fixes the hand-written part of the Application Loopback interop: the exact
/// device path, the IID, the twelve-byte activation params and the x64
/// PROPVARIANT layout that carries them.
/// </summary>
public sealed class ProcessLoopbackInteropTests
{
    [Fact]
    public void Activation_targets_the_process_loopback_virtual_device_and_i_audio_client()
    {
        Assert.Equal(@"VAD\Process_Loopback", ProcessLoopbackInterop.VirtualAudioDeviceProcessLoopback);
        Assert.Equal(
            new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
            ProcessLoopbackInterop.AudioClientInterfaceId);
        Assert.Equal(1, ProcessLoopbackInterop.ActivationTypeProcessLoopback);
        Assert.Equal(0, ProcessLoopbackInterop.ProcessLoopbackModeIncludeTargetProcessTree);
        Assert.Equal(65, ProcessLoopbackInterop.VariantTypeBlob);
    }

    [Fact]
    public void Activation_params_match_the_native_layout()
    {
        // AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS is two DWORDs and
        // AUDIOCLIENT_ACTIVATION_PARAMS adds the four-byte activation type, so
        // the flattened pair is twelve bytes with four-byte alignment.
        Assert.Equal(8, Marshal.SizeOf<AudioClientProcessLoopbackParams>());
        Assert.Equal(12, Marshal.SizeOf<AudioClientActivationParams>());
        Assert.Equal(0, Marshal.OffsetOf<AudioClientActivationParams>(
            nameof(AudioClientActivationParams.ActivationType)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<AudioClientActivationParams>(
            nameof(AudioClientActivationParams.ProcessLoopbackParams)).ToInt32());
    }

    [Fact]
    public void Blob_propvariant_matches_the_native_layout()
    {
        // PROPVARIANT is 24 bytes on x64 (type plus six reserved bytes, then a
        // sixteen-byte union whose BLOB member is a DWORD and an eight-byte
        // aligned pointer) and 16 bytes on x86.
        var is64Bit = IntPtr.Size == 8;
        Assert.Equal(is64Bit ? 24 : 16, Marshal.SizeOf<BlobPropVariant>());
        Assert.Equal(0, Marshal.OffsetOf<BlobPropVariant>(nameof(BlobPropVariant.VariantType)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<BlobPropVariant>(nameof(BlobPropVariant.BlobSize)).ToInt32());
        Assert.Equal(is64Bit ? 16 : 12, Marshal.OffsetOf<BlobPropVariant>(nameof(BlobPropVariant.BlobData)).ToInt32());
    }

    [Fact]
    public void Activation_params_blob_carries_the_included_target_process_tree()
    {
        using var activationParams = new ProcessLoopbackActivationParams(4_242);

        var variant = Marshal.PtrToStructure<BlobPropVariant>(activationParams.Pointer);
        Assert.Equal(ProcessLoopbackInterop.VariantTypeBlob, variant.VariantType);
        Assert.Equal((uint)Marshal.SizeOf<AudioClientActivationParams>(), variant.BlobSize);
        Assert.Equal(activationParams.Pointer + Marshal.SizeOf<BlobPropVariant>(), variant.BlobData);

        var parameters = Marshal.PtrToStructure<AudioClientActivationParams>(variant.BlobData);
        Assert.Equal(ProcessLoopbackInterop.ActivationTypeProcessLoopback, parameters.ActivationType);
        Assert.Equal(4_242, parameters.ProcessLoopbackParams.TargetProcessId);
        Assert.Equal(
            ProcessLoopbackInterop.ProcessLoopbackModeIncludeTargetProcessTree,
            parameters.ProcessLoopbackParams.ProcessLoopbackMode);
    }

    [Fact]
    public void Activation_params_survive_a_detach_and_release_exactly_once()
    {
        var activationParams = new ProcessLoopbackActivationParams(4_242);
        var pointer = activationParams.Pointer;
        Assert.NotEqual(IntPtr.Zero, pointer);

        // A cancelled activation hands the blob to the completion callback;
        // neither side may free it twice or free it too early.
        Assert.Equal(pointer, activationParams.Detach());
        Assert.Equal(IntPtr.Zero, activationParams.Pointer);
        Assert.Equal(IntPtr.Zero, activationParams.Detach());
        activationParams.Dispose();
        Assert.Equal(IntPtr.Zero, activationParams.Pointer);

        Marshal.FreeHGlobal(pointer);
    }

    [Fact]
    public void An_abandoned_activation_blob_is_released_by_the_callback()
    {
        // Cancelling an activation must not free the blob: Windows may still be
        // reading it, so the completion callback owns the release. This pins the
        // handoff in both orders - abandoned first and completed first - because
        // either one releasing it twice would be a double free.
        var handler = new ProcessLoopbackActivationHandler();
        try
        {
            var activationParams = new ProcessLoopbackActivationParams(4_242);
            handler.TakeOwnership(activationParams.Detach());
            activationParams.Dispose();

            handler.ActivateCompleted(null!);
            Assert.IsType<ArgumentNullException>(handler.Failure);
        }
        finally
        {
            handler.Close();
        }

        var completedFirst = new ProcessLoopbackActivationHandler();
        try
        {
            completedFirst.ActivateCompleted(null!);
            Assert.IsType<ArgumentNullException>(completedFirst.Failure);

            var activationParams = new ProcessLoopbackActivationParams(4_242);
            completedFirst.TakeOwnership(activationParams.Detach());
            activationParams.Dispose();
        }
        finally
        {
            completedFirst.Close();
        }
    }

    [Fact]
    public void Describe_names_the_hrresults_the_capture_loop_reacts_to()
    {
        Assert.Equal("DeviceInvalidated", ProcessLoopbackFault.Describe(unchecked((int)0x88890004)));
        Assert.Equal("ResourcesInvalidated", ProcessLoopbackFault.Describe(unchecked((int)0x88890026)));
        Assert.Equal("ServiceNotRunning", ProcessLoopbackFault.Describe(unchecked((int)0x88890010)));
        Assert.Equal("0x80004001", ProcessLoopbackFault.Describe(unchecked((int)0x80004001)));

        var fault = ProcessLoopbackFault.Create("进程回环激活失败", unchecked((int)0x88890004));
        Assert.Equal(unchecked((int)0x88890004), fault.HResult);
        Assert.Contains("DeviceInvalidated", fault.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The capability gate: a build below 20348 must be answered with "unsupported"
/// before any COM call, and a supported build must get a real process capture
/// rather than a silent system loopback downgrade.
/// </summary>
public sealed class ProcessLoopbackCapabilityTests
{
    [Fact]
    public async Task Factory_creates_a_process_capture_when_the_build_supports_it()
    {
        var factory = new WindowsAudioCaptureFactory(new StubProcessLoopbackSupport(isSupported: true, build: 26_200));

        await using var capture = factory.Create(AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(4_242)));

        Assert.Equal(AudioCaptureSourceKind.ProcessLoopback, capture.SourceKind);
        Assert.Equal(AudioCaptureMode.SystemLoopback, capture.Mode);
        Assert.Equal(16_000, capture.SampleRate);
        Assert.Equal(4_242, Assert.IsType<ProcessLoopbackAudioCapture>(capture).Target.ProcessId);
    }

    [Fact]
    public void Factory_refuses_process_loopback_below_the_minimum_build()
    {
        var factory = new WindowsAudioCaptureFactory(new StubProcessLoopbackSupport(isSupported: false, build: 19_045));

        var exception = Assert.Throws<ProcessLoopbackNotSupportedException>(
            () => factory.Create(AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(4_242))));

        Assert.Contains(
            WindowsProcessLoopbackSupport.MinimumBuild.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("19045", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Factory_keeps_microphone_and_system_loopback_on_an_unsupported_build()
    {
        var factory = new WindowsAudioCaptureFactory(new StubProcessLoopbackSupport(isSupported: false, build: 19_045));

        await using var microphone = factory.Create(AudioCaptureRequest.Microphone());
        await using var loopback = factory.Create(AudioCaptureRequest.SystemLoopback());

        Assert.Equal(AudioCaptureSourceKind.Microphone, microphone.SourceKind);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, loopback.SourceKind);
    }

    [Fact]
    public async Task Factory_never_downgrades_a_supported_process_request_to_system_audio()
    {
        var factory = new WindowsAudioCaptureFactory(new StubProcessLoopbackSupport(isSupported: true, build: 20_348));

        await using var capture = factory.Create(AudioCaptureRequest.ProcessLoopback(new ProcessIdentity(7)));

        Assert.NotEqual(AudioCaptureSourceKind.SystemLoopback, capture.SourceKind);
        Assert.NotEqual(AudioCaptureSourceKind.SystemLoopbackFallback, capture.SourceKind);
    }

    [Fact]
    public void Windows_support_reads_the_real_operating_system_build()
    {
        var support = WindowsProcessLoopbackSupport.Instance;

        Assert.Equal(20_348, WindowsProcessLoopbackSupport.MinimumBuild);
        Assert.Equal(OperatingSystem.IsWindows(), support.Build is not null);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Environment.OSVersion.Version.Build, support.Build);
            Assert.Equal(
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, WindowsProcessLoopbackSupport.MinimumBuild),
                support.IsSupported);
        }
        else
        {
            Assert.False(support.IsSupported);
        }
    }

    [Fact]
    public void DescribeBuild_explains_an_unreadable_build()
    {
        Assert.Equal("19045", WindowsProcessLoopbackSupport.DescribeBuild(19_045));
        Assert.Equal("未知", WindowsProcessLoopbackSupport.DescribeBuild(null));
    }
}
