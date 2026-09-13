namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Resolves providers by stable configuration id. Registration is deliberately
/// explicit so hosts can compose providers with their own dependency injection.
/// </summary>
public sealed class TranslationProviderCatalog
{
    private readonly IReadOnlyDictionary<string, ITranslationProvider> _providers;

    public TranslationProviderCatalog(IEnumerable<ITranslationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, ITranslationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!map.TryAdd(provider.Id, provider))
            {
                throw new ArgumentException($"A translation provider with id '{provider.Id}' is already registered.", nameof(providers));
            }
        }

        _providers = map;
    }

    public IReadOnlyCollection<string> Ids => _providers.Keys.ToArray();

    public bool IsRegistered(string id) => !string.IsNullOrWhiteSpace(id) && _providers.ContainsKey(id);

    public ITranslationProvider Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Translation provider '{id}' is not registered.");
    }

    public bool TryGet(string id, out ITranslationProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            provider = null;
            return false;
        }

        return _providers.TryGetValue(id, out provider);
    }
}
