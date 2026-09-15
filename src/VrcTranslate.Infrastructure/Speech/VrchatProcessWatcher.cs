using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Speech;

/// <summary>Polling policy of <see cref="VrchatProcessWatcher"/>.</summary>
public sealed class VrchatProcessWatcherOptions
{
    /// <summary>Rhythm of the scans that follow the immediate one.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Recheck delay in front of a changed decision. A launcher that hands over to
    /// the real client, or a restart that briefly leaves two processes behind,
    /// settles here instead of switching the capture twice.
    /// </summary>
    public TimeSpan ConfirmationDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Delay hook; tests replace it so three seconds cost nothing.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public static VrchatProcessWatcherOptions Default { get; } = new();
}

/// <summary>
/// Watches VRChat and pushes every decision into the adaptive capture
/// coordinator (its <c>ApplyTargetAsync</c>). The first scan runs immediately,
/// then once per <see cref="VrchatProcessWatcherOptions.PollInterval"/>; a changed
/// decision is confirmed by one short recheck, so only a stable identity reaches
/// the coordinator. Stopping cancels and awaits the loop, which leaves no polling
/// task behind when the application closes.
/// </summary>
public sealed class VrchatProcessWatcher : IAsyncDisposable
{
    private readonly VrchatProcessResolver _resolver;
    private readonly Func<ProcessIdentity?, CancellationToken, Task> _applyTarget;
    private readonly VrchatProcessWatcherOptions _options;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task _loop = Task.CompletedTask;
    private ProcessIdentity? _published;
    private bool _disposed;

    public VrchatProcessWatcher(
        VrchatProcessResolver resolver,
        Func<ProcessIdentity?, CancellationToken, Task> applyTargetAsync,
        VrchatProcessWatcherOptions? options = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _applyTarget = applyTargetAsync ?? throw new ArgumentNullException(nameof(applyTargetAsync));
        _options = options ?? VrchatProcessWatcherOptions.Default;
    }

    /// <summary>The identity of the last published decision; null while VRChat is away.</summary>
    public ProcessIdentity? Current
    {
        get { lock (_sync) return _published; }
    }

    public bool IsRunning
    {
        get { lock (_sync) return _lifetime is not null; }
    }

    /// <summary>
    /// Starts the loop. The first scan runs right away instead of waiting for the
    /// poll interval; calling it again while the loop runs changes nothing.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VrchatProcessWatcher));
            if (_lifetime is not null) return;
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _loop = RunAsync(lifetime.Token);
        }
    }

    /// <summary>
    /// Cancels the loop and waits for it, so no scan or coordinator call is still
    /// in flight when this returns. Stopping a watcher that never ran does nothing.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        Task loop;
        lock (_sync)
        {
            lifetime = _lifetime;
            _lifetime = null;
            loop = _loop;
        }

        if (lifetime is not null)
        {
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifetime?.Dispose();
        }
    }

    /// <summary>Stops the loop and releases it; disposing twice is harmless.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The scan rhythm: one immediate scan, then one per poll interval. Every
    /// failure is contained here, because a single refused scan must never end
    /// the monitoring for the rest of the session.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndPublishAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Access denied, a process that exited mid-scan, or a call the
                // coordinator refused: the next poll sees the real state.
            }

            try
            {
                await _options.DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A broken delay hook would spin the loop, so it ends instead.
                return;
            }
        }
    }

    private async Task ScanAndPublishAsync(CancellationToken cancellationToken)
    {
        var current = Current;
        var decision = _resolver.Resolve(current);
        if (IsSameTarget(decision, current)) return;

        // 候选变化先复核一次：启动器交棒、进程重启的中间态和残留进程都会在这里
        // 被吸收，协调器不会因为一次抖动来回切换。
        if (_options.ConfirmationDelay > TimeSpan.Zero)
        {
            await _options.DelayAsync(_options.ConfirmationDelay, cancellationToken).ConfigureAwait(false);
        }

        decision = _resolver.Resolve(current);
        if (IsSameTarget(decision, current)) return;

        // 只发布复核后的结果，旧决策的异步结果不可能覆盖新决策。
        await _applyTarget(decision, cancellationToken).ConfigureAwait(false);
        lock (_sync) _published = decision;
    }

    /// <summary>
    /// Publishing identity: a reused process id with a different start time is a
    /// restarted process, so it never inherits the previous decision.
    /// </summary>
    private static bool IsSameTarget(ProcessIdentity? left, ProcessIdentity? right) =>
        left is null ? right is null : left.Matches(right);
}
