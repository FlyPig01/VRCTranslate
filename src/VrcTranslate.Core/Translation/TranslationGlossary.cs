namespace VrcTranslate.Core.Translation;

/// <summary>One user or bundled glossary term.</summary>
public sealed record GlossaryEntry(string Source, string Target);

/// <summary>The glossary document persisted as data/glossary.json.</summary>
public sealed class GlossaryDocument
{
    public bool Enabled { get; set; } = true;

    public List<GlossaryEntry> Entries { get; set; } = [];
}

/// <summary>A thread-safe snapshot of the active glossary.</summary>
public sealed record TranslationGlossary(bool Enabled, IReadOnlyList<GlossaryEntry> Entries)
{
    public static TranslationGlossary Empty { get; } = new(false, []);

    public bool HasTerms => Enabled && Entries.Count > 0;
}

/// <summary>One line of recent dialogue, used only to disambiguate the current sentence.</summary>
public sealed record TranslationContextLine(string Speaker, string Text);

/// <summary>
/// Extra, optional information for the translation provider. Machine-translation
/// adapters deliberately ignore this; the LLM prompt is the only consumer.
/// </summary>
public sealed record TranslationHints(
    IReadOnlyList<GlossaryEntry>? Glossary = null,
    IReadOnlyList<TranslationContextLine>? Context = null)
{
    /// <summary>Hints for the current glossary, or null when it is off/empty.</summary>
    public static TranslationHints? ForGlossary(TranslationGlossary? glossary) =>
        glossary is { HasTerms: true }
            ? new TranslationHints(glossary.Entries)
            : null;
}
