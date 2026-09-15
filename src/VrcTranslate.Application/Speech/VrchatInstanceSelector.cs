using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Application.Speech;

/// <summary>One VRChat process as reported by a single platform scan.</summary>
public sealed record VrchatProcessCandidate(
    ProcessIdentity Identity,
    int? SessionId,
    bool HasForegroundWindow);

/// <summary>
/// The VRChat instance selection policy as a pure function: no polling, no
/// platform types and no dependence on the accidental process order of the
/// machine it runs on. The Windows monitor feeds it one scan at a time and
/// publishes whatever identity comes back.
/// </summary>
public static class VrchatInstanceSelector
{
    /// <summary>
    /// Picks the instance the capture should follow.
    /// </summary>
    /// <param name="candidates">Every VRChat process of the last scan; may be empty.</param>
    /// <param name="currentSessionId">
    /// Session id of the interactive user, or null when the platform could not
    /// report it (then no session filtering happens).
    /// </param>
    /// <param name="currentTarget">The identity the capture currently follows, if any.</param>
    public static ProcessIdentity? Select(
        IReadOnlyList<VrchatProcessCandidate>? candidates,
        int? currentSessionId,
        ProcessIdentity? currentTarget)
    {
        if (candidates is null || candidates.Count == 0) return null;

        var scoped = ScopeToSession(candidates, currentSessionId);
        if (scoped.Count == 0) return null;

        // The instance the user is looking at wins: it is the one whose audio
        // matters, and a second copy running in the background must not steal it.
        var foreground = Oldest(scoped.Where(candidate => candidate.HasForegroundWindow));
        if (foreground is not null) return foreground.Identity;

        // No usable foreground information (the front window is not VRChat, or
        // the platform reported none). Keeping the current instance stops the
        // capture from churning every time the user alt-tabs away.
        if (currentTarget is not null)
        {
            var kept = scoped.FirstOrDefault(candidate => currentTarget.Matches(candidate.Identity));
            if (kept is not null) return kept.Identity;
        }

        // Otherwise the instance that started first is the stable choice. An
        // unreadable start time sorts last; the process id keeps the order
        // deterministic for the remaining ties.
        return Oldest(scoped)!.Identity;
    }

    /// <summary>
    /// Restricts the scan to the interactive user's session. A candidate whose
    /// session could not be read cannot be proven to belong to that user, so it
    /// is dropped: capturing the audio of another logged-in user is never the
    /// intended behaviour.
    /// </summary>
    private static List<VrchatProcessCandidate> ScopeToSession(
        IReadOnlyList<VrchatProcessCandidate> candidates,
        int? currentSessionId)
    {
        if (currentSessionId is null) return [.. candidates];
        return candidates.Where(candidate => candidate.SessionId == currentSessionId.Value).ToList();
    }

    private static VrchatProcessCandidate? Oldest(IEnumerable<VrchatProcessCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.Identity.StartTimeUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(candidate => candidate.Identity.ProcessId)
            .FirstOrDefault();
}
