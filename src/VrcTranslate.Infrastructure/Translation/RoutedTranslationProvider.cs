using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>Dispatches a route to the provider selected in its profile.</summary>
public sealed class RoutedTranslationProvider : ITranslationProvider
{
    private readonly TranslationProviderCatalog _catalog;

    public RoutedTranslationProvider(TranslationProviderCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Id => "routed";

    public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var providerId = NormalizeProviderId(request.ProviderId);
        return _catalog.Get(providerId).TranslateAsync(request with { ProviderId = providerId }, cancellationToken);
    }

    /// <summary>
    /// Ids written by earlier builds are resolved in one place: the
    /// OpenAI-compatible spellings become DeepSeek, and the ids of the providers
    /// this build retired (DeepL, the free Google endpoint, Google Cloud) fall
    /// back to the default provider, so a route saved before the removal still
    /// dispatches instead of failing the catalog lookup. A genuinely unknown id
    /// is left untouched and stays a lookup failure, because a typo must be
    /// visible rather than silently served by another service.
    /// </summary>
    private static string NormalizeProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return TranslationProviderRegistry.ResolveForRouting(providerId);
    }
}

/// <summary>
/// Keeps a legacy provider visible in migrated profiles until its native V2
/// adapter is installed. It fails at the operation boundary with an actionable
/// message instead of silently replacing the user's selected service.
/// </summary>
public sealed class UnavailableTranslationProvider : ITranslationProvider
{
    private readonly string _displayName;

    public UnavailableTranslationProvider(string id, string displayName)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Provider id is required.", nameof(id)) : id.Trim();
        _displayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
    }

    public string Id { get; }

    public Task<TranslationProviderResponse> TranslateAsync(TranslationProviderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new InvalidOperationException($"{_displayName}适配器正在迁移中，请先选择已可用的服务或完成该服务的配置。");
    }
}
