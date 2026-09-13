using VrcTranslate.Core.Translation;

namespace VrcTranslate.Application.Abstractions;

public sealed record TranslationProviderRequest(
    string Text,
    string TargetLanguage,
    string? SourceLanguage,
    string Model,
    Uri Endpoint,
    string CredentialReference,
    string CorrelationId,
    string ProviderId = "echo",
    string Region = "",
    IReadOnlyDictionary<string, string>? Options = null);

public sealed record TranslationProviderResponse(string TranslatedText, string? DetectedSourceLanguage = null);

public interface ITranslationProvider
{
    string Id { get; }

    Task<TranslationProviderResponse> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITranslationRouteStore
{
    TranslationRoute? GetCurrent();

    void SetCurrent(TranslationRoute route);
}
