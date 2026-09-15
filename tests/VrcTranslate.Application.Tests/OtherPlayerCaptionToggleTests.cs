using VrcTranslate.Application.Subtitles;
using Xunit;

namespace VrcTranslate.Application.Tests;

public sealed class OtherPlayerCaptionToggleTests
{
    [Fact]
    public async Task Enabling_starts_recognition_and_shows_the_caption_surface()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        var outcome = await toggle.SetEnabledAsync(true);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Started, outcome);
        // Recognition must exist before the surface: a window that nothing can
        // fill is the failure mode this order prevents.
        Assert.Equal(["start", "show"], endpoint.Journal);
        Assert.True(endpoint.IsRunning);
        Assert.True(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task Disabling_stops_recognition_and_hides_the_caption_surface()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);
        await toggle.SetEnabledAsync(true);

        var outcome = await toggle.SetEnabledAsync(false);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Stopped, outcome);
        Assert.Equal(["start", "show", "stop", "hide"], endpoint.Journal);
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task The_shortcut_flips_the_whole_feature_between_on_and_off()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Started, await toggle.ToggleAsync());
        Assert.True(endpoint.IsRunning);
        Assert.True(endpoint.IsSurfaceVisible);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Stopped, await toggle.ToggleAsync());
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);

        Assert.Equal(["start", "show", "stop", "hide"], endpoint.Journal);
    }

    [Fact]
    public async Task A_missing_model_is_refused_without_touching_recognition_or_the_window()
    {
        var endpoint = new RecordingCaptionEndpoint { IsModelReady = false };
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        // Every press must report the refusal, so the caller can show the hint
        // again instead of looking broken.
        Assert.Equal(OtherPlayerCaptionToggleOutcome.ModelNotReady, await toggle.ToggleAsync());
        Assert.Equal(OtherPlayerCaptionToggleOutcome.ModelNotReady, await toggle.SetEnabledAsync(true));

        Assert.Empty(endpoint.Journal);
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task A_repeated_enable_keeps_the_one_running_recognition_session()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        await toggle.SetEnabledAsync(true);
        var outcome = await toggle.SetEnabledAsync(true);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Started, outcome);
        Assert.Equal(1, endpoint.Journal.Count(operation => operation == "start"));
        Assert.True(endpoint.IsRunning);
        Assert.True(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task A_failed_capture_start_never_shows_the_surface()
    {
        var endpoint = new RecordingCaptionEndpoint { FailStart = true };
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        await Assert.ThrowsAsync<InvalidOperationException>(() => toggle.SetEnabledAsync(true));

        Assert.Equal(["start"], endpoint.Journal);
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task A_failed_stop_still_takes_the_surface_down()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);
        await toggle.SetEnabledAsync(true);
        endpoint.FailStop = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => toggle.SetEnabledAsync(false));

        // Stopping may fail, but the window must never stay up pretending that
        // recognition is still alive.
        Assert.Equal(["start", "show", "stop", "hide"], endpoint.Journal);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task A_leftover_surface_is_taken_down_by_the_next_stop()
    {
        var endpoint = new RecordingCaptionEndpoint();
        endpoint.ForceSurfaceVisible();
        var toggle = new OtherPlayerCaptionToggle(endpoint);

        var outcome = await toggle.SetEnabledAsync(false);

        Assert.Equal(OtherPlayerCaptionToggleOutcome.Stopped, outcome);
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    [Fact]
    public async Task Racing_presses_never_leave_recognition_and_the_surface_disagreeing()
    {
        var endpoint = new RecordingCaptionEndpoint();
        var toggle = new OtherPlayerCaptionToggle(endpoint);
        var settled = new System.Collections.Concurrent.ConcurrentBag<(bool Running, bool Visible)>();

        var presses = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await toggle.ToggleAsync();
                // Whatever an outside observer sees once a press has finished
                // must be one of the two settled states of the feature.
                settled.Add((endpoint.IsRunning, endpoint.IsSurfaceVisible));
            }))
            .ToArray();
        await Task.WhenAll(presses);

        Assert.False(endpoint.OverlappedOperations);
        Assert.All(settled, snapshot => Assert.Equal(snapshot.Running, snapshot.Visible));
        Assert.DoesNotContain(settled, snapshot => snapshot.Visible && !snapshot.Running);
        Assert.DoesNotContain(settled, snapshot => snapshot.Running && !snapshot.Visible);

        // Four presses turned the feature on and four turned it off, and every
        // transition stayed whole: two presses can never interleave into
        // "start, start, show, show".
        var expected = new List<string>();
        for (var index = 0; index < 4; index++) expected.AddRange(["start", "show", "stop", "hide"]);
        Assert.Equal(expected, endpoint.Journal);
        Assert.False(endpoint.IsRunning);
        Assert.False(endpoint.IsSurfaceVisible);
    }

    /// <summary>
    /// Records every effect the master switch applies and reports an overlap if a
    /// transition ever runs while another one is still in flight - which is what
    /// an unserialized implementation would do under racing presses.
    /// </summary>
    private sealed class RecordingCaptionEndpoint : IOtherPlayerCaptionEndpoint
    {
        private readonly object _journalSync = new();
        private readonly List<string> _journal = [];
        private int _operationsInFlight;

        public bool IsRunning { get; private set; }

        public bool IsSurfaceVisible { get; private set; }

        public bool IsModelReady { get; init; } = true;

        public bool FailStart { get; init; }

        public bool FailStop { get; set; }

        public bool OverlappedOperations { get; private set; }

        public IReadOnlyList<string> Journal
        {
            get { lock (_journalSync) return _journal.ToArray(); }
        }

        /// <summary>Simulates a surface left behind by an earlier, unpaired transition.</summary>
        public void ForceSurfaceVisible() => IsSurfaceVisible = true;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Enter("start");
            try
            {
                // A real capture start touches audio devices; the delay keeps the
                // concurrency assertion meaningful for a broken implementation.
                await Task.Delay(2, cancellationToken);
                if (FailStart) throw new InvalidOperationException("capture start failed");
                IsRunning = true;
            }
            finally
            {
                Exit();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Enter("stop");
            try
            {
                await Task.Delay(2, cancellationToken);
                IsRunning = false;
                if (FailStop) throw new InvalidOperationException("capture stop failed");
            }
            finally
            {
                Exit();
            }
        }

        public void ShowSurface()
        {
            Enter("show");
            try
            {
                IsSurfaceVisible = true;
            }
            finally
            {
                Exit();
            }
        }

        public void HideSurface()
        {
            Enter("hide");
            try
            {
                IsSurfaceVisible = false;
            }
            finally
            {
                Exit();
            }
        }

        private void Enter(string operation)
        {
            if (Interlocked.Increment(ref _operationsInFlight) != 1)
            {
                OverlappedOperations = true;
            }

            lock (_journalSync)
            {
                _journal.Add(operation);
            }
        }

        private void Exit() => Interlocked.Decrement(ref _operationsInFlight);
    }
}
