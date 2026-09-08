using VrcTranslate.Application.Abstractions;

namespace VrcTranslate.Infrastructure.Translation;

/// <summary>
/// Deterministic provider used by the shell, diagnostics, and integration tests.
/// It does not contact a network service and returns the input unchanged.
/// </summary>
public sealed class EchoTranslationProvider : ITranslationProvider
{
    public const string ProviderId = "echo";

    public Task<TranslationProviderResponse> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new TranslationProviderResponse(request.Text, request.SourceLanguage));
    }

    public string Id => ProviderId;
}
