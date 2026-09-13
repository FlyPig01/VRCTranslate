namespace VrcTranslate.Core.Translation;

public sealed record TranslationRetryPolicy
{
    public TranslationRetryPolicy(int maxRetries = 2, TimeSpan? requestTimeout = null)
    {
        if (maxRetries is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Retries must be between 0 and 10.");
        }

        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "Timeout must be between 1 second and 5 minutes.");
        }

        MaxRetries = maxRetries;
        RequestTimeout = timeout;
    }

    public int MaxRetries { get; }
    public TimeSpan RequestTimeout { get; }
}

/// <summary>One translation configuration consumed by manual text and speech results.</summary>
public sealed record TranslationRoute
{
    public TranslationRoute(
        string routeId,
        string displayName,
        TranslationProfile profile,
        LanguagePolicy languagePolicy,
        InvariantProtectionPolicy? invariantPolicy = null,
        TranslationRetryPolicy? retryPolicy = null)
    {
        RouteId = Required(routeId, nameof(routeId));
        DisplayName = Required(displayName, nameof(displayName));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        LanguagePolicy = languagePolicy ?? throw new ArgumentNullException(nameof(languagePolicy));
        InvariantPolicy = invariantPolicy ?? InvariantProtectionPolicy.Default;
        RetryPolicy = retryPolicy ?? new TranslationRetryPolicy();

        if (languagePolicy.SourceMode == SourceLanguageMode.AutoDetect && !profile.SupportsAutoDetection)
        {
            throw new ArgumentException("The selected profile does not support automatic source detection.", nameof(languagePolicy));
        }
    }

    public string RouteId { get; }
    public string DisplayName { get; }
    public TranslationProfile Profile { get; }
    public LanguagePolicy LanguagePolicy { get; }
    public InvariantProtectionPolicy InvariantPolicy { get; }
    public TranslationRetryPolicy RetryPolicy { get; }

    /// <summary>
    /// Creates a route for one output language while preserving the selected
    /// service, source policy, invariant policy, and retry policy.
    /// </summary>
    public TranslationRoute ForTargetLanguage(string targetLanguage) => new(
        RouteId,
        DisplayName,
        Profile,
        new LanguagePolicy(
            LanguagePolicy.SourceMode,
            targetLanguage,
            LanguagePolicy.SourceMode == SourceLanguageMode.Fixed
                ? LanguagePolicy.SourceLanguage
                : null),
        InvariantPolicy,
        RetryPolicy);

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
