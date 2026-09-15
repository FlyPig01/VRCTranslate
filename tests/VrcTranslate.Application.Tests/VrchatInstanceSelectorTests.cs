using VrcTranslate.Application.Abstractions;
using VrcTranslate.Application.Speech;
using Xunit;

namespace VrcTranslate.Application.Tests;

/// <summary>
/// The multi-instance decision table of the design document. The policy must be
/// provable without a machine that happens to run two VRChat copies, and it must
/// not depend on the order a process scan returns.
/// </summary>
public sealed class VrchatInstanceSelectorTests
{
    private const int CurrentSession = 1;
    private static readonly DateTimeOffset Origin = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_instance_selects_nothing()
    {
        Assert.Null(VrchatInstanceSelector.Select([], CurrentSession, null));
        Assert.Null(VrchatInstanceSelector.Select(null, CurrentSession, null));
    }

    [Fact]
    public void Only_the_interactive_session_is_considered()
    {
        var result = VrchatInstanceSelector.Select([Candidate(100, sessionId: 2)], CurrentSession, null);

        Assert.Null(result);
    }

    [Fact]
    public void An_instance_without_a_readable_session_is_not_adopted()
    {
        var result = VrchatInstanceSelector.Select([Candidate(100, sessionId: null)], CurrentSession, null);

        Assert.Null(result);
    }

    [Fact]
    public void An_unknown_current_session_does_not_filter_candidates()
    {
        var result = VrchatInstanceSelector.Select([Candidate(100, 5, sessionId: null)], currentSessionId: null, null);

        Assert.Equal(100, result!.ProcessId);
    }

    [Fact]
    public void The_foreground_instance_wins_over_an_earlier_one()
    {
        var result = VrchatInstanceSelector.Select(
            [Candidate(100, 0), Candidate(200, 5, foreground: true)],
            CurrentSession,
            null);

        Assert.Equal(200, result!.ProcessId);
    }

    [Fact]
    public void The_foreground_instance_also_wins_over_the_current_one()
    {
        var result = VrchatInstanceSelector.Select(
            [Candidate(100, 0), Candidate(200, 5, foreground: true)],
            CurrentSession,
            Identity(100, 0));

        Assert.Equal(200, result!.ProcessId);
    }

    [Fact]
    public void The_current_instance_is_kept_while_it_is_alive()
    {
        var result = VrchatInstanceSelector.Select(
            [Candidate(100, 0), Candidate(200, 5)],
            CurrentSession,
            Identity(200, 5));

        Assert.Equal(200, result!.ProcessId);
    }

    [Fact]
    public void The_earliest_instance_is_chosen_after_the_current_one_exited()
    {
        var result = VrchatInstanceSelector.Select(
            [Candidate(300, 30), Candidate(100, 10)],
            CurrentSession,
            Identity(999, 0));

        Assert.Equal(100, result!.ProcessId);
    }

    [Fact]
    public void An_unreadable_start_time_falls_back_to_the_process_id()
    {
        // A readable start time proves age; an unreadable one cannot, so it sorts last.
        var withReadable = VrchatInstanceSelector.Select(
            [UnknownStart(500), Candidate(400, 20)],
            CurrentSession,
            null);
        Assert.Equal(400, withReadable!.ProcessId);

        // With no readable start time at all the process id is the stable order.
        var noneReadable = VrchatInstanceSelector.Select(
            [UnknownStart(500), UnknownStart(300)],
            CurrentSession,
            null);
        Assert.Equal(300, noneReadable!.ProcessId);
    }

    [Fact]
    public void A_reused_process_id_with_a_new_start_time_is_a_new_target()
    {
        var previous = Identity(100, 0);

        var result = VrchatInstanceSelector.Select(
            [Candidate(100, 60), Candidate(200, 90)],
            CurrentSession,
            previous);

        Assert.Equal(100, result!.ProcessId);
        Assert.Equal(Origin.AddSeconds(60), result.StartTimeUtc);
        Assert.False(previous.Matches(result));
    }

    private static ProcessIdentity Identity(int processId, int startedSecondsAfterOrigin = 0) =>
        new(processId, Origin.AddSeconds(startedSecondsAfterOrigin));

    private static VrchatProcessCandidate Candidate(
        int processId,
        int startedSecondsAfterOrigin = 0,
        int? sessionId = CurrentSession,
        bool foreground = false) =>
        new(Identity(processId, startedSecondsAfterOrigin), sessionId, foreground);

    private static VrchatProcessCandidate UnknownStart(int processId) =>
        new(new ProcessIdentity(processId), CurrentSession, false);
}
