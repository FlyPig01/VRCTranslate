using VrcTranslate.Application.Abstractions;
using Xunit;

namespace VrcTranslate.Application.Tests;

/// <summary>
/// Capture whose start result and events are scripted by the test. Together with
/// <see cref="FakeAudioCaptureFactory"/> it makes the coordinator's switching
/// observable without any audio hardware.
/// </summary>
internal sealed class FakeAudioCapture : IAudioCapture
{
    private readonly TaskCompletionSource? _startGate;

    public FakeAudioCapture(
        AudioCaptureRequest request,
        Exception? startFailure = null,
        TaskCompletionSource? startGate = null)
    {
        Request = request;
        StartFailure = startFailure;
        _startGate = startGate;
    }

    public AudioCaptureRequest Request { get; }

    /// <summary>When set, <see cref="StartAsync"/> throws it after the optional gate.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>When false a gated start ignores cancellation, which models a slow activation.</summary>
    public bool HonorsCancellationDuringStart { get; set; }

    public AudioCaptureMode Mode => Request.Mode;

    public AudioCaptureSourceKind SourceKind => Request.SourceKind;

    public int SampleRate => 16_000;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsStarted { get; private set; }

    public List<AudioSourceState> SourceChanges { get; } = [];

    public event EventHandler<AudioSamplesEventArgs>? SamplesReady;

    public event EventHandler<AudioSourceChangedEventArgs>? SourceChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public event EventHandler<AudioCaptureStoppedEventArgs>? Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (_startGate is not null)
        {
            if (HonorsCancellationDuringStart)
            {
                await _startGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _startGate.Task.ConfigureAwait(false);
            }
        }

        if (StartFailure is not null) throw StartFailure;
        IsStarted = true;
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

    public void Emit(float[] samples, int sampleRate = 16_000) =>
        SamplesReady?.Invoke(this, new AudioSamplesEventArgs(samples, sampleRate));

    public void RaiseFault(Exception exception) =>
        Faulted?.Invoke(this, new AudioCaptureFaultedEventArgs(SourceKind, exception));

    public void RaiseStopped(Exception? exception = null) =>
        Stopped?.Invoke(this, new AudioCaptureStoppedEventArgs(SourceKind, exception));

    public void RaiseSourceChanged(AudioSourceState state, long generation = 0, bool isBoundary = true)
    {
        SourceChanges.Add(state);
        SourceChanged?.Invoke(this, new AudioSourceChangedEventArgs(state, generation, isBoundary));
    }
}

/// <summary>Hands out <see cref="FakeAudioCapture"/> instances and records every request.</summary>
internal sealed class FakeAudioCaptureFactory : IAudioCaptureFactory
{
    private readonly object _sync = new();
    private readonly List<FakeAudioCapture> _created = [];
    private readonly Dictionary<AudioCaptureSourceKind, Exception> _failures = [];
    private readonly HashSet<AudioCaptureSourceKind> _unsupported = [];
    private TaskCompletionSource? _nextStartGate;

    public IReadOnlyList<FakeAudioCapture> Created
    {
        get { lock (_sync) return [.. _created]; }
    }

    public int ProcessAttempts => Created.Count(capture => capture.Request.SourceKind == AudioCaptureSourceKind.ProcessLoopback);

    public FakeAudioCapture Last => Created[^1];

    /// <summary>Makes every later start of this kind fail with the given exception.</summary>
    public void FailKind(AudioCaptureSourceKind kind, Exception failure)
    {
        lock (_sync) _failures[kind] = failure;
    }

    /// <summary>Makes every later start of this kind report an unsupported platform.</summary>
    public void MarkUnsupported(AudioCaptureSourceKind kind)
    {
        lock (_sync) _unsupported.Add(kind);
    }

    /// <summary>Lets later starts of this kind succeed again.</summary>
    public void HealKind(AudioCaptureSourceKind kind)
    {
        lock (_sync)
        {
            _failures.Remove(kind);
            _unsupported.Remove(kind);
        }
    }

    /// <summary>Blocks the next created capture inside StartAsync until the gate completes.</summary>
    public void GateNextStart(TaskCompletionSource gate)
    {
        lock (_sync) _nextStartGate = gate;
    }

    public IAudioCapture Create(AudioCaptureRequest request)
    {
        TaskCompletionSource? gate;
        Exception? failure;
        lock (_sync)
        {
            gate = _nextStartGate;
            _nextStartGate = null;
            failure = _unsupported.Contains(request.SourceKind)
                ? new ProcessLoopbackNotSupportedException("测试：当前系统不支持进程回环。")
                : _failures.TryGetValue(request.SourceKind, out var planned) ? planned : null;
        }

        var capture = new FakeAudioCapture(request, failure, gate);
        lock (_sync) _created.Add(capture);
        return capture;
    }
}

/// <summary>Backoff delay the test releases by hand, so 30 seconds cost nothing.</summary>
internal sealed class RecordingDelay
{
    private readonly object _sync = new();
    private readonly List<TimeSpan> _requested = [];
    private readonly List<TaskCompletionSource> _pending = [];

    public IReadOnlyList<TimeSpan> Requested
    {
        get { lock (_sync) return [.. _requested]; }
    }

    public int WaitingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

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
            _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        return completion.Task;
    }

    /// <summary>Releases the oldest waiting backoff so the retry runs immediately.</summary>
    public bool CompleteNext()
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

/// <summary>Polling helper for state that a background retry changes asynchronously.</summary>
internal static class TestWait
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
