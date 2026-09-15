namespace VrcTranslate.Application.Subtitles;

/// <summary>Result of one press of the 他人语音 → 字幕 master switch.</summary>
public enum OtherPlayerCaptionToggleOutcome
{
    /// <summary>Recognition is running and the caption surface is on screen.</summary>
    Started,

    /// <summary>Recognition stopped and the caption surface was taken off screen.</summary>
    Stopped,

    /// <summary>Refused because the local speech model is not ready; nothing was touched.</summary>
    ModelNotReady
}

/// <summary>
/// The two effects the other-player caption switch drives: the recognition
/// session and the caption surface. The desktop shell implements them; the
/// toggle owns the order in which they are applied.
/// </summary>
public interface IOtherPlayerCaptionEndpoint
{
    /// <summary>Whether the other-player recognition session is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Whether the caption surface is currently on screen.</summary>
    bool IsSurfaceVisible { get; }

    /// <summary>Whether the local speech model can recognize anything yet.</summary>
    bool IsModelReady { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    void ShowSurface();

    void HideSurface();
}

/// <summary>
/// 他人语音 → 字幕 master switch: one shortcut (and one start/stop action on the
/// voice page) turns other-player recognition and its caption surface on and off
/// together. Recognition and the window are two effects of a single user
/// decision, so every transition - enable, disable, and a refused start - runs on
/// this one serialized path. That is what keeps the window from being hidden
/// while recognition keeps running (or the reverse): a competing press waits for
/// the one in flight, and each transition ends with both effects in the state
/// the caller asked for.
/// </summary>
public sealed class OtherPlayerCaptionToggle
{
    private readonly IOtherPlayerCaptionEndpoint _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OtherPlayerCaptionToggle(IOtherPlayerCaptionEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>
    /// Applies one absolute state. Asking for the state the feature is already in
    /// is a no-op instead of a restart, so a repeated shortcut press cannot
    /// interrupt the running capture session.
    /// </summary>
    public Task<OtherPlayerCaptionToggleOutcome> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(enabled, cancellationToken);

    /// <summary>
    /// Flips the feature; used by the global shortcut. The direction is decided
    /// inside the gate, so two presses can never read the same state and run the
    /// same transition twice.
    /// </summary>
    public Task<OtherPlayerCaptionToggleOutcome> ToggleAsync(CancellationToken cancellationToken = default) =>
        TransitionAsync(enabled: null, cancellationToken);

    private async Task<OtherPlayerCaptionToggleOutcome> TransitionAsync(
        bool? enabled,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = enabled ?? !_endpoint.IsRunning;
            return target
                ? await EnableAsync(cancellationToken).ConfigureAwait(false)
                : await DisableAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OtherPlayerCaptionToggleOutcome> EnableAsync(CancellationToken cancellationToken)
    {
        if (_endpoint.IsRunning)
        {
            // Recognition outlives page navigation, so a session can already be
            // running while the surface is missing. Showing it is the whole
            // transition; the capture must not be restarted.
            _endpoint.ShowSurface();
            return OtherPlayerCaptionToggleOutcome.Started;
        }

        if (!_endpoint.IsModelReady)
        {
            // Neither effect is applied. The caller owns the one visible notice:
            // a shortcut that cannot work must never look like it did nothing.
            return OtherPlayerCaptionToggleOutcome.ModelNotReady;
        }

        // Recognition first: a capture that fails to start must leave the
        // surface hidden rather than showing a window nothing will ever fill.
        await _endpoint.StartAsync(cancellationToken).ConfigureAwait(false);
        _endpoint.ShowSurface();
        return OtherPlayerCaptionToggleOutcome.Started;
    }

    private async Task<OtherPlayerCaptionToggleOutcome> DisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _endpoint.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The surface always goes down with the session, even when stopping
            // reports a failure: a hidden window is recoverable, a window that
            // lies about a dead recognition session is not.
            _endpoint.HideSurface();
        }

        return OtherPlayerCaptionToggleOutcome.Stopped;
    }
}
