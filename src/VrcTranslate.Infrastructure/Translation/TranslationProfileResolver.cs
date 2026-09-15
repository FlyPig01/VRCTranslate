namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Turns the translation profiles written by an older build into the set this
/// build can actually serve.
/// <para>
/// A profile that names a provider this build no longer ships - DeepL, the free
/// Google endpoint, Google Cloud - cannot translate anything. It is dropped
/// instead of being offered as a broken service, the remaining profiles stay
/// intact, and the runtime route falls back to a profile that still resolves, so
/// an old configuration degrades gracefully rather than failing the page.
/// </para>
/// </summary>
public static class TranslationProfileResolver
{
    /// <summary>
    /// Keeps the saved profiles this build can serve: a provider id that is
    /// registered by the host and an absolute http(s) endpoint. Profiles of
    /// removed providers fail the registration test and disappear here, which is
    /// the graceful downgrade for a configuration saved before the removal.
    /// </summary>
    public static IReadOnlyList<TranslationProfileRecord> RetainServable(
        IEnumerable<TranslationProfileRecord>? saved,
        IReadOnlyCollection<string> registeredProviderIds)
    {
        ArgumentNullException.ThrowIfNull(registeredProviderIds);
        if (saved is null) return [];

        var retained = new List<TranslationProfileRecord>();
        foreach (var profile in saved)
        {
            if (profile is null) continue;
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Provider)) continue;
            if (!TranslationProviderRegistry.IsRegistered(profile.Provider, registeredProviderIds)) continue;
            if (!IsHttpEndpoint(profile.Endpoint)) continue;
            retained.Add(profile);
        }

        return retained;
    }

    /// <summary>
    /// Profile a route must use when the service it was saved with is gone: the
    /// first profile this build can serve, preferring a real service over the
    /// offline echo profile, so a removed DeepL selection degrades to whatever
    /// the user still has configured. Null when nothing can be served - the
    /// caller then falls back to the built-in default profile.
    /// </summary>
    public static TranslationProfileRecord? SelectFallback(
        IEnumerable<TranslationProfileRecord>? profiles,
        IReadOnlyCollection<string> registeredProviderIds)
    {
        var servable = RetainServable(profiles, registeredProviderIds);
        return servable.FirstOrDefault(profile => !IsOfflineTestProfile(profile)) ?? servable.FirstOrDefault();
    }

    /// <summary>The echo profile is a diagnostic; a configured service wins over it.</summary>
    private static bool IsOfflineTestProfile(TranslationProfileRecord profile) =>
        string.Equals(profile.Provider.Trim(), EchoTranslationProvider.ProviderId, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
