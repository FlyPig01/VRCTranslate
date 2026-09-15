namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// The one place that knows which translation providers this build ships and how
/// the provider ids written by earlier builds resolve against them.
/// <para>
/// DeepL, the free Google endpoint and Google Cloud were removed from the
/// product. Their ids therefore stay known as <em>retired</em>: a saved profile
/// or route that still names one of them has to degrade to a provider this build
/// can serve instead of failing the configuration load or the route lookup.
/// </para>
/// </summary>
public static class TranslationProviderRegistry
{
    /// <summary>Provider a configuration falls back to when the saved one is gone.</summary>
    public const string DefaultProviderId = "deepseek";

    /// <summary>Provider ids this build registers, in registration order.</summary>
    public static IReadOnlyList<string> ShippedProviderIds { get; } =
        [EchoTranslationProvider.ProviderId, DefaultProviderId, "tencent", "aliyun"];

    /// <summary>Provider ids earlier builds shipped and this build removed.</summary>
    public static IReadOnlyList<string> RetiredProviderIds { get; } =
        ["deepl", "google-free", "google-cloud"];

    /// <summary>
    /// Builds the shipped adapters. The desktop composition root and the tests
    /// share this factory, so "what this build offers" has one definition.
    /// </summary>
    public static IReadOnlyList<ITranslationProvider> CreateShippedProviders() =>
    [
        new EchoTranslationProvider(),
        new DeepSeekTranslationProvider(),
        new TencentTranslationProvider(),
        new AliyunTranslationProvider()
    ];

    /// <summary>
    /// Stable id for a provider reference, including the spellings earlier builds
    /// wrote, or null when this build cannot serve the reference. Retired ids and
    /// unknown ids both return null: neither may be dispatched silently.
    /// </summary>
    public static string? TryResolveId(string? configuredProvider)
    {
        if (string.IsNullOrWhiteSpace(configuredProvider)) return null;
        var normalized = configuredProvider.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(normalized, out var alias) ? alias
            : ShippedProviderIds.Contains(normalized, StringComparer.Ordinal) ? normalized
            : null;
    }

    /// <summary>True when the reference names a provider this build retired.</summary>
    public static bool IsRetired(string? configuredProvider) =>
        !string.IsNullOrWhiteSpace(configuredProvider) &&
        RetiredSpellings.Contains(configuredProvider.Trim().ToLowerInvariant());

    /// <summary>
    /// Whether a saved provider id can still be served, matched against the ids
    /// the host actually registered. A removed provider fails this test no matter
    /// which spelling the old configuration used.
    /// </summary>
    public static bool IsRegistered(string? configuredProvider, IReadOnlyCollection<string> registeredProviderIds)
    {
        ArgumentNullException.ThrowIfNull(registeredProviderIds);
        return !string.IsNullOrWhiteSpace(configuredProvider) &&
               registeredProviderIds.Contains(configuredProvider.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Id a route is dispatched with. Aliases resolve to the provider that
    /// replaced them, and a retired id resolves to <see cref="DefaultProviderId"/>
    /// so a route saved before the removal still dispatches instead of failing
    /// the catalog lookup with "provider not registered". A genuinely unknown id
    /// is returned unchanged: a typo has to stay a visible lookup failure.
    /// </summary>
    public static string ResolveForRouting(string configuredProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredProvider);
        return TryResolveId(configuredProvider)
            ?? (IsRetired(configuredProvider) ? DefaultProviderId : configuredProvider.Trim());
    }

    /// <summary>
    /// Spellings this build retired. The dash and underscore forms come from
    /// configurations saved before the ids were normalized.
    /// </summary>
    private static readonly HashSet<string> RetiredSpellings = new(StringComparer.Ordinal)
    {
        "deepl",
        "deep-l",
        "google-free",
        "google_free",
        "google-cloud",
        "google_cloud"
    };

    /// <summary>
    /// Spellings that still resolve. The OpenAI-compatible spellings are how
    /// earlier builds reached DeepSeek, so their saved profiles keep working.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["openai_compatible"] = DefaultProviderId,
        ["openai-compatible"] = DefaultProviderId,
        ["multimodal_openai"] = DefaultProviderId,
        ["multimodal-openai"] = DefaultProviderId,
        ["aliyun_nls"] = "aliyun"
    };
}
