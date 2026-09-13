using VrcTranslate.Application.Abstractions;
using VrcTranslate.Infrastructure.Logging;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Thin orchestration service used by hosts and tests. It keeps provider lookup,
/// cancellation, and diagnostic logging in one place without knowing a provider's
/// wire protocol.
/// </summary>
public sealed class TranslationService
{
    private readonly TranslationProviderCatalog _catalog;
    private readonly IStructuredLogger? _logger;

    public TranslationService(TranslationProviderCatalog catalog, IStructuredLogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;
    }

    public async Task<TranslationProviderResponse> TranslateAsync(
        string providerId,
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);

        var provider = _catalog.Get(providerId);
        _logger?.Trace("Translation request started", new Dictionary<string, object?>
        {
            ["provider"] = provider.Id,
            ["targetLanguage"] = request.TargetLanguage,
            ["correlationId"] = request.CorrelationId
        });

        try
        {
            var result = await provider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            _logger?.Info("Translation request completed", new Dictionary<string, object?>
            {
                ["provider"] = provider.Id,
                ["correlationId"] = request.CorrelationId
            });
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error("Translation request failed", new Dictionary<string, object?>
            {
                ["provider"] = provider.Id,
                ["correlationId"] = request.CorrelationId
            }, exception);
            throw;
        }
    }
}
