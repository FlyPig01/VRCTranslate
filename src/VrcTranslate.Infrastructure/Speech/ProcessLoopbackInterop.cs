using System.Runtime.InteropServices;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>
/// The complete COM/Win32 surface Windows Application Loopback needs on top of
/// NAudio. NAudio 2.2.1 already publishes the two activation interfaces
/// (<see cref="IActivateAudioInterfaceCompletionHandler"/> and
/// <see cref="IActivateAudioInterfaceAsyncOperation"/>) and the WASAPI client
/// wrappers, so this file only adds the activation entry point, the activation
/// parameter blob and the PROPVARIANT that carries it. No IAudioClient member is
/// declared twice.
/// </summary>
internal static class ProcessLoopbackInterop
{
    /// <summary>
    /// Device interface path that activates a process-tree loopback stream
    /// (<c>VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK</c>).
    /// </summary>
    public const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    /// <summary><c>VT_BLOB</c>: the PROPVARIANT carries a counted byte blob.</summary>
    public const ushort VariantTypeBlob = 65;

    /// <summary><c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>.</summary>
    public const int ActivationTypeProcessLoopback = 1;

    /// <summary><c>PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE</c>.</summary>
    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    /// <summary>
    /// IID of the WASAPI <c>IAudioClient</c> the activation must return. It is
    /// taken from NAudio's own declaration so the two can never drift apart.
    /// </summary>
    public static Guid AudioClientInterfaceId => typeof(IAudioClient).GUID;

    // Mmdevapi!ActivateAudioInterfaceAsync. Guid is passed by value because the
    // native parameter is REFIID, which the x64 ABI also passes as a pointer to
    // the sixteen bytes. PreserveSig keeps the immediate HRESULT visible instead
    // of turning it into an exception, which is what the caller has to report.
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation? activationOperation);

    /// <summary>
    /// Starts one process-loopback activation. The immediate HRESULT is returned
    /// unchanged, so a failure is reported instead of thrown; a failed immediate
    /// result also means no completion callback will ever arrive.
    /// </summary>
    public static int Activate(
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation? activationOperation) =>
        ActivateAudioInterfaceAsync(
            VirtualAudioDeviceProcessLoopback,
            AudioClientInterfaceId,
            activationParams,
            completionHandler,
            out activationOperation);
}

/// <summary>
/// <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c>. Both members are four-byte
/// values, so the structure is eight bytes on every architecture.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    /// <summary>Process whose render streams (and those of its children) are included.</summary>
    public int TargetProcessId;

    /// <summary><c>PROCESS_LOOPBACK_MODE</c>: include or exclude the target tree.</summary>
    public int ProcessLoopbackMode;
}

/// <summary>
/// <c>AUDIOCLIENT_ACTIVATION_PARAMS</c>. The native type is an activation type
/// followed by a union; only the process-loopback member exists here and every
/// member is four-byte aligned, so the flattened pair matches the native
/// twelve-byte layout on both x86 and x64.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    /// <summary><c>AUDIOCLIENT_ACTIVATION_TYPE</c>.</summary>
    public int ActivationType;

    /// <summary>The union member selected by <see cref="ActivationType"/>.</summary>
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

/// <summary>
/// The <c>VT_BLOB</c> shape of a <c>PROPVARIANT</c>. On x64 the native type is
/// two bytes of variant type, six reserved bytes and a sixteen-byte union whose
/// BLOB member is a four-byte size and an eight-byte-aligned pointer; sequential
/// layout with the default pack produces exactly that (24 bytes, pointer at
/// offset 16). On x86 the same declaration yields the native 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BlobPropVariant
{
    /// <summary><c>vt</c>, <see cref="ProcessLoopbackInterop.VariantTypeBlob"/> for this use.</summary>
    public ushort VariantType;

    /// <summary><c>wReserved1</c>, unused.</summary>
    public ushort Reserved1;

    /// <summary><c>wReserved2</c>, unused.</summary>
    public ushort Reserved2;

    /// <summary><c>wReserved3</c>, unused.</summary>
    public ushort Reserved3;

    /// <summary><c>blob.cbSize</c>: size of the bytes <see cref="BlobData"/> points at.</summary>
    public uint BlobSize;

    /// <summary><c>blob.pBlobData</c>: unmanaged pointer to the activation params.</summary>
    public IntPtr BlobData;
}

/// <summary>
/// Owns the unmanaged PROPVARIANT and the activation params blob it points at.
/// Windows reads the blob while the asynchronous activation runs, so the memory
/// is never released while a callback is still outstanding: a caller that gives
/// up hands the pointer to <see cref="ProcessLoopbackActivationHandler"/>, which
/// releases it when the callback arrives.
/// </summary>
internal sealed class ProcessLoopbackActivationParams : IDisposable
{
    private IntPtr _memory;

    public ProcessLoopbackActivationParams(int targetProcessId)
    {
        var variantSize = Marshal.SizeOf<BlobPropVariant>();
        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        _memory = Marshal.AllocHGlobal(variantSize + paramsSize);
        var paramsPointer = _memory + variantSize;
        Marshal.StructureToPtr(
            new AudioClientActivationParams
            {
                ActivationType = ProcessLoopbackInterop.ActivationTypeProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = targetProcessId,
                    ProcessLoopbackMode = ProcessLoopbackInterop.ProcessLoopbackModeIncludeTargetProcessTree
                }
            },
            paramsPointer,
            fDeleteOld: false);
        Marshal.StructureToPtr(
            new BlobPropVariant
            {
                VariantType = ProcessLoopbackInterop.VariantTypeBlob,
                BlobSize = (uint)paramsSize,
                BlobData = paramsPointer
            },
            _memory,
            fDeleteOld: false);
    }

    /// <summary>Pointer to the PROPVARIANT handed to <c>ActivateAudioInterfaceAsync</c>.</summary>
    public IntPtr Pointer => _memory;

    /// <summary>
    /// Gives up ownership without freeing. Used when the activation is abandoned
    /// while Windows may still be reading the blob.
    /// </summary>
    public IntPtr Detach() => Interlocked.Exchange(ref _memory, IntPtr.Zero);

    /// <summary>Releases the blob. Safe to call repeatedly and after <see cref="Detach"/>.</summary>
    public void Dispose()
    {
        var memory = Interlocked.Exchange(ref _memory, IntPtr.Zero);
        if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
    }
}

/// <summary>
/// Receives the result of one asynchronous activation. Windows calls
/// <see cref="ActivateCompleted"/> from a worker thread in the process MTA and
/// keeps a reference to this object until the operation completes, so the
/// callback can never arrive after the handler was collected. The handler is
/// created on the capture thread, which is already in the MTA, so the callback
/// needs no marshaler.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ProcessLoopbackActivationHandler : IActivateAudioInterfaceCompletionHandler
{
    private readonly ManualResetEventSlim _completed = new(false);
    private IntPtr _ownedActivationParams;

    /// <summary>HRESULT reported by <c>GetActivateResult</c>; E_FAIL until reported.</summary>
    public int ActivateResult { get; private set; } = unchecked((int)0x80004005);

    /// <summary>The activated WASAPI object, or null when the activation failed.</summary>
    public object? ActivatedInterface { get; private set; }

    /// <summary>Set when the callback itself failed; the capture reports it as a fault.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>True once <see cref="ActivateCompleted"/> returned.</summary>
    public bool IsCompleted => _completed.IsSet;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(activateOperation);
            activateOperation.GetActivateResult(out var activateResult, out var activateInterface);
            ActivateResult = activateResult;
            ActivatedInterface = activateInterface;
        }
        catch (Exception exception)
        {
            // A managed callback must never let an exception escape into Windows.
            Failure = exception;
        }
        finally
        {
            ReleaseOwnedActivationParams();
            try
            {
                _completed.Set();
            }
            catch (ObjectDisposedException)
            {
                // The caller already closed the handler after abandoning the wait.
            }
        }
    }

    /// <summary>
    /// The completion event. It is a handle rather than a blocking wait because
    /// an STA attempt has to keep its message queue served while it waits for
    /// the callback.
    /// </summary>
    public WaitHandle CompletionHandle => _completed.WaitHandle;

    /// <summary>
    /// Takes over an activation blob the caller abandons. Windows may still be
    /// reading it, so it is deliberately not released here: the completion
    /// callback releases it instead. The one case that has no callback left is
    /// an activation that already completed, and then the blob is reclaimed
    /// immediately so nothing leaks.
    /// </summary>
    public void TakeOwnership(IntPtr activationParams)
    {
        if (activationParams == IntPtr.Zero) return;
        Interlocked.Exchange(ref _ownedActivationParams, activationParams);
        if (IsCompleted) ReleaseOwnedActivationParams();
    }

    /// <summary>Releases the completion event once nobody waits on it any more.</summary>
    public void Close() => _completed.Dispose();

    private void ReleaseOwnedActivationParams()
    {
        var pointer = Interlocked.Exchange(ref _ownedActivationParams, IntPtr.Zero);
        if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
    }
}

/// <summary>
/// What a failed activation means for the next attempt. Only three answers
/// matter: the COM environment of this machine refused the apartment (retry
/// once in the other one), the failure cannot ever succeed here (fuse process
/// loopback for the session), or the situation can change (let the coordinator's
/// backoff own the retry).
/// </summary>
internal enum ProcessLoopbackFaultKind
{
    /// <summary>A device or timing failure: retry under the coordinator's backoff.</summary>
    Transient,

    /// <summary>The apartment itself was refused: retry once in the other one.</summary>
    ApartmentEnvironment,

    /// <summary>Retrying cannot help: mark process loopback unsupported for this session.</summary>
    Permanent
}

/// <summary>Builds the exceptions a failed activation is reported with.</summary>
internal static class ProcessLoopbackFault
{
    /// <summary><c>E_NOTIMPL</c>: the virtual client answers this for device-only members.</summary>
    public const int NotImplemented = unchecked((int)0x80004001);

    /// <summary><c>E_NOINTERFACE</c>: the activation did not return the requested client.</summary>
    public const int NoInterface = unchecked((int)0x80004002);

    /// <summary><c>REGDB_E_CLASSNOTREG</c>: the application loopback server is not registered.</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary><c>CO_E_NOTINITIALIZED</c>: COM was never initialized on this thread.</summary>
    public const int NotInitialized = unchecked((int)0x800401F0);

    /// <summary><c>RPC_E_WRONG_THREAD</c>; the brief calls it E_ILLEGAL_METHOD_CALL.</summary>
    public const int WrongThread = unchecked((int)0x8001010E);

    /// <summary><c>RPC_E_CHANGED_MODE</c>: the thread already lives in another apartment.</summary>
    public const int ChangedMode = unchecked((int)0x80010106);

    /// <summary><c>E_ILLEGAL_METHOD_CALL</c>.</summary>
    public const int IllegalMethodCall = unchecked((int)0x8000000E);

    /// <summary><c>E_ACCESSDENIED</c>: policy or a device ACL refused the capture.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)</c>.</summary>
    public const int NotSupportedByPlatform = unchecked((int)0x80070032);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION)</c>: this Windows version cannot do it.</summary>
    public const int OldWindowsVersion = unchecked((int)0x8007047E);

    /// <summary>The HRESULT range <c>RPC_E_*</c> lives in.</summary>
    private const int RpcErrorMask = unchecked((int)0xFFFFFF00);
    private const int RpcErrorBase = unchecked((int)0x80010100);

    /// <summary>
    /// Names the HRESULT so a log line says <c>DeviceInvalidated</c> instead of
    /// an opaque number. NAudio publishes the codes as constants rather than as
    /// an enum, so the interesting ones are listed explicitly.
    /// </summary>
    public static string Describe(int hresult) => hresult switch
    {
        AudioClientErrorCode.NotInitialized => "NotInitialized",
        AudioClientErrorCode.DeviceInvalidated => "DeviceInvalidated",
        AudioClientErrorCode.NotStopped => "NotStopped",
        AudioClientErrorCode.UnsupportedFormat => "UnsupportedFormat",
        AudioClientErrorCode.InvalidSize => "InvalidSize",
        AudioClientErrorCode.DeviceInUse => "DeviceInUse",
        AudioClientErrorCode.ServiceNotRunning => "ServiceNotRunning",
        AudioClientErrorCode.EventHandleNotExpected => "EventHandleNotExpected",
        AudioClientErrorCode.EventHandleNotSet => "EventHandleNotSet",
        AudioClientErrorCode.IncorrectBufferSize => "IncorrectBufferSize",
        AudioClientErrorCode.BufferSizeError => "BufferSizeError",
        AudioClientErrorCode.CpuUsageExceeded => "CpuUsageExceeded",
        AudioClientErrorCode.BufferError => "BufferError",
        AudioClientErrorCode.BufferSizeNotAligned => "BufferSizeNotAligned",
        AudioClientErrorCode.InvalidDevicePeriod => "InvalidDevicePeriod",
        AudioClientErrorCode.InvalidStreamFlag => "InvalidStreamFlag",
        AudioClientErrorCode.ResourcesInvalidated => "ResourcesInvalidated",
        _ => $"0x{hresult:X8}"
    };

    /// <summary>An activation or capture failure carrying the failing HRESULT.</summary>
    public static COMException Create(string message, int hresult) =>
        new($"{message}（{Describe(hresult)}，HRESULT 0x{hresult:X8}）。", hresult);

    /// <summary>
    /// The apartment refused the activation or COM was not usable on this
    /// thread. These are the only HRESULTs that get a retry in the other
    /// apartment; anything else is either permanent or a device situation.
    /// </summary>
    public static bool IsApartmentEnvironment(int hresult) =>
        hresult is WrongThread or IllegalMethodCall or NotInitialized or AccessDenied or ChangedMode
        || (hresult & RpcErrorMask) == RpcErrorBase;

    /// <summary>
    /// HRESULTs that cannot succeed on a later attempt. E_ACCESSDENIED is listed
    /// here as well as in <see cref="IsApartmentEnvironment"/>: it earns one
    /// apartment retry, and if it comes back the machine is treated as unable to
    /// capture process audio at all.
    /// </summary>
    public static bool IsPermanent(int hresult) =>
        hresult is NotImplemented or NoInterface or ClassNotRegistered or AccessDenied
            or NotSupportedByPlatform or OldWindowsVersion;

    /// <summary>What a failed HRESULT means for the next attempt.</summary>
    public static ProcessLoopbackFaultKind ClassifyHResult(int hresult) =>
        IsApartmentEnvironment(hresult) ? ProcessLoopbackFaultKind.ApartmentEnvironment
        : IsPermanent(hresult) ? ProcessLoopbackFaultKind.Permanent
        : ProcessLoopbackFaultKind.Transient;

    /// <summary>What a failed attempt means for the next attempt.</summary>
    public static ProcessLoopbackFaultKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // A timeout says nothing about the machine's ability: the coordinator
            // backs off and retries instead of fusing process loopback.
            ProcessLoopbackActivationTimeoutException => ProcessLoopbackFaultKind.Transient,
            ProcessLoopbackNotSupportedException => ProcessLoopbackFaultKind.Permanent,
            COMException com => ClassifyHResult(com.HResult),
            _ => ProcessLoopbackFaultKind.Transient
        };
    }

    /// <summary>
    /// The stable code published through <c>AudioCaptureFaultedEventArgs</c> and
    /// written to the diagnostics log. A named HRESULT (DeviceInvalidated) is
    /// used when one exists so the log line is readable.
    /// </summary>
    public static string? CodeOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            ProcessLoopbackActivationTimeoutException => ProcessLoopbackActivationTimeoutException.ActivationTimeoutCode,
            OperationCanceledException => "Cancelled",
            ProcessLoopbackNotSupportedException => "NotSupported",
            COMException com => Describe(com.HResult),
            _ => null
        };
    }
}
