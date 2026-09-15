namespace VrcTranslate.Application.Subtitles;

/// <summary>
/// How much of a caption is shown on the subtitle surface. The default keeps the
/// recognized line together with its translation, which is what earlier builds
/// always rendered.
/// </summary>
public enum SubtitleContentMode
{
    /// <summary>Only the Simplified Chinese translation is shown.</summary>
    TranslatedOnly = 0,

    /// <summary>The translation is shown with the recognized line below it.</summary>
    TranslatedWithOriginal = 1,
}

/// <summary>
/// Where one message's translation stands on the progressive caption path.
/// Recognition and translation run at very different speeds (roughly 140 ms
/// against 900 ms), so a message is published as soon as it is recognized and is
/// completed later instead of waiting for its translation.
/// </summary>
public enum SubtitleTranslationState
{
    /// <summary>Recognition produced the message; its translation is still being produced.</summary>
    Pending = 0,

    /// <summary>A non-empty translation arrived and fills the message.</summary>
    Ready = 1,

    /// <summary>The translation failed or came back empty, so none will arrive.</summary>
    Unavailable = 2,
}

/// <summary>
/// One recognized sentence as a single message: the translation, the recognized
/// line it came from, and the speaker label when speaker separation is on. The
/// two text parts are never split into separate messages, and a message whose
/// translation is still on its way is the very same message that the translation
/// later fills through <see cref="SubtitleCaptionBuffer.TrySetTranslation"/>.
/// </summary>
public sealed record SubtitleCaption
{
    /// <summary>
    /// One finished message: the recognized line together with the translation
    /// that came with it. An empty translation resolves the message as
    /// unavailable, which keeps the recognized line visible instead of leaving an
    /// empty message behind.
    /// </summary>
    /// <param name="id">
    /// Correlation id used to fill this message's translation later. Zero means
    /// the message is complete on arrival and is never filled.
    /// </param>
    public SubtitleCaption(string? original, string? translated, string? speakerLabel = null, long id = 0)
        : this(
            Normalize(original),
            Normalize(translated),
            NormalizeSpeaker(speakerLabel),
            id,
            Normalize(translated).Length > 0 ? SubtitleTranslationState.Ready : SubtitleTranslationState.Unavailable)
    {
    }

    private SubtitleCaption(
        string original,
        string translated,
        string? speakerLabel,
        long id,
        SubtitleTranslationState translationState)
    {
        Original = original;
        Translated = translated;
        SpeakerLabel = speakerLabel;
        Id = id;
        TranslationState = translationState;
    }

    /// <summary>
    /// One message that recognition produced on its own: the recognized line and
    /// the speaker label are already final while the translation is filled into
    /// this same message when it arrives.
    /// </summary>
    public static SubtitleCaption Pending(string? original, string? speakerLabel = null, long id = 0) =>
        new(Normalize(original), string.Empty, NormalizeSpeaker(speakerLabel), id, SubtitleTranslationState.Pending);

    /// <summary>Recognized text without the speaker prefix.</summary>
    public string Original { get; }

    /// <summary>Simplified Chinese translation for <see cref="Original"/>.</summary>
    public string Translated { get; }

    /// <summary>Name of the separated speaker, or null when speaker labels are off.</summary>
    public string? SpeakerLabel { get; }

    /// <summary>
    /// Correlation id of this message. The caption surface hands it back with the
    /// translation so the two form one message instead of two.
    /// </summary>
    public long Id { get; }

    /// <summary>Whether the translation is still missing, arrived, or never will.</summary>
    public SubtitleTranslationState TranslationState { get; }

    public bool HasSpeaker => SpeakerLabel is not null;

    /// <summary>A caption without any text carries no message at all.</summary>
    public bool IsEmpty => Translated.Length == 0 && Original.Length == 0;

    /// <summary>
    /// Resolves this message with the translation that arrived. A null or empty
    /// translation marks the message unavailable rather than leaving it waiting
    /// forever; the recognized line stays on its own either way.
    /// </summary>
    public SubtitleCaption WithTranslation(string? translated)
    {
        var text = Normalize(translated);
        return new SubtitleCaption(
            Original,
            text,
            SpeakerLabel,
            Id,
            text.Length > 0 ? SubtitleTranslationState.Ready : SubtitleTranslationState.Unavailable);
    }

    private static string Normalize(string? text) => text?.Trim() ?? string.Empty;

    private static string? NormalizeSpeaker(string? speakerLabel) =>
        string.IsNullOrWhiteSpace(speakerLabel) ? null : speakerLabel.Trim();
}

/// <summary>One rendered line of a caption message.</summary>
/// <param name="Text">Visible text.</param>
/// <param name="IsOriginal">True for the recognized line, false for the translation.</param>
/// <param name="TranslationState">
/// Where the message's translation stands. A line that stands in for a
/// translation which has not arrived yet - or never will - keeps
/// <see cref="SubtitleTranslationState.Pending"/> or
/// <see cref="SubtitleTranslationState.Unavailable"/> so the surface can present
/// it as the recognized line and never as a finished translation.
/// </param>
public readonly record struct SubtitleCaptionLine(
    string Text,
    bool IsOriginal,
    SubtitleTranslationState TranslationState = SubtitleTranslationState.Ready);

/// <summary>
/// Bounded, in-memory message log behind the subtitle overlay. It exists for the
/// current run only: nothing here is written to disk, and there is no user-facing
/// clear action. A paused log stops appending but keeps every existing message,
/// so stopping recognition and starting it again continues the same conversation.
/// Public members are UI-thread affine; the internal lock only guards against a
/// stray marshalling mistake.
/// </summary>
public sealed class SubtitleCaptionBuffer
{
    /// <summary>Newest messages kept before the oldest are dropped.</summary>
    public const int DefaultCapacity = 200;

    private readonly object _sync = new();
    private readonly List<SubtitleCaption> _captions;

    public SubtitleCaptionBuffer(
        int capacity = DefaultCapacity,
        SubtitleContentMode contentMode = SubtitleContentMode.TranslatedWithOriginal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        ContentMode = contentMode;
        _captions = new List<SubtitleCaption>(Math.Min(capacity, 64));
    }

    /// <summary>Hard limit on retained messages; older ones are discarded.</summary>
    public int Capacity { get; }

    public SubtitleContentMode ContentMode { get; private set; }

    /// <summary>True while recognition is stopped; appends are dropped, nothing is cleared.</summary>
    public bool IsPaused { get; private set; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _captions.Count;
            }
        }
    }

    /// <summary>Messages discarded because the log reached <see cref="Capacity"/>.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Snapshot in arrival order, oldest first.</summary>
    public IReadOnlyList<SubtitleCaption> Captions
    {
        get
        {
            lock (_sync)
            {
                return _captions.ToArray();
            }
        }
    }

    public bool Append(string? original, string? translated, string? speakerLabel = null) =>
        Append(new SubtitleCaption(original, translated, speakerLabel));

    /// <summary>
    /// Adds one message and evicts the oldest messages beyond the cap.
    /// Returns false when the log is paused or the caption has no text.
    /// A pending caption counts as a message: its recognized line is already
    /// there, and its translation is filled into the same entry later.
    /// </summary>
    public bool Append(SubtitleCaption caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        lock (_sync)
        {
            if (IsPaused || caption.IsEmpty)
            {
                return false;
            }

            _captions.Add(caption);
            while (_captions.Count > Capacity)
            {
                _captions.RemoveAt(0);
                DroppedCount++;
            }

            return true;
        }
    }

    /// <summary>
    /// Fills the translation into the pending message with this id. No message is
    /// added, removed or reordered: the recognized line that is already on screen
    /// becomes the same message's translation. A null or empty translation
    /// resolves the message as <see cref="SubtitleTranslationState.Unavailable"/>,
    /// so a failed or empty translation leaves the recognized line standing
    /// instead of a message that waits forever.
    /// </summary>
    /// <returns>
    /// False when no pending message carries the id - because the bounded log
    /// already dropped it, because it was resolved before, or because the id is
    /// not a caption id at all. A translation that is already filled is never
    /// overwritten, and a message that is still pending is completed even while
    /// the log is paused: the pause stops new messages, not the completion of a
    /// message the reader can already see.
    /// </returns>
    public bool TrySetTranslation(long id, string? translated)
    {
        if (id <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            for (var index = 0; index < _captions.Count; index++)
            {
                var caption = _captions[index];
                if (caption.Id != id) continue;
                if (caption.TranslationState != SubtitleTranslationState.Pending) return false;

                _captions[index] = caption.WithTranslation(translated);
                return true;
            }

            return false;
        }
    }

    /// <summary>Reads one retained message by its correlation id.</summary>
    public bool TryGetCaption(long id, out SubtitleCaption caption)
    {
        lock (_sync)
        {
            foreach (var candidate in _captions)
            {
                if (candidate.Id != id) continue;
                caption = candidate;
                return true;
            }
        }

        caption = null!;
        return false;
    }

    /// <summary>Stops appending. Existing messages stay exactly as they are.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            IsPaused = true;
        }
    }

    /// <summary>Continues appending after the existing messages.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            IsPaused = false;
        }
    }

    public void SetContentMode(SubtitleContentMode mode)
    {
        lock (_sync)
        {
            ContentMode = mode;
        }
    }

    /// <summary>Projects one message with the current <see cref="ContentMode"/>.</summary>
    public IReadOnlyList<SubtitleCaptionLine> Present(SubtitleCaption caption) =>
        Present(caption, ContentMode);

    /// <summary>
    /// Projects one message into the lines the overlay renders. The order is
    /// fixed for every source: the translation first, the recognized line below
    /// it. "仅译文" keeps the translation alone while "译文 + 原文" shows both.
    /// A message whose translation has not arrived yet - or never will - shows
    /// the recognized line instead, also in "仅译文", so the message that
    /// recognition published is never an empty placeholder.
    /// </summary>
    public static IReadOnlyList<SubtitleCaptionLine> Present(SubtitleCaption caption, SubtitleContentMode mode)
    {
        ArgumentNullException.ThrowIfNull(caption);
        var lines = new List<SubtitleCaptionLine>(2);
        if (caption.Translated.Length > 0)
        {
            lines.Add(new SubtitleCaptionLine(caption.Translated, IsOriginal: false));
            if (mode == SubtitleContentMode.TranslatedWithOriginal && caption.Original.Length > 0)
            {
                lines.Add(new SubtitleCaptionLine(caption.Original, IsOriginal: true));
            }

            return lines;
        }

        if (caption.Original.Length > 0)
        {
            lines.Add(new SubtitleCaptionLine(caption.Original, IsOriginal: true, caption.TranslationState));
        }

        return lines;
    }
}
