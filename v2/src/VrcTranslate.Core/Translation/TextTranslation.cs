namespace VrcTranslate.Core.Translation;

public enum TextTranslationSource
{
    ManualText,
    SpeechRecognition,
    ExternalText
}

public sealed record TextTranslationRequest
{
    public TextTranslationRequest(
        string text,
        TranslationRoute route,
        TextTranslationSource source,
        DateTimeOffset? requestedAt = null,
        string? sourceLanguageHint = null,
        string? correlationId = null)
    {
        Text = string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("Text is required.", nameof(text))
            : text.Trim();
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Source = source;
        RequestedAt = requestedAt ?? DateTimeOffset.UtcNow;
        SourceLanguageHint = string.IsNullOrWhiteSpace(sourceLanguageHint) ? null : sourceLanguageHint.Trim();
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId.Trim();
    }

    public string Text { get; }
    public TranslationRoute Route { get; }
    public TextTranslationSource Source { get; }
    public DateTimeOffset RequestedAt { get; }
    public string? SourceLanguageHint { get; }
    public string CorrelationId { get; }
}

public sealed record TextTranslationResult
{
    public TextTranslationResult(
        TextTranslationRequest request,
        string translatedText,
        string? resolvedSourceLanguage,
        TimeSpan elapsed,
        InvariantValidationResult invariantValidation,
        DateTimeOffset? completedAt = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        TranslatedText = string.IsNullOrWhiteSpace(translatedText)
            ? throw new ArgumentException("Translated text is required.", nameof(translatedText))
            : translatedText.Trim();
        ResolvedSourceLanguage = resolvedSourceLanguage;
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        Elapsed = elapsed;
        InvariantValidation = invariantValidation ?? throw new ArgumentNullException(nameof(invariantValidation));
        CompletedAt = completedAt ?? DateTimeOffset.UtcNow;
    }

    public TextTranslationRequest Request { get; }
    public string OriginalText => Request.Text;
    public string TranslatedText { get; }
    public string? ResolvedSourceLanguage { get; }
    public TimeSpan Elapsed { get; }
    public InvariantValidationResult InvariantValidation { get; }
    public DateTimeOffset CompletedAt { get; }
}

/// <summary>Results for one source sentence translated to one or two outputs.</summary>
public sealed record TextTranslationBatchResult
{
    public TextTranslationBatchResult(
        TextTranslationRequest request,
        TranslationTargetSet targets,
        IReadOnlyList<TextTranslationResult> results)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        Results = results is null || results.Count == 0
            ? throw new ArgumentException("At least one translation result is required.", nameof(results))
            : results;

        if (Results.Count != Targets.Languages.Count)
        {
            throw new ArgumentException("The result count must match the target language count.", nameof(results));
        }
    }

    public TextTranslationRequest Request { get; }

    public TranslationTargetSet Targets { get; }

    public IReadOnlyList<TextTranslationResult> Results { get; }

    public TextTranslationResult Primary => Results[0];

    public TextTranslationResult? Secondary => Results.Count > 1 ? Results[1] : null;

    public string CombinedText => string.Join(Environment.NewLine, Results.Select(result => result.TranslatedText));

    /// <summary>
    /// Compact text shared by the own-input preview and VRChat Chatbox.
    /// </summary>
    public string FormattedText => TranslationOutputFormatter.Format(this);
}
