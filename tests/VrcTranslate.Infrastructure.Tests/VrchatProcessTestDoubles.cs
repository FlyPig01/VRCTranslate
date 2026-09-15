using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using VrcTranslate.Infrastructure.Speech;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

/// <summary>
/// Process source whose scan answers are scripted one by one; the last answer
/// repeats. The watcher policy is proven here instead of on a machine that
/// happens to run VRChat.
/// </summary>
internal sealed class ScriptedVrchatProcessSource : IVrchatProcessSource
{
    private readonly object _sync = new();
    private readonly List<Func<VrchatProcessScan>> _answers = [];
    private int _scanCount;

    public int ScanCount
    {
        get { lock (_sync) return _scanCount; }
    }

    /// <summary>Queues one scan answer.</summary>
    public ScriptedVrchatProcessSource Returns(VrchatProcessScan scan)
    {
        lock (_sync) _answers.Add(() => scan);
        return this;
    }

    /// <summary>Queues one scan that fails, for example with an access denial.</summary>
    public ScriptedVrchatProcessSource Throws(Exception failure)
    {
        lock (_sync) _answers.Add(() => throw failure);
        return this;
    }

    public VrchatProcessScan Scan()
    {
        Func<VrchatProcessScan> answer;
        lock (_sync)
        {
            _scanCount++;
            answer = _answers.Count == 0
                ? static () => VrchatProcessScan.None
                : _answers[Math.Min(_scanCount - 1, _answers.Count - 1)];
        }

        return answer();
    }
}

/// <summary>Delay hook the test releases by hand, so 3 seconds and 300 ms cost nothing.</summary>
internal sealed class ScriptedDelay
{
    private readonly object _sync = new();
    private readonly List<TimeSpan> _requested = [];
    private readonly List<TaskCompletionSource> _pending = [];
    private int _cancelled;

    public IReadOnlyList<TimeSpan> Requested
    {
        get { lock (_sync) return [.. _requested]; }
    }

    /// <summary>The delay requested last; the loop never has two outstanding.</summary>
    public TimeSpan LastRequested
    {
        get { lock (_sync) return _requested.Count == 0 ? TimeSpan.Zero : _requested[^1]; }
    }

    public int WaitingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    /// <summary>How many waits the cancellation token ended, i.e. how often stopping worked.</summary>
    public int CancelledCount => Volatile.Read(ref _cancelled);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _requested.Add(delay);
            _pending.Add(completion);
        }

        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.Register(() =>
            {
                if (completion.TrySetCanceled(cancellationToken)) Interlocked.Increment(ref _cancelled);
            });
        }

        return completion.Task;
    }

    /// <summary>Releases the oldest waiting delay so the loop continues.</summary>
    public bool ReleaseNext()
    {
        TaskCompletionSource? next;
        lock (_sync)
        {
            if (_pending.Count == 0) return false;
            next = _pending[0];
            _pending.RemoveAt(0);
        }

        return next.TrySetResult();
    }
}

/// <summary>The polling policy under test, wired to a scripted source and sink.</summary>
internal sealed class WatcherHarness : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMilliseconds(300);

    private readonly List<ProcessIdentity?> _published = [];

    public WatcherHarness(
        ScriptedVrchatProcessSource source,
        Func<ProcessIdentity?, CancellationToken, Task>? sink = null)
    {
        Source = source;
        Delay = new ScriptedDelay();
        Watcher = new VrchatProcessWatcher(
            new VrchatProcessResolver(source),
            sink ?? RecordAsync,
            new VrchatProcessWatcherOptions
            {
                PollInterval = PollInterval,
                ConfirmationDelay = ConfirmationDelay,
                DelayAsync = Delay.DelayAsync,
            });
    }

    public ScriptedVrchatProcessSource Source { get; }

    public ScriptedDelay Delay { get; }

    public VrchatProcessWatcher Watcher { get; }

    /// <summary>Every target the watcher published, in order.</summary>
    public IReadOnlyList<ProcessIdentity?> Published
    {
        get { lock (_published) return [.. _published]; }
    }

    public int PublishedCount
    {
        get { lock (_published) return _published.Count; }
    }

    /// <summary>Waits for the pending poll delay and releases it, so the next scan runs now.</summary>
    public async Task ReleasePollAsync()
    {
        await ProcessTestWait.UntilAsync(
            () => Delay.WaitingCount > 0 && Delay.LastRequested == PollInterval,
            "监视器未进入 3 秒轮询等待").ConfigureAwait(false);
        Assert.True(Delay.ReleaseNext());
    }

    /// <summary>Waits for the pending confirmation delay and releases it.</summary>
    public async Task ReleaseConfirmationAsync()
    {
        await ProcessTestWait.UntilAsync(
            () => Delay.WaitingCount > 0 && Delay.LastRequested == ConfirmationDelay,
            "监视器未请求复核延时").ConfigureAwait(false);
        Assert.True(Delay.ReleaseNext());
    }

    public ValueTask DisposeAsync() => Watcher.DisposeAsync();

    private Task RecordAsync(ProcessIdentity? target, CancellationToken cancellationToken)
    {
        lock (_published) _published.Add(target);
        return Task.CompletedTask;
    }
}

/// <summary>Polling helper for state a background loop changes asynchronously.</summary>
internal static class ProcessTestWait
{
    public static async Task UntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.True(condition(), message);
    }
}

/// <summary>Capture whose start result is scripted; no audio device is touched.</summary>
internal sealed class FakeLoopbackCapture : IAudioCapture
{
    public FakeLoopbackCapture(AudioCaptureRequest request, Exception? startFailure = null)
    {
        Request = request;
        StartFailure = startFailure;
    }

    public AudioCaptureRequest Request { get; }

    public Exception? StartFailure { get; set; }

    public AudioCaptureMode Mode => Request.Mode;

    public AudioCaptureSourceKind SourceKind => Request.SourceKind;

    public int SampleRate => 16_000;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsStarted { get; private set; }

#pragma warning disable CS0067 // The fake only has to satisfy the capture contract; it never delivers audio.
    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (StartFailure is not null) throw StartFailure;
        IsStarted = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        IsStarted = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsStarted = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out <see cref="FakeLoopbackCapture"/> instances and can refuse process loopback.</summary>
internal sealed class FakeLoopbackCaptureFactory : IAudioCaptureFactory
{
    private readonly object _sync = new();
    private readonly List<FakeLoopbackCapture> _created = [];
    private readonly HashSet<AudioCaptureSourceKind> _unsupported = [];

    public IReadOnlyList<FakeLoopbackCapture> Created
    {
        get { lock (_sync) return [.. _created]; }
    }

    public int ProcessAttempts => Created.Count(capture => capture.Request.SourceKind == AudioCaptureSourceKind.ProcessLoopback);

    /// <summary>Refuses a source the way the Windows factory refuses process loopback today.</summary>
    public void MarkUnsupported(AudioCaptureSourceKind kind)
    {
        lock (_sync) _unsupported.Add(kind);
    }

    public IAudioCapture Create(AudioCaptureRequest request)
    {
        lock (_sync)
        {
            if (_unsupported.Contains(request.SourceKind))
            {
                throw new ProcessLoopbackNotSupportedException("测试：当前系统不支持进程回环。");
            }

            var capture = new FakeLoopbackCapture(request);
            _created.Add(capture);
            return capture;
        }
    }
}
