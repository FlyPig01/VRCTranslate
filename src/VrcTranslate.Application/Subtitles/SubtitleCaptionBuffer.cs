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
/// One recognized sentence as a single message: the translation, the recognized
/// line it came from, and the speaker label when speaker separation is on. The
/// two text parts are never split into separate messages.
/// </summary>
public sealed record SubtitleCaption
{
    public SubtitleCaption(string? original, string? translated, string? speakerLabel = null)
    {
        Original = original?.Trim() ?? string.Empty;
        Translated = translated?.Trim() ?? string.Empty;
        SpeakerLabel = string.IsNullOrWhiteSpace(speakerLabel) ? null : speakerLabel.Trim();
    }

    /// <summary>Recognized text without the speaker prefix.</summary>
    public string Original { get; }

    /// <summary>Simplified Chinese translation for <see cref="Original"/>.</summary>
    public string Translated { get; }

    /// <summary>Name of the separated speaker, or null when speaker labels are off.</summary>
    public string? SpeakerLabel { get; }

    public bool HasSpeaker => SpeakerLabel is not null;

    /// <summary>A caption without any text carries no message at all.</summary>
    public bool IsEmpty => Translated.Length == 0 && Original.Length == 0;
}

/// <summary>One rendered line of a caption message.</summary>
/// <param name="Text">Visible text.</param>
/// <param name="IsOriginal">True for the recognized line, false for the translation.</param>
public readonly record struct SubtitleCaptionLine(string Text, bool IsOriginal);

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
    /// Projects one message into the lines the overlay renders. "译文 + 原文"
    /// adds the recognized line below the translation; "仅译文" keeps the
    /// translation alone. A caption whose translation is missing still shows the
    /// recognized line instead of an empty message.
    /// </summary>
    public static IReadOnlyList<SubtitleCaptionLine> Present(SubtitleCaption caption, SubtitleContentMode mode)
    {
        ArgumentNullException.ThrowIfNull(caption);
        var lines = new List<SubtitleCaptionLine>(2);
        if (caption.Translated.Length > 0)
        {
            lines.Add(new SubtitleCaptionLine(caption.Translated, IsOriginal: false));
        }

        if (mode == SubtitleContentMode.TranslatedWithOriginal && caption.Original.Length > 0)
        {
            lines.Add(new SubtitleCaptionLine(caption.Original, IsOriginal: true));
        }

        if (lines.Count == 0 && caption.Original.Length > 0)
        {
            lines.Add(new SubtitleCaptionLine(caption.Original, IsOriginal: true));
        }

        return lines;
    }
}
